using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// The <see cref="IOperationRegistry"/> implementation: one lock over the in-memory state, an
/// immutable <see cref="OperationInfo"/> snapshot published on every transition. Ported from a
/// proven sibling app's process registry, reduced to mechanism (see the design doc's evidence
/// table for what stayed behind): id, owning module, app-defined kind/scope, status, progress,
/// timestamps, idempotent finish, bounded history — no queue, scheduler, retry, priority, or
/// phase model.
/// <para>
/// State is in-memory only and does not survive a restart — the source app deleted its own
/// persisted state file for good reason (finished history was purged at startup anyway).
/// </para>
/// </summary>
public sealed class OperationRegistry : IOperationRegistry, IDisposable
{
    private readonly IEventBus _bus;
    private readonly OperationRegistryOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Finished-entry ids in the order they finished, so pruning drops the OLDEST first. A
    /// separate structure from <see cref="_entries"/> because a <see cref="Dictionary{TKey,TValue}"/>
    /// makes no ordering guarantee and running entries must never be counted or touched here.
    /// </summary>
    private readonly LinkedList<string> _finishedOrder = new();

    private long _nextSequence;

    /// <summary>The only status <see cref="Report"/> and <see cref="Pause"/> accept.</summary>
    private static readonly OperationStatus[] ActiveOnly = [OperationStatus.Running];

    /// <summary>
    /// What <see cref="IOperation.Complete"/>/<see cref="IOperation.Fail(string, IReadOnlyDictionary{string, string}?, string?)"/>
    /// (via <see cref="OperationHandle"/>) and the public by-id <see cref="Cancel(string)"/> accept —
    /// a paused deploy can still complete, fail on a deadline, or be cancelled by an external client,
    /// exactly as a running one can (§5A.3).
    /// </summary>
    private static readonly OperationStatus[] ActiveOrPaused = [OperationStatus.Running, OperationStatus.Paused];

    /// <summary>
    /// The WAITING band (§5A.2) — what <see cref="RequestResume"/> and <see cref="Dismiss"/> accept.
    /// Never entered into <see cref="_finishedOrder"/>, so never pruned by <see cref="PruneHistory"/>
    /// or removed by <see cref="ClearFinished"/> — an offer is not history.
    /// </summary>
    private static readonly OperationStatus[] WaitingBand = [OperationStatus.Paused, OperationStatus.Interrupted];

    /// <summary>What <see cref="Resume"/> (the handle's own — not the by-id <see cref="RequestResume"/>) accepts.</summary>
    private static readonly OperationStatus[] PausedOnly = [OperationStatus.Paused];

    /// <summary>
    /// The three finish-outcomes. Everything else is a band this registry must give an exit from
    /// (§5A.1). <c>internal</c>, not <c>private</c> (hardening, this batch's review) — the test project
    /// sees internals (<c>src/Directory.Build.props</c>' <c>InternalsVisibleTo("Shenora.Tests")</c>),
    /// so <c>OperationLifecycleInvariantTests</c> calls THIS method directly instead of hand-copying its
    /// own terminal check: a status classified terminal here but missed in a second hand-copy could
    /// otherwise let the invariant sweep silently skip it.
    /// </summary>
    internal static bool IsTerminal(OperationStatus status) =>
        status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    /// <summary>
    /// ANY non-terminal status — what the owner-path terminal cancel (<see cref="CancelTerminal"/>)
    /// accepts (§5A.2/§5A.3): a <see cref="OperationStatus.Paused"/> body is still parked on its own
    /// token and must be able to unwind the same way a <see cref="OperationStatus.Running"/> one does.
    /// DERIVED from the live enum via <see cref="IsTerminal"/> (hardening, this batch's review) — this
    /// is the one file whose thesis is "don't hand-maintain a status set"; a hand-copied array here
    /// would leave a future status added tomorrow silently EXCLUDED, so <c>CancelTerminal</c> would
    /// signal the token and then refuse the flip — the token cancelled, the entry stranded in the new
    /// state, which is exactly the class of bug this whole feature exists to close.
    /// </summary>
    private static readonly OperationStatus[] NonTerminal =
        Enum.GetValues<OperationStatus>().Where(s => !IsTerminal(s)).ToArray();

    /// <summary>Options are validated NOW, not on first use, so a bad value names itself at the call site.</summary>
    public OperationRegistry(IEventBus bus, OperationRegistryOptions? options = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _options = options ?? new OperationRegistryOptions();

        if (_options.MaxHistory < 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(OperationRegistryOptions.MaxHistory)} must be at least 0.");
        if (_options.ProgressInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(OperationRegistryOptions.ProgressInterval)} must not be negative.");
    }

    /// <inheritdoc />
    public IOperation Start(string module, OperationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(options);

        var cts = new CancellationTokenSource();
        var entry = new Entry
        {
            Id = Guid.NewGuid().ToString(),
            Module = module,
            Kind = options.Kind,
            Scope = options.Scope,
            Status = OperationStatus.Running,
            Progress = ClampProgress(options.Progress),
            Title = options.Title,
            Cancellable = options.Cancellable,
            Resumable = options.Resumable,
            ResumePayload = options.ResumePayload,
            StartedAt = _options.TimeProvider.GetUtcNow(),
            Cts = cts,
        };

        lock (_lock)
        {
            entry.Sequence = _nextSequence++;
            _entries[entry.Id] = entry;
        }

        Publish(entry, immediate: true);
        return new OperationHandle(this, entry.Id, cts.Token);
    }

