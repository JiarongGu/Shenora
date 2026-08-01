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
                operation.Complete();
            }
            catch (OperationCanceledException) { operation.Cancel(); }
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
    public IReadOnlyList<OperationInfo> GetAll(string? module = null, string? scope = null)
    {
        lock (_lock)
        {
            return _entries.Values
                .Where(e => (module is null || string.Equals(e.Module, module, StringComparison.Ordinal))
                         && (scope is null || string.Equals(e.Scope, scope, StringComparison.Ordinal)))
                .OrderBy(e => e.Status == OperationStatus.Running ? 0 : 1)
                .ThenBy(e => e.Sequence)
                .Select(ToInfo)
                .ToList();
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
            miss = Validate(id, out var entry);
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

        // Cancel the token BEFORE the status flip: a body observing the token sees the
        // cancellation rather than racing a completed-then-cancelled transition. Deliberately
        // OUTSIDE the lock: CancellationTokenSource.Cancel() runs registered callbacks
        // synchronously, and a callback that re-enters the registry (e.g. observes the token and
        // calls Report/Complete on the SAME thread) would deadlock re-acquiring _lock if it were
        // still held here. Do NOT move this inside the lock.
        //
        // Because it runs outside the lock, `cts` can legitimately already be disposed by a
        // CONCURRENT Finish() (another caller completed/failed/cancelled the same operation
        // first) or by Dispose() (the registry is being torn down) between the read above and
        // this call — CancellationTokenSource.Cancel() on an already-disposed instance throws
        // ObjectDisposedException. That is not a bug to propagate: it means the operation is
        // already finished (or the registry is gone), so THIS call's own Finish() below will
        // correctly no-op and log the miss. Swallow it — proven the same way in the harvested
        // source app.
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by a concurrent Finish()/Dispose() — see the comment above.
        }

        Finish(id, OperationStatus.Cancelled, null, "Cancel");
        return true;
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
        lock (_lock)
        {
            miss = ValidateResumable(id, out entry);
            if (miss is null) _entries.Remove(id);
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
        }, entry.Scope);

        return true;
    }

    /// <summary>
    /// Look up <paramref name="id"/> for <see cref="RequestResume"/>: null = found, Interrupted, and
    /// Resumable (proceed), <paramref name="entry"/> is set; otherwise the diagnostic reason to log.
    /// MUST be called while holding <see cref="_lock"/> — it reads <see cref="_entries"/> directly.
    /// </summary>
    private string? ValidateResumable(string id, out Entry? entry)
    {
        if (!_entries.TryGetValue(id, out entry))
            return "is not known to this registry (a stale id usually means the caller kept a handle past the operation's life)";
        if (entry.Status != OperationStatus.Interrupted)
            return $"is not a pending interrupted offer (status is {entry.Status})";
        return entry.Resumable
            ? null
            : "is not resumable (OperationOptions.Resumable was false)";
    }

    /// <summary>Called by an <see cref="OperationHandle"/>. Ignored once the operation is terminal.</summary>
    private void Report(string id, int? progress, OperationLabel? detail)
    {
        Entry? entry;
        string? miss;
        lock (_lock)
        {
            miss = Validate(id, out entry);
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
    /// The one terminal transition every finish (Complete/Fail/Cancel) goes through. Idempotent:
    /// a second call for an already-terminal (or unknown) id is a safe no-op — this is what makes
    /// the "Complete at the end + Fail in the catch" pattern safe.
    /// </summary>
    /// <param name="id">The operation id.</param>
    /// <param name="status">The terminal status to transition to.</param>
    /// <param name="error">The structured failure, when <paramref name="status"/> is <see cref="OperationStatus.Failed"/>; otherwise null.</param>
    /// <param name="caller">The public API this came from (<c>"Complete"</c>/<c>"Fail"</c>/<c>"Cancel"</c>) — only for the miss diagnostic.</param>
    private void Finish(string id, OperationStatus status, IpcError? error, string caller)
    {
        Entry? entry;
        string? miss;
        lock (_lock)
        {
            miss = Validate(id, out entry);
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
            return;
        }

        Publish(entry!, immediate: true);
    }

    /// <summary>
    /// Look up <paramref name="id"/>: null = found and running (proceed), <paramref name="entry"/>
    /// is set; otherwise the diagnostic reason to log for why the caller's id was ignored (an
    /// unknown id, or one already in a terminal state). MUST be called while holding
    /// <see cref="_lock"/> — it reads <see cref="_entries"/> directly.
    /// </summary>
    private string? Validate(string id, out Entry? entry)
    {
        if (!_entries.TryGetValue(id, out entry))
            return "is not known to this registry (a stale id usually means the caller kept a handle past the operation's life)";
        return entry.Status != OperationStatus.Running
            ? $"has already reached a terminal state ({entry.Status})"
            : null;
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
    /// Caller holds <see cref="_lock"/>. Only ever touches <see cref="_finishedOrder"/> — an
    /// <see cref="OperationStatus.Interrupted"/> entry from <see cref="RegisterInterrupted"/> is
    /// never added to that list (it is a pending offer, not finished history), so it structurally
    /// cannot be evicted here regardless of how many operations finish afterward.
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

        public void Complete() => registry.Finish(Id, OperationStatus.Completed, null, "Complete");

        public void Fail(string code, IReadOnlyDictionary<string, string>? parameters = null, string? message = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            registry.Finish(Id, OperationStatus.Failed, new IpcError { Code = code, Message = message, Parameters = parameters }, "Fail");
        }

        public void Fail(OperationException error)
        {
            ArgumentNullException.ThrowIfNull(error);
            registry.Finish(Id, OperationStatus.Failed, error.ToError(), "Fail");
        }

        public void Cancel() => registry.Cancel(Id);
    }
}
