
namespace Shenora.Engine.Missions;

/// <summary>
/// The default <see cref="IMissionScheduler"/> — an event-driven, claim-aware dispatcher. Admission
/// rules and durability ordering: <c>docs/design/missions-and-files.md</c>.
/// <para>
/// 🔴 <b>Work never runs under <see cref="_gate"/></b>, which covers bookkeeping only: admitted bodies
/// are collected into a local list and started after it is released. Running one inline deadlocks the
/// moment that body submits more work.
/// </para>
/// </summary>
public sealed class MissionScheduler : IMissionScheduler, IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<Entry> _pending = new();
    private readonly List<Entry> _running = [];
    private readonly Dictionary<MissionKey, Entry> _byKey = [];
    private readonly Dictionary<string, IClaimScope> _scopes;
    private readonly Dictionary<string, LaneState> _lanes = new(StringComparer.Ordinal);
    private readonly LaneState _defaultLane;
    private readonly MissionSchedulerOptions _options;
    private readonly IMissionPolicy _policy;
    private readonly IReadOnlyList<IMissionObserver> _observers;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// The shutdown token, captured ONCE at construction. ⚠ <see cref="RunEntryAsync"/> runs on a pool
    /// thread that <see cref="StartAll"/> only QUEUES, so reading <c>_shutdown.Token</c> there races
    /// disposal: the <see cref="ObjectDisposedException"/> lands before that method's try block and
    /// strands the submitter's task forever. A token struct stays readable after its source is disposed.
    /// </summary>
    private readonly CancellationToken _shutdownToken;

    private long _nextId;
    private bool _disposed;

    /// <summary>
    /// The name of the lane every request draws from — see <see cref="GlobalLane"/>. Parenthesised so it
    /// cannot collide with an app's own lane name.
    /// </summary>
    public const string GlobalLaneName = "(global)";

    /// <param name="options">Scopes, capacity, store and logging.</param>
    public MissionScheduler(MissionSchedulerOptions? options = null)
    {
        _options = options ?? new MissionSchedulerOptions();
        _shutdownToken = _shutdown.Token;
        _policy = _options.Policy ?? PriorityMissionPolicy.Instance;
        _observers = _options.Observers;
        _scopes = new Dictionary<string, IClaimScope>(StringComparer.Ordinal);
        foreach (var scope in _options.Scopes)
        {
            ArgumentNullException.ThrowIfNull(scope);
            _scopes[scope.Name] = scope;
        }

        if (_options.GlobalLaneCapacity is { } requested)
            ArgumentOutOfRangeException.ThrowIfLessThan(requested, 1, $"{nameof(options)}.{nameof(MissionSchedulerOptions.GlobalLaneCapacity)}");
        var capacity = _options.GlobalLaneCapacity ?? Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        _defaultLane = new LaneState(GlobalLaneName, capacity, this);
        Log(() => $"mission scheduler ready (global lane capacity {capacity}, scopes: {_scopes.Count})");
    }

    /// <inheritdoc/>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <inheritdoc/>
    public int RunningCount { get { lock (_gate) return _running.Count; } }

    /// <inheritdoc/>
    public bool IsActive(MissionKey key) { lock (_gate) return _byKey.ContainsKey(key); }

    /// <inheritdoc/>
    public IReadOnlyList<MissionExecution> Snapshot()
    {
        lock (_gate)
        {
            var items = new List<MissionExecution>(_pending.Count + _running.Count);
            foreach (var entry in _pending) items.Add(entry.Execution());
            foreach (var entry in _running) items.Add(entry.Execution(running: true));
            return items;
        }
    }

    /// <inheritdoc/>
    public void Reevaluate() => OnLaneChanged();

    /// <inheritdoc/>
    public ILane GlobalLane => _defaultLane;

    /// <inheritdoc/>
    public ILane Lane(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate) return LaneLocked(name);
    }

    /// <summary>
    /// Resolve a lane name to its ONE instance, creating it on first use.
    /// ⚠ <see cref="GlobalLaneName"/> resolves to the global lane itself, never to a lane that merely
    /// shares its name: a decoy would accept a capacity change and alter nothing, and a mission
    /// DECLARING the global lane would draw a second permit from a different pool.
    /// </summary>
    private LaneState LaneLocked(string name)
    {
        if (string.Equals(name, GlobalLaneName, StringComparison.Ordinal)) return _defaultLane;
        if (!_lanes.TryGetValue(name, out var lane))
        {
            // Start at the global bound: a new lane must not narrow work before anyone asked it to.
            lane = new LaneState(name, _defaultLane.Capacity, this);
            _lanes[name] = lane;
        }
        return lane;
    }

    /// <inheritdoc/>
    public Task<MissionResult> SubmitAsync(MissionDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Run);

        Entry entry;
        List<Entry> toStart;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Dedup BEFORE building state: the active entry carries this caller's completion.
            if (definition.Key is { } key && _byKey.TryGetValue(key, out var existing))
            {
                Log(() => $"mission '{key}' deduplicated against {existing.MissionId}");
                return DeduplicateAsync(existing);
            }

            entry = CreateEntry(definition, cancellationToken);   // throws for unknown scope/lane
            _pending.AddLast(entry.Node);
            if (definition.Key is { } newKey) _byKey[newKey] = entry;
            toStart = DispatchLocked();
        }

        // Persist, notify and start OUTSIDE the lock. The Queued append is TRACKED rather than awaited —
        // this method must stay synchronous — and every later store write chains behind it, so the store
        // can never see Running or Remove before Queued: an overtaken Queued append resurrects a finished
        // mission at the next recovery. ⚠ SUPPLIED, not assigned: the entry became dispatchable INSIDE
        // the lock above, so another path can need the ordering before this line runs, and it resolves
        // here for durable and non-durable alike.
        entry.SupplyQueuedPersist(entry.Durable ? PersistAsync(entry, MissionState.Queued) : Task.CompletedTask);
        // A pending item's cancellation must WAKE dispatch, which otherwise runs only on submit,
        // completion or a lane change — none of which may ever come. Registering an already-cancelled
        // token runs the wake HERE, which is what makes the late registration safe.
        if (cancellationToken.CanBeCanceled)
            entry.CancellationWake = cancellationToken.Register(OnLaneChanged);
        Notify(observer => observer.OnQueued(entry.Execution()));
        StartAll(toStart);
        return entry.Completion.Task;
    }

    /// <inheritdoc/>
    public async Task<int> RecoverAsync(
        Func<MissionRecord, MissionDefinition?> rehydrate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rehydrate);
        var store = _options.QueueStore;
        if (store is null) return 0;

        var records = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var requeued = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = _options.RecoveryPolicyFor?.Invoke(record) ?? DefaultRecoveryPolicy(record);
            if (policy is RecoveryPolicy.Discard or RecoveryPolicy.Fail)
            {
                // Both drop the record; Fail is distinguished only by being LOGGED.
                if (policy == RecoveryPolicy.Fail)
                    Log(() => $"mission {record.MissionId} ({record.Kind}) was {record.State} at shutdown — failed, not retried");
                await store.RemoveAsync(record.MissionId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var request = rehydrate(record);
            if (request is null)
            {
                await store.RemoveAsync(record.MissionId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // SubmitAsync is NOT async, so an unusable definition throws HERE, not on the returned task.
            // Skipped rather than rethrown: abandoning the pass over one bad row leaves every later
            // record unrecovered AND unremoved, so the next boot repeats the whole thing.
            try { _ = SubmitAsync(request, cancellationToken); }
            catch (Exception ex)
            {
                Log(() => $"mission {record.MissionId} ({record.Kind}) could not be resubmitted " +
                          $"({ex.GetType().Name}) — dropping the record");
                await store.RemoveAsync(record.MissionId, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // 🔴 THE RESUBMIT MINTS A NEW ID, so the recovered record's own id is now orphaned: its
            // ForgetAsync will remove the NEW id and never this one. Left in place it is reloaded and
            // re-executed on EVERY subsequent boot, forever. Removed AFTER the resubmit, never before —
            // a crash in this window costs a duplicate rather than a lost mission.
            await store.RemoveAsync(record.MissionId, cancellationToken).ConfigureAwait(false);
            requeued++;
        }
        Log(() => $"recovered {requeued} of {records.Count} durable mission record(s)");
        return requeued;
    }

    /// <summary>Running records default to Fail — see <see cref="RecoveryPolicy"/> for the incident behind it.</summary>
    private static RecoveryPolicy DefaultRecoveryPolicy(MissionRecord record) =>
        record.State == MissionState.Running ? RecoveryPolicy.Fail : RecoveryPolicy.Requeue;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        List<Entry> pending, running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = [.. _pending];
            running = [.. _running];
            // Pending keys leave with their entries; running keys leave when their bodies finish.
            foreach (var entry in pending)
                if (entry.Definition.Key is { } key) _byKey.Remove(key);
            _pending.Clear();
        }

        // Queued work never starts; in-flight work is asked to stop and then AWAITED, so a caller that
        // disposes cannot race a body still writing to disk.
        foreach (var entry in pending) entry.TryComplete(MissionOutcome.Cancelled, 0, null);
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var entry in running)
        {
            var task = entry.RunTask;
            if (task is null) continue;
            try { await task.ConfigureAwait(false); } catch { /* reported through the entry */ }
        }
        _shutdown.Dispose();
    }

    /// <summary>
    /// Stop accepting and cancel everything queued — <b>without waiting for work already running</b>.
    /// <para>
    /// 🔴 It exists because the framework registers a scheduler in EVERY app (D64), and a singleton that
    /// is <see cref="IAsyncDisposable"/>-only makes Microsoft DI's synchronous
    /// <c>ServiceProvider.Dispose()</c> THROW — a crash on every clean quit. It is the WEAKER teardown:
    /// <see cref="DisposeAsync"/> awaits in-flight bodies, so <b>prefer <c>await using var app = …</c></b>
    /// whenever a mission may be mid-write.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        List<Entry> pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = [.. _pending];
            foreach (var entry in pending)
                if (entry.Definition.Key is { } key) _byKey.Remove(key);
            _pending.Clear();
        }

        foreach (var entry in pending) entry.TryComplete(MissionOutcome.Cancelled, 0, null);
        // Running bodies are SIGNALLED, not awaited; what this guarantees is that nothing new starts.
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    // ── Admission ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start every pending item whose resources are free and whose turn the policy says it is. Returns
    /// the entries to start; the CALLER runs them after releasing the lock. SAFETY is decided first
    /// (claims, lanes, fairness) and only the survivors are offered to the policy.
    /// </summary>
    private List<Entry> DispatchLocked()
    {
        var started = new List<Entry>();

        // Re-evaluated after each start: taking a permit or a claim can make a later item ineligible.
        while (true)
        {
            List<LinkedListNode<Entry>>? eligible = null;
            var node = _pending.First;
            while (node is not null)
            {
                var next = node.Next;
                var entry = node.Value;

                if (entry.Cancellation.IsCancellationRequested)
                {
                    RemovePendingLocked(node);
                    if (entry.Definition.Key is { } cancelledKey) _byKey.Remove(cancelledKey);
                    entry.TryComplete(MissionOutcome.Cancelled, 0, null);
                    // The caller cancelled it; its Queued record must not resurrect it at recovery.
                    if (entry.Durable) _ = ForgetCancelledAsync(entry);
                    node = next;
                    continue;
                }

                if (CanStartLocked(entry, node)) (eligible ??= []).Add(node);
                node = next;
            }

            if (eligible is null || eligible.Count == 0) break;

            var state = new MissionSchedulerState(_pending.Count, _running.Count);
            var chosen = SelectLocked(eligible, state);
            if (chosen is null) break;   // policy deferred everything it was offered

            var entryToRun = chosen.Value;
            RemovePendingLocked(chosen);
            foreach (var (lane, permits) in entryToRun.Lanes) lane.TakeLocked(permits);
            _running.Add(entryToRun);
            started.Add(entryToRun);
        }

        return started;
    }

    /// <summary>Best eligible node per the policy, skipping any the policy defers.</summary>
    private LinkedListNode<Entry>? SelectLocked(List<LinkedListNode<Entry>> eligible, MissionSchedulerState state)
    {
        LinkedListNode<Entry>? best = null;
        foreach (var candidate in eligible)
        {
            var view = candidate.Value.Execution();
            if (!AskShouldStart(view, state)) continue;
            if (best is null || ComparePolicy(view, best.Value.Execution()) < 0) best = candidate;
        }
        return best;
    }

    private bool AskShouldStart(in MissionExecution view, in MissionSchedulerState state)
    {
        // A throwing policy must not wedge the scheduler: treat a failure as "not now".
        // (`in` parameters cannot be captured by the logging lambda, hence the local copy.)
        try { return _policy.ShouldStart(view, state); }
        catch (Exception ex)
        {
            var id = view.MissionId;
            Log(() => $"mission policy ShouldStart threw; deferring {id}", ex);
            return false;
        }
    }

    private int ComparePolicy(in MissionExecution a, in MissionExecution b)
    {
        try { return _policy.Compare(a, b); }
        catch (Exception ex)
        {
            var (seqA, seqB) = (a.Sequence, b.Sequence);
            Log(() => "mission policy Compare threw; falling back to submission order", ex);
            return seqA.CompareTo(seqB);
        }
    }

    private bool CanStartLocked(Entry entry, LinkedListNode<Entry> self)
    {
        // Lanes first — cheapest test, and the common reason a busy scheduler defers.
        foreach (var (lane, permits) in entry.Lanes)
            if (lane.IsHeld || lane.AvailableLocked < permits) return false;

        foreach (var other in _running)
            if (Conflicts(entry, other)) return false;

        // No EARLIER pending item may conflict — fairness, so a queued item cannot starve. Submission
        // order, NOT policy order: a newer item must never jump a conflicting older one.
        for (var node = _pending.First; node is not null && node != self; node = node.Next)
            if (Conflicts(entry, node.Value)) return false;

        return true;
    }

    private bool Conflicts(Entry a, Entry b)
    {
        foreach (var (scopeName, keyA, modeA) in a.Claims)
        {
            foreach (var (otherScope, keyB, modeB) in b.Claims)
            {
                if (!string.Equals(scopeName, otherScope, StringComparison.Ordinal)) continue;
                // Two shared holders coexist; anything else on a conflicting key does not.
                if (modeA == ClaimMode.Shared && modeB == ClaimMode.Shared) continue;
                if (_scopes[scopeName].Conflicts(keyA, keyB)) return true;
            }
        }
        return false;
    }

    private void RemovePendingLocked(LinkedListNode<Entry> node)
    {
        if (node.List is not null) _pending.Remove(node);
    }

    // ── Execution ─────────────────────────────────────────────────────────────────────────────────

    private void StartAll(List<Entry> entries)
    {
        foreach (var entry in entries)
        {
            Notify(observer => observer.OnStarted(entry.Execution(running: true)));
            // Task.Run so a synchronous-until-first-await body cannot run on the submitting thread,
            // which would make SubmitAsync's cost depend on whether a slot happened to be free.
            entry.RunTask = Task.Run(() => RunEntryAsync(entry), CancellationToken.None);
        }
    }

    /// <summary>
    /// Fan a lifecycle callback out to the observers. One that throws is logged and skipped; the other
    /// observers, and the work itself, carry on.
    /// </summary>
    private void Notify(Action<IMissionObserver> notification)
    {
        for (var i = 0; i < _observers.Count; i++)
        {
            var observer = _observers[i];
            AppCallback.Run(
                () => notification(observer),
                ex => Log(() => $"mission observer {observer.GetType().Name} threw", ex));
        }
    }

    private async Task RunEntryAsync(Entry entry)
    {
        var outcome = MissionOutcome.Completed;
        Exception? error = null;
        var attempts = 0;

        // ⚠ `_shutdownToken`, never `_shutdown.Token` — see the field.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation, _shutdownToken);
        if (entry.Durable)
        {
            // Behind the Queued append, never beside it — the store must see Queued → Running → Remove.
            await entry.QueuedPersist.ConfigureAwait(false);
            await PersistAsync(entry, MissionState.Running).ConfigureAwait(false);
        }

        try
        {
            var retry = entry.Definition.Retry ?? RetryPolicy.None;
            var hasCommit = entry.Definition.Commit is not null;

            // The two-phase rule: with a Commit, Run happens ONCE and only Commit is retried.
            if (hasCommit)
            {
                attempts = 1;
                entry.Attempt = 1;
                await entry.Definition.Run(entry.Execution(running: true), linked.Token).ConfigureAwait(false);
                attempts = await RunWithRetryAsync(entry.Definition.Commit!, entry, retry, linked.Token).ConfigureAwait(false);
            }
            else
            {
                attempts = await RunWithRetryAsync(entry.Definition.Run, entry, retry, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            outcome = MissionOutcome.Cancelled;
            attempts = entry.Attempt;
        }
        catch (Exception ex)
        {
            outcome = MissionOutcome.Failed;
            error = ex;
            // RunWithRetryAsync returns a count only on SUCCESS; on the throw path the entry carries the
            // attempts actually made, and the pre-call value would claim the body never ran.
            attempts = entry.Attempt;
            Log(() => $"mission {entry.MissionId} failed after {attempts} attempt(s)", ex);
        }

        List<Entry> toStart;
        lock (_gate)
        {
            _running.Remove(entry);
            foreach (var (lane, permits) in entry.Lanes) lane.GiveBackLocked(permits);
            if (entry.Definition.Key is { } key) _byKey.Remove(key);
            toStart = _disposed ? [] : DispatchLocked();
        }

        if (entry.Durable) await ForgetAsync(entry).ConfigureAwait(false);
        var result = new MissionResult(outcome, entry.MissionId, attempts, error);
        Notify(observer => observer.OnFinished(entry.Execution(), result));
        entry.CancellationWake.Unregister();
        entry.Completion.TrySetResult(result);
        StartAll(toStart);
    }

    private static async Task<int> RunWithRetryAsync(
        Func<MissionExecution, CancellationToken, Task> body, Entry entry, RetryPolicy retry, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                entry.Attempt = attempt;
                await body(entry.Execution(running: true), ct).ConfigureAwait(false);
                return attempt;
            }
            catch (Exception ex) when (
                attempt < retry.Attempts && !ct.IsCancellationRequested && retry.IsTransient(ex))
            {
                await Task.Delay(retry.Delay * attempt, ct).ConfigureAwait(false);   // linear backoff
            }
        }
    }

    // ── Durability ────────────────────────────────────────────────────────────────────────────────

    private async Task PersistAsync(Entry entry, MissionState state)
    {
        if (_options.QueueStore is not { } store) return;
        var record = new MissionRecord(entry.MissionId, entry.Definition.Kind, entry.Definition.Payload, state,
            entry.CreatedUtc, entry.Definition.Key);
        // A store failure must never take down the work it was describing: durability is a best-effort
        // overlay on execution, not a precondition for it.
        try { await store.AppendAsync(record, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log(() => $"mission store save failed for {entry.MissionId}", ex); }
    }

    private async Task ForgetAsync(Entry entry)
    {
        if (_options.QueueStore is not { } store) return;
        try { await store.RemoveAsync(entry.MissionId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log(() => $"mission store remove failed for {entry.MissionId}", ex); }
    }

    /// <summary>Remove a cancelled-while-pending durable record — after its Queued append lands.</summary>
    private async Task ForgetCancelledAsync(Entry entry)
    {
        await entry.QueuedPersist.ConfigureAwait(false);
        await ForgetAsync(entry).ConfigureAwait(false);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Answer a submission that was folded into work already in flight — with THAT work's outcome.
    /// </summary>
    /// <remarks>
    /// 🔴 It used to discard everything but the id and always answer <c>Deduplicated</c>, which
    /// <c>MissionResult.Succeeded</c> treats as success — so a submission folded into a mission that THREW
    /// was told it had succeeded, and <c>ThrowIfFailed()</c> was a no-op on a real failure. The outcome is
    /// carried now: a failure stays a failure, with its error and its attempt count, and
    /// <c>Deduplicated</c> means what its own doc says — the work was already done, and it worked.
    /// </remarks>
    private static async Task<MissionResult> DeduplicateAsync(Entry existing)
    {
        var result = await existing.Completion.Task.ConfigureAwait(false);
        return result.Outcome is MissionOutcome.Completed or MissionOutcome.Deduplicated
            ? new MissionResult(MissionOutcome.Deduplicated, result.MissionId, 0, null)
            : new MissionResult(result.Outcome, result.MissionId, result.Attempts, result.Error);
    }

    private Entry CreateEntry(MissionDefinition definition, CancellationToken cancellationToken)
    {
        var claims = new List<(string Scope, string Key, ClaimMode Mode)>(definition.Claims.Count);
        foreach (var claim in definition.Claims)
        {
            if (!_scopes.TryGetValue(claim.Scope, out var scope))
                throw new ArgumentException(
                    $"claim scope '{claim.Scope}' is not registered on this scheduler — add it to " +
                    $"{nameof(MissionSchedulerOptions)}.{nameof(MissionSchedulerOptions.Scopes)}. Ignoring it would " +
                    "silently drop an exclusion the caller asked for.", nameof(definition));
            claims.Add((claim.Scope, scope.Normalize(claim.Key), claim.Mode));
        }

        var lanes = new List<(LaneState Lane, int Permits)>(definition.Lanes.Count + 1) { (_defaultLane, 1) };
        foreach (var (name, permits) in definition.Lanes)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(definition));
            ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1, nameof(definition));
            var lane = LaneLocked(name);
            // Naming one lane twice would take its permits twice and could deadlock against itself; the
            // merge is also what makes declaring the GLOBAL lane safe, since it is already in this list.
            var index = lanes.FindIndex(x => ReferenceEquals(x.Lane, lane));
            if (index >= 0) lanes[index] = (lane, lanes[index].Permits + permits);
            else lanes.Add((lane, permits));
        }

        var sequence = Interlocked.Increment(ref _nextId);
        return new Entry($"m{sequence}", sequence, definition, claims, lanes, cancellationToken);
    }

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Log, message, exception: failure);

    /// <summary>Re-run admission after a lane's capacity or hold state changed.</summary>
    internal void OnLaneChanged()
    {
        List<Entry> toStart;
        lock (_gate)
        {
            if (_disposed) return;
            toStart = DispatchLocked();
        }
        StartAll(toStart);
    }

    /// <summary>A queued or running item and everything the scheduler tracks about it.</summary>
    private sealed class Entry
    {
        public Entry(
            string missionId, long sequence, MissionDefinition definition,
            List<(string Scope, string Key, ClaimMode Mode)> claims,
            List<(LaneState Lane, int Permits)> lanes, CancellationToken cancellation)
        {
            MissionId = missionId;
            Definition = definition;
            Claims = claims;
            Lanes = lanes;
            Cancellation = cancellation;
            Node = new LinkedListNode<Entry>(this);
            CreatedUtc = DateTimeOffset.UtcNow;
            QueuedPersist = _queuedPersist.Task.Unwrap();
            Queued = new MissionExecution(missionId, definition.Kind, definition.Priority, CreatedUtc, sequence,
                Key: definition.Key);
        }

        public string MissionId { get; }
        public MissionDefinition Definition { get; }
        public List<(string Scope, string Key, ClaimMode Mode)> Claims { get; }
        public List<(LaneState Lane, int Permits)> Lanes { get; }
        public CancellationToken Cancellation { get; }
        public LinkedListNode<Entry> Node { get; }
        public DateTimeOffset CreatedUtc { get; }

        /// <summary>The execution as first accepted: attempt 0, not running. Every other view is a `with`.</summary>
        public MissionExecution Queued { get; }

        /// <summary>Current attempt, so a snapshot reports the attempt running work is on.</summary>
        public int Attempt { get; set; }

        /// <summary>The execution as it stands now — the one value handed to bodies, observers and views.</summary>
        public MissionExecution Execution(bool running = false) =>
            Queued with { Attempt = Attempt, IsRunning = running };

        public Task? RunTask { get; set; }
        public bool Durable => Definition.Durable;

        /// <summary>
        /// The Queued append, so every later store write can chain behind it — never overtake it. A GATE
        /// rather than a settable task: the entry is dispatchable the moment it enters <c>_pending</c>,
        /// while the append starts after the lock, so a waiter arriving in that window must wait for the
        /// assignment itself rather than read a default.
        /// </summary>
        public Task QueuedPersist { get; }

        private readonly TaskCompletionSource<Task> _queuedPersist =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Resolve <see cref="QueuedPersist"/> — called exactly once, durable or not.</summary>
        public void SupplyQueuedPersist(Task persist) => _queuedPersist.TrySetResult(persist);

        /// <summary>
        /// Wakes dispatch when a pending item's token cancels. ⚠ <b>Unregister, never Dispose</b>:
        /// Dispose blocks until an in-flight callback finishes, and that callback takes the scheduler
        /// lock — disposing from under the lock deadlocks against it.
        /// </summary>
        public CancellationTokenRegistration CancellationWake { get; set; }

        public TaskCompletionSource<MissionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Idempotent: a cancelled-while-pending entry can also be reached by dispose.</summary>
        public void TryComplete(MissionOutcome outcome, int attempts, Exception? error)
        {
            CancellationWake.Unregister();
            Completion.TrySetResult(new MissionResult(outcome, MissionId, attempts, error));
        }
    }

    /// <summary>
    /// A permit pool. Counters are guarded by the scheduler's lock; the lane has none of its own, so
    /// there is exactly one lock in this component and no lock order to reason about.
    /// </summary>
    private sealed class LaneState : ILane
    {
        private readonly MissionScheduler _owner;
        private int _capacity;
        private int _taken;
        private int _holds;

        public LaneState(string name, int capacity, MissionScheduler owner)
        {
            Name = name;
            _capacity = capacity;
            _owner = owner;
        }

        public string Name { get; }

        public int Capacity
        {
            get { lock (_owner._gate) return _capacity; }
            set
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                int bound;
                lock (_owner._gate)
                {
                    _capacity = value;
                    bound = IsGlobal ? value : _owner._defaultLane._capacity;
                }
                // ⚠ SAY SO when the request cannot take effect — storing it silently made this invisible,
                // since the getter still answered with the requested value. Logged rather than thrown or
                // clamped: a governor may widen a lane just before widening the global bound.
                if (value > bound)
                {
                    _owner.Log(() =>
                        $"lane '{Name}' capacity set to {value}, but the global lane admits {bound}, so it will " +
                        $"run at {bound}. Raise IMissionScheduler.GlobalLane.Capacity to widen it.");
                }
                // Raising capacity can admit queued work immediately; lowering it cancels nothing —
                // AvailableLocked simply stays <= 0 until enough items finish.
                _owner.OnLaneChanged();
            }
        }

        /// <inheritdoc/>
        public int EffectiveCapacity
        {
            get
            {
                lock (_owner._gate)
                    return IsGlobal ? _capacity : Math.Min(_capacity, _owner._defaultLane._capacity);
            }
        }

        /// <summary>
        /// Whether this IS the scheduler's global lane, which bounds every other lane and is bounded by
        /// nothing but itself. ⚠ Compared by REFERENCE: the global lane is constructed before
        /// <see cref="_defaultLane"/> is assigned, so a name comparison would be true during its own
        /// construction and would then read an uninitialised field.
        /// </summary>
        private bool IsGlobal => ReferenceEquals(this, _owner._defaultLane);

        public bool IsHeld { get { lock (_owner._gate) return _holds > 0; } }

        public void Hold() { lock (_owner._gate) _holds++; }

        public void Release()
        {
            lock (_owner._gate) { if (_holds > 0) _holds--; }
            _owner.OnLaneChanged();
        }

        /// <summary>Permits free right now. Negative when capacity was lowered below what is running.</summary>
        public int AvailableLocked => _capacity - _taken;

        public void TakeLocked(int permits) => _taken += permits;

        public void GiveBackLocked(int permits) => _taken -= permits;
    }
}