    /// <inheritdoc />
    public string Run(string module, OperationOptions options, Func<IOperation, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var operation = Start(module, options);   // validates module/options; publishes the Running snapshot
        _ = Task.Run(async () =>
        {
            try
            {
                // ConfigureAwait(false) is REQUIRED here and BANNED in the dispatch path (see
                // ipc-contracts.md): this body is deliberately NOT the dispatch path — capturing the
                // caller's synchronization context would put the work back on the thread this
                // handoff exists to free.
                await work(operation, operation.CancellationToken).ConfigureAwait(false);
                // Complete ONLY when still Running (IMPORTANT 2, this batch's review): Complete()
                // itself accepts ActiveOrPaused (a paused deploy CAN still complete — see its own
                // doc), so this unconditional call used to stamp Completed on a body that did the
                // spec's own headline move — op.Pause("dns"); return; — and simply returned instead
                // of throwing. §5A.2 exists because "keep it Running" and "Fail it" are both lies for
                // a paused-but-not-crashed run; silently completing it here would have been a THIRD
                // lie, introduced by the very feature meant to remove the other two. A body that
                // paused and returned leaves the operation Paused with no live body — resuming it is
                // the app's own job (via the SAME handle's IOperation.Resume, or the app's own
                // checkpoint/restart path), same shape as a crash offer (see IModuleContext.Run's doc).
                if (PeekStatus(operation.Id) == OperationStatus.Running)
                    operation.Complete();
            }
            // NOT operation.Cancel() routed through the CLIENT-permission-checked Cancel(id) (see
            // that method's own doc) — the body itself just ended in cancellation, which is data
            // loss to refuse, not a permission question. CancelTerminal is the same path
            // OperationHandle.Cancel() uses for exactly this reason (Finding 1, whole-branch review):
            // a non-Cancellable operation whose body threw OperationCanceledException (an HttpClient
            // timeout, a linked shutdown token, TaskCanceledException derives from this) used to be
            // refused here and left stranded Running forever — no terminal transition, no
            // OPERATION_UPDATED, and never evictable by ClearFinished since it never reached
            // _finishedOrder.
            catch (OperationCanceledException) { CancelTerminal(operation.Id, "Run"); }
            catch (OperationException expected) { operation.Fail(expected); }
            catch (Exception ex)
            {
                // The boundary rule, identical to MessageDispatcher's: the app never sees the raw
                // message. No ILogger on this type (the registry is transport/UI agnostic) — route
                // the detail through the same guarded/lazy Log() every other diagnostic here uses,
                // not a second logging path.
                Log(() => $"[Shenora.Ipc] Run: operation {operation.Id} ({options.Kind} in {module}) " +
                    $"failed with {ex.GetType().Name}: {ex.Message}");
                operation.Fail(IpcErrorCodes.UnknownError,
                    new Dictionary<string, string> { ["exceptionType"] = ex.GetType().Name });
            }
        });
        return operation.Id;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sorted by the THREE BANDS (§5A.2), not "Running vs everything else" (coordinator ruling, this
    /// batch — a real defect, not a style nit): Active (<see cref="OperationStatus.Running"/>, oldest
    /// first) → Waiting (<see cref="OperationStatus.Paused"/>/<see cref="OperationStatus.Interrupted"/>,
    /// oldest first) → Terminal (newest FINISHED first, tiebroken by newest SEQUENCE — a history/log
    /// view surfaces the most recent outcome first; the sequence tiebreak matters because
    /// <c>TimeProvider.System</c> has ~15.6 ms granularity on Windows, so two same-tick finishes would
    /// otherwise fall back to dictionary enumeration order, which reshuffles on unrelated churn —
    /// IMPORTANT 3, this batch's review). Before this fix, a <see cref="OperationStatus.Paused"/> entry
    /// fell into the "everything else" bucket alongside completed history with no band of its own —
    /// burying the exact row a user needs to find in order to resume or dismiss it, precisely the
    /// reason the Waiting band exists (pinned by <c>OperationRegistryTests.GetAll_orders_active_then_waiting_then_terminal</c>,
    /// <c>…_orders_terminal_entries_newest_finished_first</c>, and
    /// <c>…_breaks_a_terminal_tie_on_finishedAt_by_sequence_not_enumeration_order</c>).
    /// </remarks>
    public IReadOnlyList<OperationInfo> GetAll(string? module = null, string? scope = null)
    {
        lock (_lock)
        {
            var filtered = _entries.Values
                // Scope follows the SAME rule as IEventBus (Finding 4, whole-branch review), not
                // strict equality: no requested scope = every scope, AND an unscoped (global) entry
                // matches any requested scope too. Both event buses (Shenora.Core.EventBus, the TS
                // ShenoraEventBus) already apply this — a scope-less event still reaches scoped
                // subscribers — so a scoped GetAll snapshot that excluded unscoped entries disagreed
                // with the deltas a scoped store folds afterward: it never SAW an unscoped operation
                // in LIST but DID receive its OPERATION_UPDATED deltas, so its contents depended on
                // mount order relative to when the work started.
                .Where(e => (module is null || string.Equals(e.Module, module, StringComparison.Ordinal))
                         && (scope is null || e.Scope is null || string.Equals(e.Scope, scope, StringComparison.Ordinal)))
                .ToList();

            var active = filtered.Where(e => e.Status == OperationStatus.Running)
                .OrderBy(e => e.Sequence);
            var waiting = filtered.Where(e => e.Status is OperationStatus.Paused or OperationStatus.Interrupted)
                .OrderBy(e => e.Sequence);
            // ThenByDescending(Sequence) is the DETERMINISTIC tiebreak (IMPORTANT 3, this batch's
            // review) — TimeProvider.System has ~15.6 ms granularity on Windows, so two operations
            // finishing within the same tick tie on FinishedAt alone, and LINQ's stable sort then
            // falls back to the PRE-SORT (dictionary enumeration) order, which reshuffles after any
            // unrelated removal/insert. Sequence is a strictly monotonic counter that never repeats,
            // so the tie always breaks the same way regardless of _entries' internal layout.
            var terminal = filtered.Where(e => IsTerminal(e.Status))
                .OrderByDescending(e => e.FinishedAt)
                .ThenByDescending(e => e.Sequence);

            return active.Concat(waiting).Concat(terminal).Select(ToInfo).ToList();
        }
    }

    /// <inheritdoc />
    public bool Cancel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        CancellationTokenSource? cts;
        string? miss;
        lock (_lock)
        {
            // ActiveOrPaused (Finding 2, this batch, §5A.3): a paused operation is exactly as
            // cancellable as a running one — nothing about being paused changes whether an external
            // client has standing to stop it.
            miss = Validate(id, ActiveOrPaused, out var entry);
            if (miss is null && !entry!.Cancellable)
            {
                // The honest CANCEL contract (Task 5, carried from the Task 2 review): Start()
                // allocates a CTS for EVERY operation, cancellable or not, so a CTS is not what a
                // non-cancellable operation lacks. What Cancellable actually gates is THIS call —
                // Cancel() is the only path that ever signals that token, so an operation that opted
                // OUT simply never has it signalled. Flipping the status to Cancelled here anyway
                // would lie to the UI while the body keeps running to its own Complete()/Fail() (which
                // then no-ops, since the entry would already be terminal). Same "ignored" path as an
                // unknown/already-terminal id, just a different reason: this operation was simply
                // never cancellable.
                miss = "is not cancellable (OperationOptions.Cancellable was false)";
            }
            cts = miss is null ? entry!.Cts : null; // read under the lock — Finish()/Dispose() may dispose it concurrently otherwise
        }

        if (miss is not null)
        {
            LogIgnored("Cancel", id, miss);
            return false;
        }

        // The HONEST return (hardening, this batch's review — same fix as Dismiss below): report
        // whatever Finish's OWN re-validation (under a freshly re-acquired lock) actually decided,
        // rather than assuming success just because THIS check passed. A concurrent transition
        // between releasing this lock and Finish's own lock (e.g. another caller's Complete/Fail/
        // Cancel racing in) can make the id no longer eligible by the time Finish re-checks it.
        return CancelTokenThenFinish(cts, id, "Cancel", ActiveOrPaused);
    }

    /// <summary>
    /// The BODY ended in cancellation — used by <see cref="OperationHandle.Cancel"/> (the operation's
    /// OWN owner, holding the handle directly — never an arbitrary by-id caller) and by
    /// <see cref="Run"/>'s catch when the background work itself throws
    /// <see cref="OperationCanceledException"/>. Deliberately UNCONDITIONAL, unlike the public by-id
    /// <see cref="Cancel(string)"/>: that method answers "may an external CLIENT request stop this
    /// operation", and rightly refuses when <see cref="OperationOptions.Cancellable"/> opted out. This
    /// method answers a different question — "the work is over, record that" — which is not a
    /// permission decision at all. Refusing it is data loss, not honesty: the entry would stay
    /// <see cref="OperationStatus.Running"/> forever, never evictable by <see cref="ClearFinished"/>
    /// (it never reaches <see cref="_finishedOrder"/>) and its CTS never disposed (Finding 1,
    /// whole-branch review — reachable on the DEFAULT <c>Cancellable = false</c>, since
    /// <see cref="TaskCanceledException"/> derives from <see cref="OperationCanceledException"/>: an
    /// <c>HttpClient</c> timeout or a plain <c>ct.ThrowIfCancellationRequested()</c> in the body both
    /// land here). Still idempotent through <see cref="Finish"/>: a no-op for an unknown or
    /// already-terminal id — no separate "already terminal" branch is needed here for that reason.
    /// </summary>
    private void CancelTerminal(string id, string caller)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            _entries.TryGetValue(id, out var entry);
            // ANY non-terminal status, not only Running (§5A.2/§5A.3, this batch): a Paused body is
            // still parked on this same token via IOperation.Pause and must unwind the same way.
            cts = entry is not null && !IsTerminal(entry.Status) ? entry.Cts : null;
        }

