namespace Shenora.Core;

/// <summary>
/// The default <see cref="IMissionScheduler"/> — an event-driven, claim-aware dispatcher.
///
/// <para>
/// ADMISSION. A pending item starts when all three hold:
/// (1) no IN-FLIGHT item holds a conflicting claim — the safety rule;
/// (2) no EARLIER PENDING item holds a conflicting claim — the fairness rule, without which a
///     steady stream of newer disjoint work starves a queued item indefinitely;
/// (3) a permit is free in every lane it named, and none of those lanes is held.
/// </para>
///
/// <para>
/// DISPATCH IS EVENT-DRIVEN — evaluated on submit and on each completion. There is no polling
/// worker and no dedicated thread. The family's two prior planners both used a worker loop and both
/// paid for it, one in idle latency and one in a thread parked for the process lifetime.
/// </para>
///
/// <para>
/// WORK NEVER RUNS UNDER THE LOCK. <see cref="_gate"/> covers bookkeeping only; admitted bodies are
/// collected into a local list and started after it is released. This is called out because it is
/// the single easiest thing to get wrong here — running a body inline while holding the lock
/// deadlocks the moment that body submits more work, which every real user of a scheduler
/// eventually does.
/// </para>
/// </summary>
public sealed class MissionScheduler : IMissionScheduler
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
    private long _nextId;
    private bool _disposed;

    /// <summary>The lane every request draws from. Not addressable by name; use the options to size it.</summary>
    private const string DefaultLaneName = "";

    /// <param name="options">Scopes, capacity, store and logging.</param>
    public MissionScheduler(MissionSchedulerOptions? options = null)
    {
        _options = options ?? new MissionSchedulerOptions();
        _policy = _options.Policy ?? PriorityMissionPolicy.Instance;
        _observers = _options.Observers;
        _scopes = new Dictionary<string, IClaimScope>(StringComparer.Ordinal);
        foreach (var scope in _options.Scopes)
        {
            ArgumentNullException.ThrowIfNull(scope);
            _scopes[scope.Name] = scope;
        }

        var capacity = _options.DefaultLaneCapacity > 0
            ? _options.DefaultLaneCapacity
            : Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
        _defaultLane = new LaneState(DefaultLaneName, capacity, this);
        Log(() => $"mission scheduler ready (default lane capacity {capacity}, scopes: {_scopes.Count})");
    }

    /// <inheritdoc/>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <inheritdoc/>
    public int RunningCount { get { lock (_gate) return _running.Count; } }

    /// <inheritdoc/>
    public bool IsActive(MissionKey key) { lock (_gate) return _byKey.ContainsKey(key); }

    /// <inheritdoc/>
    public IReadOnlyList<MissionSnapshot> Snapshot()
    {
        lock (_gate)
        {
            var items = new List<MissionSnapshot>(_pending.Count + _running.Count);
            foreach (var entry in _pending) items.Add(new MissionSnapshot(entry.View, IsRunning: false));
            foreach (var entry in _running) items.Add(new MissionSnapshot(entry.View, IsRunning: true));
            return items;
        }
    }

    /// <inheritdoc/>
    public void Reevaluate() => OnLaneChanged();

    /// <inheritdoc/>
    public ILane Lane(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_gate)
        {
            if (!_lanes.TryGetValue(name, out var lane))
            {
                lane = new LaneState(name, _defaultLane.Capacity, this);
                _lanes[name] = lane;
            }
            return lane;
        }
    }

    /// <inheritdoc/>
    public Task<MissionResult> SubmitAsync(MissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Run);

        Entry entry;
        List<Entry> toStart;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Dedup BEFORE building state: an identical key already active carries this caller's
            // completion, and the body never runs a second time.
            if (request.Key is { } key && _byKey.TryGetValue(key, out var existing))
            {
                Log(() => $"mission '{key}' deduplicated against {existing.MissionId}");
                return DeduplicateAsync(existing);
            }

            entry = CreateEntry(request, cancellationToken);   // throws for unknown scope/lane
            _pending.AddLast(entry.Node);
            if (request.Key is { } newKey) _byKey[newKey] = entry;
            toStart = DispatchLocked();
        }

        // Persist, notify and start OUTSIDE the lock.
        if (entry.Durable) _ = PersistAsync(entry, MissionState.Queued);
        Notify(observer => observer.OnQueued(entry.View));
        StartAll(toStart);
        return entry.Completion.Task;
    }

    /// <inheritdoc/>
    public async Task<int> RecoverAsync(
        Func<MissionRecord, MissionRequest?> rehydrate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rehydrate);
        var store = _options.Store;
        if (store is null) return 0;

        var records = await store.LoadPendingAsync(cancellationToken).ConfigureAwait(false);
        var requeued = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policy = _options.RecoveryPolicyFor?.Invoke(record) ?? DefaultRecoveryPolicy(record);
            if (policy is RecoveryPolicy.Discard or RecoveryPolicy.Fail)
            {
                // Both drop the record; Fail is distinguished only by being LOGGED, because a record
                // that silently vanished after a crash is indistinguishable from one that ran.
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
            _ = SubmitAsync(request, cancellationToken);
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
            _pending.Clear();
        }

        // Queued work never starts; in-flight work is asked to stop and then AWAITED, so a caller
        // that disposes cannot race a body still writing to disk.
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

    // ── Admission ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Start every pending item whose resources are free and whose turn the policy says it is.
    /// Returns the entries to start; the CALLER runs them after releasing the lock.
    ///
    /// <para>
    /// Order of operations matters and is the safety boundary for app-supplied policy: SAFETY is
    /// decided first (claims, lanes, fairness), and only the survivors are offered to the policy.
    /// A policy therefore chooses among legal moves and can delay work, never corrupt it.
    /// </para>
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
                    if (entry.Request.Key is { } cancelledKey) _byKey.Remove(cancelledKey);
                    entry.TryComplete(MissionOutcome.Cancelled, 0, null);
                    node = next;
                    continue;
                }

                if (CanStartLocked(entry, node)) (eligible ??= []).Add(node);
                node = next;
            }

            if (eligible is null || eligible.Count == 0) break;

            // "What next" — the app's ordering, over the already-safe set.
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
            var view = candidate.Value.View;
            // "When" — a policy may hold this item back for a reason only the app can see.
            if (!AskShouldStart(view, state)) continue;
            if (best is null || ComparePolicy(view, best.Value.View) < 0) best = candidate;
        }
        return best;
    }

    private bool AskShouldStart(in MissionView view, in MissionSchedulerState state)
    {
        // A throwing policy must not wedge the scheduler; treat a failure as "not now" and log it.
        // (`in` parameters cannot be captured by the logging lambda, hence the local copy.)
        try { return _policy.ShouldStart(view, state); }
        catch (Exception ex)
        {
            var id = view.MissionId;
            Log(() => $"mission policy ShouldStart threw ({ex.GetType().Name}); deferring {id}");
            return false;
        }
    }

    private int ComparePolicy(in MissionView a, in MissionView b)
    {
        try { return _policy.Compare(a, b); }
        catch (Exception ex)
        {
            var (seqA, seqB) = (a.Sequence, b.Sequence);
            Log(() => $"mission policy Compare threw ({ex.GetType().Name}); falling back to submission order");
            return seqA.CompareTo(seqB);
        }
    }

    private bool CanStartLocked(Entry entry, LinkedListNode<Entry> self)
    {
        // (3) lanes first — cheapest test, and the common reason a busy scheduler defers.
        foreach (var (lane, permits) in entry.Lanes)
            if (lane.IsHeld || lane.AvailableLocked < permits) return false;

        // (1) nothing in flight may conflict.
        foreach (var other in _running)
            if (Conflicts(entry, other)) return false;

        // (2) no EARLIER pending item may conflict — fairness, so a queued item cannot starve.
        //     Note this is submission order, NOT policy order: priority re-ranks work that could
        //     legally run in any order, and must never let a newer item jump a conflicting older one.
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
            Notify(observer => observer.OnStarted(entry.View));
            // Task.Run so a synchronous-until-first-await body cannot run on the submitting thread,
            // which would make SubmitAsync's cost depend on whether a slot happened to be free.
            entry.RunTask = Task.Run(() => RunEntryAsync(entry), CancellationToken.None);
        }
    }

    /// <summary>
    /// Fan a lifecycle callback out to the observers. Guarded per observer: one that throws is
    /// logged and skipped, and the others — and the work itself — carry on. An observer is a
    /// bystander; it must never be able to fail the thing it is watching.
    /// </summary>
    private void Notify(Action<IMissionObserver> notification)
    {
        for (var i = 0; i < _observers.Count; i++)
        {
            var observer = _observers[i];
            AppCallback.Run(
                () => notification(observer),
                ex => Log(() => $"mission observer {observer.GetType().Name} threw: {ex.GetType().Name}"));
        }
    }

    private async Task RunEntryAsync(Entry entry)
    {
        var outcome = MissionOutcome.Completed;
        Exception? error = null;
        var attempts = 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation, _shutdown.Token);
        if (entry.Durable) await PersistAsync(entry, MissionState.Running).ConfigureAwait(false);

        try
        {
            var retry = entry.Request.Retry ?? RetryPolicy.None;
            var hasCommit = entry.Request.Commit is not null;

            // The two-phase rule: with a Commit, Run happens ONCE and only Commit is retried.
            if (hasCommit)
            {
                attempts = 1;
                await entry.Request.Run(new MissionContext(entry.MissionId, 1, linked.Token)).ConfigureAwait(false);
                attempts = await RunWithRetryAsync(entry.Request.Commit!, entry, retry, linked.Token).ConfigureAwait(false);
            }
            else
            {
                attempts = await RunWithRetryAsync(entry.Request.Run, entry, retry, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            outcome = MissionOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            outcome = MissionOutcome.Failed;
            error = ex;
            Log(() => $"mission {entry.MissionId} failed after {attempts} attempt(s): {ex.GetType().Name}");
        }

        List<Entry> toStart;
        lock (_gate)
        {
            _running.Remove(entry);
            foreach (var (lane, permits) in entry.Lanes) lane.GiveBackLocked(permits);
            if (entry.Request.Key is { } key) _byKey.Remove(key);
            toStart = _disposed ? [] : DispatchLocked();
        }

        if (entry.Durable) await ForgetAsync(entry).ConfigureAwait(false);
        var result = new MissionResult(outcome, entry.MissionId, attempts, error);
        Notify(observer => observer.OnFinished(entry.View, result));
        entry.Completion.TrySetResult(result);
        StartAll(toStart);
    }

    private static async Task<int> RunWithRetryAsync(
        Func<MissionContext, Task> body, Entry entry, RetryPolicy retry, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await body(new MissionContext(entry.MissionId, attempt, ct)).ConfigureAwait(false);
                return attempt;
            }
            catch (Exception ex) when (
                attempt < retry.Attempts && !ct.IsCancellationRequested && retry.IsTransient(ex))
            {
                // Linear backoff: the family measured 500ms × attempt as long enough for an external
                // process to release a file and short enough that the UI does not feel stuck.
                await Task.Delay(retry.Delay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Durability ────────────────────────────────────────────────────────────────────────────────

    private async Task PersistAsync(Entry entry, MissionState state)
    {
        if (_options.Store is not { } store) return;
        var record = new MissionRecord(entry.MissionId, entry.Request.Kind, entry.Request.Payload, state, entry.CreatedUtc);
        // A store failure must never take down the work it was describing — durability is a
        // best-effort overlay on execution, not a precondition for it.
        try { await store.SaveAsync(record, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log(() => $"mission store save failed for {entry.MissionId}: {ex.GetType().Name}"); }
    }

    private async Task ForgetAsync(Entry entry)
    {
        if (_options.Store is not { } store) return;
        try { await store.RemoveAsync(entry.MissionId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Log(() => $"mission store remove failed for {entry.MissionId}: {ex.GetType().Name}"); }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<MissionResult> DeduplicateAsync(Entry existing)
    {
        var result = await existing.Completion.Task.ConfigureAwait(false);
        return new MissionResult(MissionOutcome.Deduplicated, result.MissionId, 0, null);
    }

    private Entry CreateEntry(MissionRequest request, CancellationToken cancellationToken)
    {
        var claims = new List<(string Scope, string Key, ClaimMode Mode)>(request.Claims.Count);
        foreach (var claim in request.Claims)
        {
            if (!_scopes.TryGetValue(claim.Scope, out var scope))
                throw new ArgumentException(
                    $"claim scope '{claim.Scope}' is not registered on this scheduler — add it to " +
                    $"{nameof(MissionSchedulerOptions)}.{nameof(MissionSchedulerOptions.Scopes)}. Ignoring it would " +
                    "silently drop an exclusion the caller asked for.", nameof(request));
            claims.Add((claim.Scope, scope.Normalize(claim.Key), claim.Mode));
        }

        var lanes = new List<(LaneState Lane, int Permits)>(request.Lanes.Count + 1) { (_defaultLane, 1) };
        foreach (var (name, permits) in request.Lanes)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(request));
            ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1, nameof(request));
            if (!_lanes.TryGetValue(name, out var lane))
            {
                lane = new LaneState(name, _defaultLane.Capacity, this);
                _lanes[name] = lane;
            }
            // Naming one lane twice would take its permits twice and could deadlock against itself.
            var index = lanes.FindIndex(x => ReferenceEquals(x.Lane, lane));
            if (index >= 0) lanes[index] = (lane, lanes[index].Permits + permits);
            else lanes.Add((lane, permits));
        }

        var sequence = Interlocked.Increment(ref _nextId);
        return new Entry($"w{sequence}", sequence, request, claims, lanes, cancellationToken);
    }

    private void Log(Func<string> message) => AppCallback.Log(_options.Log, message);

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
            string missionId, long sequence, MissionRequest request,
            List<(string Scope, string Key, ClaimMode Mode)> claims,
            List<(LaneState Lane, int Permits)> lanes, CancellationToken cancellation)
        {
            MissionId = missionId;
            Request = request;
            Claims = claims;
            Lanes = lanes;
            Cancellation = cancellation;
            Node = new LinkedListNode<Entry>(this);
            CreatedUtc = DateTimeOffset.UtcNow;
            View = new MissionView(missionId, request.Kind, request.Priority, CreatedUtc, sequence);
        }

        public string MissionId { get; }
        public MissionRequest Request { get; }
        public List<(string Scope, string Key, ClaimMode Mode)> Claims { get; }
        public List<(LaneState Lane, int Permits)> Lanes { get; }
        public CancellationToken Cancellation { get; }
        public LinkedListNode<Entry> Node { get; }
        public DateTimeOffset CreatedUtc { get; }
        public MissionView View { get; }
        public Task? RunTask { get; set; }
        public bool Durable => Request.Durable;

        public TaskCompletionSource<MissionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Idempotent: a cancelled-while-pending entry can also be reached by dispose, and completing
        /// a TCS twice throws.
        /// </summary>
        public void TryComplete(MissionOutcome outcome, int attempts, Exception? error) =>
            Completion.TrySetResult(new MissionResult(outcome, MissionId, attempts, error));
    }

    /// <summary>
    /// A permit pool. Counters are guarded by the scheduler's lock — the lane deliberately has no
    /// lock of its own, so there is exactly one lock in this component and therefore no lock order
    /// to reason about.
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
                lock (_owner._gate) _capacity = value;
                // Raising capacity can admit queued work immediately; lowering it cannot cancel
                // anything, it just means AvailableLocked stays <= 0 until enough items finish.
                _owner.OnLaneChanged();
            }
        }

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