        CancelTokenThenFinish(cts, id, caller, NonTerminal);
    }

    /// <summary>
    /// Shared tail of <see cref="Cancel(string)"/> and <see cref="CancelTerminal"/>: signal the token,
    /// then transition to <see cref="OperationStatus.Cancelled"/> through the one terminal path
    /// (<see cref="Finish"/>).
    /// <para>
    /// Cancel the token BEFORE the status flip: a body observing the token sees the cancellation
    /// rather than racing a completed-then-cancelled transition. Deliberately OUTSIDE the lock:
    /// <see cref="CancellationTokenSource.Cancel()"/> runs registered callbacks synchronously, and a
    /// callback that re-enters the registry (e.g. observes the token and calls Report/Complete on the
    /// SAME thread) would deadlock re-acquiring <see cref="_lock"/> if it were still held here. Do NOT
    /// move this inside the lock.
    /// </para>
    /// <para>
    /// Because it runs outside the lock, <paramref name="cts"/> can legitimately already be disposed
    /// by a CONCURRENT <see cref="Finish"/> (another caller completed/failed/cancelled the same
    /// operation first) or by <see cref="Dispose"/> (the registry is being torn down) between the read
    /// that produced it and this call — <see cref="CancellationTokenSource.Cancel()"/> on an
    /// already-disposed instance throws <see cref="ObjectDisposedException"/>. That is not a bug to
    /// propagate: it means the operation is already finished (or the registry is gone), so THIS
    /// call's own <see cref="Finish"/> below will correctly no-op and log the miss. Swallow it —
    /// proven the same way in the harvested source app.
    /// </para>
    /// <para>
    /// Returns whatever <see cref="Finish"/> actually decided (hardening, this batch's review) — NOT
    /// an assumed <c>true</c> — because the caller's own permission check ran under a DIFFERENT lock
    /// acquisition than <see cref="Finish"/>'s re-validation, and a concurrent transition in that
    /// window (e.g. <see cref="Resume"/> flipping a <see cref="OperationStatus.Paused"/> entry back to
    /// <see cref="OperationStatus.Running"/> between <see cref="Dismiss"/>'s own check and this call)
    /// must not be reported to the client as a successful transition that did not actually happen.
    /// </para>
    /// </summary>
    private bool CancelTokenThenFinish(CancellationTokenSource? cts, string id, string caller,
        IReadOnlyCollection<OperationStatus> allowedStatuses)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a concurrent Finish()/Dispose() — see the comment above.
        }

        return Finish(id, OperationStatus.Cancelled, null, caller, allowedStatuses);
    }

    /// <inheritdoc />
    public void ClearFinished()
    {
        lock (_lock)
        {
            foreach (var id in _finishedOrder)
                _entries.Remove(id);
            _finishedOrder.Clear();
        }
    }

    /// <inheritdoc />
    public bool Dismiss(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        CancellationTokenSource? cts;
        string? miss;
        lock (_lock)
        {
            // WaitingBand (§5A.2/§5A.3, this batch): Paused or Interrupted only — Dismiss REFUSES
            // Running on purpose. Declining a pending offer and cancelling LIVE work are different
            // acts; letting this accept Running too would be the exact conflation (Cancel accepting
            // states it should not) that produced this branch's only Critical.
            miss = Validate(id, WaitingBand, out var entry);
            cts = miss is null ? entry!.Cts : null; // Paused has one; Interrupted never does (RegisterInterrupted) — read under the lock, same discipline as Cancel(id).
        }

        if (miss is not null)
        {
            LogIgnored("Dismiss", id, miss);
            return false;
        }

        // Signal the token FIRST (same order as Cancel/CancelTerminal): a Paused body still parked on
        // it unwinds rather than racing a completed-then-cancelled flip. Interrupted's cts is null, so
        // this is a no-op for that case — see CancelTokenThenFinish's own null-conditional call.
        //
        // The HONEST return (hardening, this batch's review): a concurrent Resume() between THIS
        // lock's release and Finish's own re-validation can flip a Paused entry back to Running before
        // Finish ever runs — Finish then correctly refuses the transition, and this must report that
        // refusal rather than the unconditional `true` it used to return regardless of the outcome
        // (which would leave a live, un-cancelled operation reported as successfully dismissed).
        return CancelTokenThenFinish(cts, id, "Dismiss", WaitingBand);
    }

    /// <inheritdoc />
    public string RegisterInterrupted(string module, OperationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(options);
        // A silently-accepted unusable entry is worse than a loud rejection: nobody could ever
        // resume this, so it would sit as a dead offer with no way for the app to act on it.
        if (!options.Resumable)
            throw new ArgumentException(
                $"Registering an interrupted operation requires {nameof(OperationOptions.Resumable)} " +
                "to be true — the kit only offers a resume the app itself marked resumable.",
                nameof(options));
        if (string.IsNullOrEmpty(options.ResumePayload))
            throw new ArgumentException(
                $"Registering an interrupted operation requires a non-empty " +
                $"{nameof(OperationOptions.ResumePayload)} — it is the opaque checkpoint token the " +
                "app resumes from.",
                nameof(options));

        Entry entry;
        var isNew = false;
        lock (_lock)
        {
            // Dedupe on (module, kind, resumePayload) among already-Interrupted entries: a
            // profile/session switch re-announces the SAME checkpoint, and that must return the
            // existing offer rather than stack a second one for what is still the same interrupted
            // operation.
            var existing = _entries.Values.FirstOrDefault(e =>
                e.Status == OperationStatus.Interrupted
                && string.Equals(e.Module, module, StringComparison.Ordinal)
                && string.Equals(e.Kind, options.Kind, StringComparison.Ordinal)
                && string.Equals(e.ResumePayload, options.ResumePayload, StringComparison.Ordinal));

            if (existing is not null)
            {
                entry = existing;
            }
            else
            {
                entry = new Entry
                {
                    Id = Guid.NewGuid().ToString(),
                    Module = module,
                    Kind = options.Kind,
                    Scope = options.Scope,
                    Status = OperationStatus.Interrupted,
                    Progress = ClampProgress(options.Progress),
                    Title = options.Title,
                    Cancellable = options.Cancellable,
                    Resumable = options.Resumable,
                    ResumePayload = options.ResumePayload,
                    StartedAt = _options.TimeProvider.GetUtcNow(),
                    // No CTS: an interrupted entry is not running work, just a pending offer —
                    // there is nothing to cancel until the app's own resume restarts it as a fresh
                    // Start()/Run(), which allocates its own.
                    Cts = null,
                };
                entry.Sequence = _nextSequence++;
                _entries[entry.Id] = entry;
                // Deliberately NEVER added to _finishedOrder. That list is PruneHistory's eviction
                // queue for TERMINAL history, and Interrupted is not terminal (see OperationStatus) —
                // it is a pending offer that only RequestResume removes. Adding it here would let an
                // unrelated flood of finished operations silently evict a crash offer the app has not
                // yet had a chance to show the user; this is the structural half of that guard (see
                // also PruneHistory's own note).
                isNew = true;
            }
        }

        if (isNew) Publish(entry, immediate: true);
        return entry.Id;
    }

    /// <inheritdoc />
    public bool RequestResume(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Entry? entry;
        string? miss;
        OperationStatus status = default;
        lock (_lock)
        {
            // WaitingBand (§5A.4, this batch): Paused OR Interrupted. The asymmetry lives HERE, not in
            // Validate — only the Interrupted case is removed.
            miss = Validate(id, WaitingBand, out entry);
            if (miss is null)
            {
                status = entry!.Status;
                if (status == OperationStatus.Interrupted)
                {
                    // No live handle to flip: the body died with the process, and the resumed
                    // operation registers a FRESH one via Start/Run when it actually restarts. This
                    // is why the RESUME_REQUESTED payload below carries `status` — a handler cannot
                    // look this entry up afterward to tell the two cases apart.
                    _entries.Remove(id);
                }
                // Paused: deliberately LEFT IN PLACE — the app's own IOperation.Resume() flips it once
                // it has ACTUALLY resumed. The client asking is not the state changing (§5A.4) — the
                // same split that fixed this branch's only Critical (Cancel vs Dismiss, §5A.3).
            }
        }

        if (miss is not null)
        {
            LogIgnored("RequestResume", id, miss);
            return false;
        }

        // Outside the lock, same discipline as every other bus emission here: nothing calls out to
        // app code while holding _lock.
        _bus.Emit(_options.ModuleName, OperationEvents.ResumeRequested, new
        {
            operationId = entry!.Id,
            module = entry.Module,
            kind = entry.Kind,
            resumePayload = entry.ResumePayload,
            scope = entry.Scope,
            status,   // lets a handler tell Paused (still there) apart from Interrupted (already gone)
        }, entry.Scope);

        return true;
    }

    /// <summary>
    /// Called by an <see cref="OperationHandle"/>. Requires <see cref="OperationStatus.Running"/> —
    /// NOT <see cref="OperationStatus.Paused"/> too (§5A.3): a paused operation is not progressing, and
    /// letting progress tick while paused is how a UI ends up showing motion for work that is stopped.
    /// </summary>
    private void Report(string id, int? progress, OperationLabel? detail)
    {
        Entry? entry;
        string? miss;
        lock (_lock)
        {
            miss = Validate(id, ActiveOnly, out entry);
            if (miss is null)
            {
                if (progress.HasValue) entry!.Progress = ClampProgress(progress);
                if (detail is not null) entry!.Detail = detail;
            }
        }

        if (miss is not null)
        {
            LogIgnored("Report", id, miss);
            return;
        }

        Publish(entry!, immediate: false);
    }

    /// <summary>
    /// Pause: <see cref="OperationStatus.Running"/> → <see cref="OperationStatus.Paused"/> (§5A.3).
    /// Called by an <see cref="OperationHandle"/>. A lifecycle transition — emits immediately, never
    /// throttled, same as every other band change.
    /// </summary>
    private void Pause(string id, string reason, OperationLabel? detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Entry? entry;
        string? miss;
        lock (_lock)
        {
            miss = Validate(id, ActiveOnly, out entry);
            if (miss is null)
            {
                entry!.Status = OperationStatus.Paused;
                entry.PauseReason = reason;
                if (detail is not null) entry.Detail = detail;
            }
        }

        if (miss is not null)
        {
            LogIgnored("Pause", id, miss);
            return;
        }

        Publish(entry!, immediate: true);
    }

    /// <summary>
    /// Resume: <see cref="OperationStatus.Paused"/> → <see cref="OperationStatus.Running"/>, clearing
    /// <see cref="Entry.PauseReason"/> (§5A.3). Called by an <see cref="OperationHandle"/> — distinct
    /// from the by-id <see cref="RequestResume"/>, which only ASKS (§5A.4).
    /// </summary>
    private void Resume(string id)
    {
        Entry? entry;
        string? miss;
        lock (_lock)
        {
            miss = Validate(id, PausedOnly, out entry);
            if (miss is null)
            {
                entry!.Status = OperationStatus.Running;
                entry.PauseReason = null;
            }
        }

        if (miss is not null)
        {
            LogIgnored("Resume", id, miss);
            return;
        }

        Publish(entry!, immediate: true);
    }

    /// <summary>
    /// The one terminal transition every finish (Complete/Fail/Cancel) goes through. Idempotent:
    /// a second call for an already-terminal (or unknown) id is a safe no-op — this is what makes
    /// the "Complete at the end + Fail in the catch" pattern safe.
    /// </summary>
    /// <param name="id">The operation id.</param>
    /// <param name="status">The terminal status to transition to.</param>
    /// <param name="error">The structured failure, when <paramref name="status"/> is <see cref="OperationStatus.Failed"/>; otherwise null.</param>
    /// <param name="caller">The public API this came from (<c>"Complete"</c>/<c>"Fail"</c>/<c>"Cancel"</c>/<c>"Dismiss"</c>/<c>"Run"</c>) — only for the miss diagnostic.</param>
    /// <param name="allowedStatuses">
    /// What this particular transition accepts (Task: rework Validate, this batch) — different
    /// callers legitimately accept different bands: <c>Complete</c>/<c>Fail</c> accept
    /// <see cref="ActiveOrPaused"/> (a paused deploy can still fail on a deadline), the public by-id
    /// <c>Cancel</c> accepts <see cref="ActiveOrPaused"/>, the owner-path terminal cancel accepts
    /// <see cref="NonTerminal"/>, and <c>Dismiss</c> accepts <see cref="WaitingBand"/>.
    /// </param>
    /// <returns>
    /// Whether the transition actually happened (hardening, this batch's review) — <c>false</c> for an
    /// unknown id or one whose CURRENT status is not in <paramref name="allowedStatuses"/>. Every
    /// caller that itself answers a client (the public by-id <see cref="Cancel(string)"/>,
    /// <see cref="Dismiss"/>) MUST propagate this rather than assuming success, because their own
    /// permission check ran under a separate, earlier lock acquisition than this one — a concurrent
    /// transition in between (see <see cref="CancelTokenThenFinish"/>'s own doc) can make this call
    /// the one that actually decides the outcome.
    /// </returns>
    private bool Finish(string id, OperationStatus status, IpcError? error, string caller,
        IReadOnlyCollection<OperationStatus> allowedStatuses)
    {
        Entry? entry;
        string? miss;
        lock (_lock)
        {
            miss = Validate(id, allowedStatuses, out entry);
            if (miss is null)
            {
                entry!.Status = status;
                entry.Error = error;
                entry.FinishedAt = _options.TimeProvider.GetUtcNow();
                if (status == OperationStatus.Completed) entry.Progress = 100;
                entry.Cts?.Dispose();
                entry.Cts = null;

                _finishedOrder.AddLast(id);
                PruneHistory();
            }
        }

        if (miss is not null)
        {
            LogIgnored(caller, id, miss);
            return false;
        }

        Publish(entry!, immediate: true);
        return true;
    }

    /// <summary>
    /// Look up <paramref name="id"/>: null = found and in one of <paramref name="allowedStatuses"/>
    /// (proceed), <paramref name="entry"/> is set; otherwise the diagnostic reason to log for why the
    /// caller's id was ignored. MUST be called while holding <see cref="_lock"/> — it reads
    /// <see cref="_entries"/> directly.
    /// <para>
    /// Rework (this batch, §5A.1): different callers legitimately accept different statuses — a single
    /// hard-coded <c>Status == Running</c> check here is exactly what left <see cref="OperationStatus.Interrupted"/>
    /// with no sanctioned exit (every transition refused it, because none of them was allowed to accept
    /// anything but <see cref="OperationStatus.Running"/>). Every call site now states what IT accepts.
    /// </para>
    /// <para>
    /// The "ignored" reason is now HONEST about terminal vs. non-terminal (this batch): the old message
    /// unconditionally said "has already reached a terminal state (Interrupted)" for an
    /// <see cref="OperationStatus.Interrupted"/> id passed to a transition that does not accept it —
    /// which is false, <see cref="OperationStatus.Interrupted"/> is explicitly NOT terminal (see its own
    /// doc). Only an actually-terminal status gets that wording; anything else (a status that merely
    /// isn't in <paramref name="allowedStatuses"/>) gets a status-naming message instead.
    /// </para>
    /// </summary>
    private string? Validate(string id, IReadOnlyCollection<OperationStatus> allowedStatuses, out Entry? entry)
    {
        if (!_entries.TryGetValue(id, out entry))
            return "is not known to this registry (a stale id usually means the caller kept a handle past the operation's life)";
        if (allowedStatuses.Contains(entry.Status)) return null;
        return IsTerminal(entry.Status)
            ? $"has already reached a terminal state ({entry.Status})"
            : $"is currently {entry.Status}, which does not accept this transition";
    }

    /// <summary>
    /// A read-only status peek for <see cref="Run"/>'s tail (IMPORTANT 2, this batch's review) — NOT
    /// a <c>Validate</c> variant, because it makes no decision and mutates nothing: it exists only so
    /// <c>Run</c> can ask "is this still <see cref="OperationStatus.Running"/>?" before deciding
    /// whether to call <see cref="IOperation.Complete"/>, without duplicating a dictionary lookup
    /// inline. Returns <c>null</c> for an unknown id (defensive; not expected on <c>Run</c>'s own path).
    /// </summary>
    private OperationStatus? PeekStatus(string id)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(id, out var entry) ? entry.Status : null;
        }
    }

    /// <summary>
    /// The one real job <see cref="OperationRegistryOptions.Log"/> has today: an id the registry
    /// does not know, or one already terminal, silently dropped otherwise. Never called while
    /// holding <see cref="_lock"/> — the sink is app code.
    /// </summary>
    private void LogIgnored(string caller, string id, string reason) =>
        Log(() => $"[Shenora.Ipc] {caller} ignored: operation '{id}' {reason}.");

    /// <summary>
    /// Guarded and lazy, matching <c>WebViewIpcBridge.Log</c>'s convention: build the message only
    /// when a sink is configured, and never let a throwing sink escape into the caller.
    /// </summary>
    private void Log(Func<string> message)
    {
        if (_options.Log is null) return;
        AppCallback.Run(() => _options.Log(message()));
    }

    /// <summary>
    /// Drop the oldest finished entries over <see cref="OperationRegistryOptions.MaxHistory"/>.
    /// Caller holds <see cref="_lock"/>. Only ever touches <see cref="_finishedOrder"/> — the WAITING
    /// band (§5A.2), <see cref="OperationStatus.Interrupted"/> (from <see cref="RegisterInterrupted"/>)
    /// and <see cref="OperationStatus.Paused"/> (from <see cref="Pause"/>), is never added to that list
    /// (an offer is not history), so it structurally cannot be evicted here regardless of how many
    /// other operations finish afterward — only <see cref="Dismiss"/> moves one out of this band.
    /// </summary>
    private void PruneHistory()
    {
        while (_finishedOrder.Count > _options.MaxHistory)
        {
            var oldest = _finishedOrder.First!.Value;
            _finishedOrder.RemoveFirst();
            _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// The one place a transition reaches the bus. <paramref name="immediate"/> distinguishes a
    /// lifecycle transition (start/terminal — always emits now, never throttled) from a progress
    /// report (<c>false</c> — collapsed to at most one emission per
    /// <see cref="OperationRegistryOptions.ProgressInterval"/> window, with a trailing emit so the
    /// final value in a window is never lost). <see cref="TimeSpan.Zero"/> disables the throttle:
    /// every window is immediately "closed", so this falls through to an immediate emit.
    /// </summary>
    private void Publish(Entry entry, bool immediate)
    {
        if (immediate) { EmitNow(entry); return; }

        var now = _options.TimeProvider.GetUtcNow();
        lock (_lock)
        {
            if (entry.Status != OperationStatus.Running) return;          // terminal already
            if (now - entry.LastEmitUtc < _options.ProgressInterval)
            {
                if (entry.TrailingScheduled) return;                       // one pending trailer, not N
                entry.TrailingScheduled = true;
                var delay = _options.ProgressInterval - (now - entry.LastEmitUtc);
                _ = TrailingEmitAsync(entry, delay);                       // fire-and-forget, guarded below
                return;
            }
            entry.LastEmitUtc = now;
        }
        EmitNow(entry);
    }

    /// <summary>
    /// The trailing half of the throttle: guarantees the LAST progress value in a window is never
    /// simply dropped (the stuck-at-80%-bar symptom). Guarded end to end — this is a
    /// fire-and-forget body (<see cref="Publish"/> does not await it), so an unguarded exception
    /// here would be an UNOBSERVED task exception rather than a caller-visible failure.
    /// </summary>
    private async Task TrailingEmitAsync(Entry entry, TimeSpan delay)
    {
        var shouldEmit = false;
        try
        {
            try
            {
                // Task.Delay's TimeProvider overload is what makes the FakeTimeProvider test deterministic —
                // a real 100 ms sleep in the suite would be both slow and flaky.
                await Task.Delay(delay, _options.TimeProvider).ConfigureAwait(false);
            }
            finally
            {
                // MUST run on EVERY exit from the await — success, cancellation, or a faulting
                // TimeProvider (TimeProvider is public, consumer-settable surface, so a faulting
                // custom CreateTimer is not purely academic). A `return` here would silently
                // swallow whatever exception is in flight, so this only ever sets state; the
                // exception (if any) keeps propagating to the catch below on its own.
                // Found in review: resetting the flag only on the success path let it stick at
                // `true` forever after a fault, silently muting every later Report on this
                // operation — the exact silent-drop failure class this throttle exists to remove.
                lock (_lock)
                {
                    entry.TrailingScheduled = false;
                    entry.LastEmitUtc = _options.TimeProvider.GetUtcNow();
                    shouldEmit = entry.Status == OperationStatus.Running; // a terminal emit already went
                }
            }

            if (shouldEmit) EmitNow(entry);   // unreachable when the await above faulted — see the finally
        }
        catch (Exception ex)
        {
            // An unguarded fire-and-forget body makes any fault an UNOBSERVED task exception.
            // Routed through the same guarded/lazy Log() every other diagnostic uses — not a
            // second logging path — so a throwing sink still cannot escape here either.
            Log(() => $"[Shenora.Ipc] trailing progress emit failed: {ex.GetType().Name}");
        }
    }

    private void EmitNow(Entry entry)
    {
        OperationInfo snapshot;
        lock (_lock) { snapshot = ToInfo(entry); }
        // Fire-and-forget by design: IEventBus.Emit guarantees a subscriber cannot fault the caller.
        _bus.Emit(_options.ModuleName, OperationEvents.Updated, snapshot, snapshot.Scope);
    }

    private static OperationInfo ToInfo(Entry entry) => new()
    {
        Id = entry.Id,
        Module = entry.Module,
        Kind = entry.Kind,
        Scope = entry.Scope,
        Status = entry.Status,
        Progress = entry.Progress,
        Title = entry.Title,
        Detail = entry.Detail,
        PauseReason = entry.PauseReason,
        Error = entry.Error,
        Cancellable = entry.Cancellable,
        Resumable = entry.Resumable,
        ResumePayload = entry.ResumePayload,
        StartedAt = entry.StartedAt,
        FinishedAt = entry.FinishedAt,
    };

    private static int? ClampProgress(int? value) => value is null ? null : Math.Clamp(value.Value, 0, 100);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            // Safe even with an operation mid-Cancel on another thread: CancellationTokenSource
            // .Dispose() is idempotent (no exception on a second call), so THIS call never throws
            // regardless of ordering. The only side of that race that CAN throw — a concurrent
            // Cancel()'s own cts.Cancel() call landing on an instance this Dispose() just disposed
            // — is guarded at that call site (see Cancel()'s try/catch), not here.
            foreach (var entry in _entries.Values)
                entry.Cts?.Dispose();
            _entries.Clear();
            _finishedOrder.Clear();
        }
    }

    /// <summary>Mutable state for one operation, plus the CTS the registry owns. Never exposed directly — <see cref="ToInfo"/> is the only way out.</summary>
    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Module { get; init; }
        public required string Kind { get; init; }
        public string? Scope { get; init; }
        public OperationStatus Status { get; set; }
        public int? Progress { get; set; }
        public OperationLabel? Title { get; init; }
        public OperationLabel? Detail { get; set; }
        public string? PauseReason { get; set; }
        public IpcError? Error { get; set; }
        public bool Cancellable { get; init; }
        public bool Resumable { get; init; }
        public string? ResumePayload { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public long Sequence { get; set; }

        /// <summary>When this entry last actually emitted — the anchor the throttle window is measured from.</summary>
        public DateTimeOffset LastEmitUtc { get; set; }

        /// <summary>True while a trailing emit is already queued for this entry — caps it at one pending timer, not N.</summary>
        public bool TrailingScheduled { get; set; }
    }

    /// <summary>The handle returned by <see cref="Start"/> — closes over the owning registry and this operation's id.</summary>
    private sealed class OperationHandle(OperationRegistry registry, string id, CancellationToken token) : IOperation
    {
        public string Id { get; } = id;

        public CancellationToken CancellationToken { get; } = token;

        public void Report(int? progress = null, OperationLabel? detail = null) =>
            registry.Report(Id, progress, detail);

        // ActiveOrPaused (§5A.3, this batch): a paused deploy can still complete once the human
        // unblocks it out of band, or fail on a deadline — see Finish's own doc for the full band map.
        public void Complete() => registry.Finish(Id, OperationStatus.Completed, null, "Complete", ActiveOrPaused);

        public void Fail(string code, IReadOnlyDictionary<string, string>? parameters = null, string? message = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            registry.Finish(Id, OperationStatus.Failed, new IpcError { Code = code, Message = message, Parameters = parameters }, "Fail", ActiveOrPaused);
        }

        public void Fail(OperationException error)
        {
            ArgumentNullException.ThrowIfNull(error);
            registry.Finish(Id, OperationStatus.Failed, error.ToError(), "Fail", ActiveOrPaused);
        }

        // CancelTerminal, NOT registry.Cancel(Id) (Finding 1, whole-branch review): this handle is
        // held by the operation's own owner, not an arbitrary by-id client, so ending it is never a
        // permission question the way the public by-id Cancel(id) is — see CancelTerminal's own doc.
        public void Cancel() => registry.CancelTerminal(Id, "Cancel");

        public void Pause(string reason, OperationLabel? detail = null) => registry.Pause(Id, reason, detail);

        public void Resume() => registry.Resume(Id);
    }
}
