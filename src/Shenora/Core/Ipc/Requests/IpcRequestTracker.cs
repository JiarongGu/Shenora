using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// The <see cref="IIpcRequestTracker"/> implementation: one lock over in-memory state, an immutable
/// <see cref="IpcRequestStatus"/> snapshot published on every announced transition. State does not
/// survive a restart.
/// <para>
/// 🔴 <b>The grace period is the whole design.</b> Every request is tracked automatically — paying an
/// entry, a linked <see cref="CancellationTokenSource"/> and a one-shot timer — and the tracker stays
/// SILENT until one outlives <see cref="IpcRequestTrackerOptions.GracePeriod"/>, so the fast case,
/// nearly every case, never reaches the wire.
/// </para>
/// </summary>
public sealed class IpcRequestTracker : IIpcRequestTracker, IDisposable
{
    private readonly IEventBus _bus;
    private readonly IpcRequestTrackerOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Finished ids in finish order, so pruning drops the OLDEST first.</summary>
    private readonly LinkedList<string> _finishedOrder = new();

    private long _nextSequence;
    private bool _disposed;

    /// <summary>Options are validated NOW, so a bad value names itself at the call site.</summary>
    public IpcRequestTracker(IEventBus bus, IpcRequestTrackerOptions? options = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _options = options ?? new IpcRequestTrackerOptions();

        if (_options.MaxHistory < 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(IpcRequestTrackerOptions.MaxHistory)} must be at least 0.");
        if (_options.GracePeriod < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(IpcRequestTrackerOptions.GracePeriod)} must not be negative.");
        if (_options.ProgressInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"{nameof(IpcRequestTrackerOptions.ProgressInterval)} must not be negative.");
    }

    /// <inheritdoc />
    public IIpcRequestScope Begin(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var entry = new Entry
        {
            Id = request.Id,
            Module = request.Module,
            Type = request.Type,
            Scope = request.Scope,
            State = IpcRequestState.Running,
            StartedAt = request.Timestamp,
            Cts = cts,
        };

        lock (_lock)
        {
            entry.Sequence = _nextSequence++;
            // Last writer wins on a duplicate id: refusing here would fail the DISPATCH over a
            // bookkeeping detail.
            _entries[request.Id] = entry;

            // Scheduled, never awaited, and it publishes NOTHING if the request beats it. Zero grace
            // means announce on the next tick rather than never.
            // ⚠ Created AND assigned under the lock, because Finish disposes this field: a concurrent
            // finish landing in between would see null and remove the entry, leaving a live timer on a
            // dead entry that nothing will ever dispose.
            entry.Announce = _options.TimeProvider.CreateTimer(
                static state => ((IpcRequestTracker)((object[])state!)[0]).Announce((string)((object[])state!)[1]),
                new object[] { this, request.Id },
                _options.GracePeriod,
                Timeout.InfiniteTimeSpan);
        }

        return new Scope(this, entry.Id, cts.Token);
    }

    /// <summary>
    /// The grace period expired with the request still running: tell the page, once.
    /// </summary>
    private void Announce(string id)
    {
        Entry? entry = null;
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var found) && !found.Announced && found.State == IpcRequestState.Running)
            {
                found.Announced = true;
                found.LastEmitUtc = _options.TimeProvider.GetUtcNow();
                entry = found;
            }
        }

        if (entry is not null) Publish(entry);
    }

    private void Report(string id, IpcProgress? progress, IpcLabel? detail)
    {
        Entry? toPublish = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.State != IpcRequestState.Running) return;

            if (progress is not null) entry.Progress = progress;
            if (detail is not null) entry.Detail = detail;

            // SILENT while un-announced — the page has never heard of this request. The value is kept,
            // so the first announced snapshot carries the latest.
            if (!entry.Announced) return;

            var now = _options.TimeProvider.GetUtcNow();
            if (_options.ProgressInterval > TimeSpan.Zero && now - entry.LastEmitUtc < _options.ProgressInterval) return;
            entry.LastEmitUtc = now;
            toPublish = entry;
        }

        if (toPublish is not null) Publish(toPublish);
    }

    /// <summary>
    /// The one terminal transition. Idempotent: a second call for an already-finished (or unknown) id is a
    /// safe no-op, which is what makes "complete on dispose + fail in the catch" safe.
    /// <para>
    /// ⚠ <b>Returns whether it ACTUALLY transitioned the entry, and a caller that reports an outcome must
    /// propagate that rather than infer one.</b> A finished entry can be GONE rather than merely changed
    /// (the un-announced fast path below, and <see cref="PruneHistory"/>), so "no entry" cannot be read
    /// as "I did it".
    /// </para>
    /// </summary>
    private bool Finish(string id, IpcRequestState state, IpcError? error)
    {
        Entry? toPublish = null;
        List<string>? removed = null;

        lock (_lock)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.State != IpcRequestState.Running) return false;

            entry.State = state;
            entry.Error = error;
            entry.FinishedAt = _options.TimeProvider.GetUtcNow();
            entry.Announce?.Dispose();
            entry.Announce = null;
            // On BOTH exits, not just the fast path below: the linked source holds a live registration on
            // the host's lifetime token, so an announced entry retained in history would keep it alive
            // until eviction. Double disposal is fine — CTS.Dispose is idempotent.
            entry.Cts?.Dispose();

            if (!entry.Announced)
            {
                // 🔴 THE FAST PATH, and the point of the whole design: nobody was ever told this request
                // existed, so it leaves without touching the wire at all.
                _entries.Remove(id);
                return true;
            }

            _finishedOrder.AddLast(entry.Id);
            removed = PruneHistory();
            toPublish = entry;
        }

        if (toPublish is not null) Publish(toPublish);
        if (removed is { Count: > 0 }) EmitRemoved(removed);
        return true;
    }

    /// <inheritdoc />
    public bool Cancel(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_entries.TryGetValue(requestId, out var entry) || entry.State != IpcRequestState.Running) return false;
            cts = entry.Cts;
        }

        // Token FIRST, so a body observing it sees the cancellation rather than racing a
        // finished-then-cancelled flip. ⚠ Its callbacks run SYNCHRONOUSLY here and one of them may finish
        // the request.
        try { cts?.Cancel(); }
        catch (Exception ex) { Log(() => "[Shenora] Request cancel signal failed.", ex); }

        // Two lock acquisitions, because CancellationTokenSource.Cancel must not run under the lock (its
        // callbacks re-enter this type). So the SECOND one's outcome is the only one still true here:
        // report it, never the first check's.
        return Finish(requestId, IpcRequestState.Cancelled, null);
    }

    /// <inheritdoc />
    public IReadOnlyList<IpcRequestStatus> GetAll(string? module = null, string? scope = null)
    {
        lock (_lock)
        {
            var filtered = _entries.Values.Where(e => Matches(e, module, scope)).ToList();

            var inFlight = filtered.Where(e => e.State == IpcRequestState.Running).OrderBy(e => e.Sequence);
            // Newest-finished first. Sequence breaks ties deterministically: the clock's granularity is
            // coarse enough that two same-tick finishes would otherwise fall back to dictionary order.
            var finished = filtered.Where(e => e.State != IpcRequestState.Running)
                .OrderByDescending(e => e.FinishedAt)
                .ThenByDescending(e => e.Sequence);

            return inFlight.Concat(finished).Select(ToStatus).ToList();
        }
    }

    /// <inheritdoc />
    public void ClearFinished(string? module = null, string? scope = null)
    {
        List<string> removed;
        lock (_lock)
        {
            removed = _entries.Values
                .Where(e => e.State != IpcRequestState.Running && Matches(e, module, scope))
                .Select(e => e.Id)
                .ToList();

            foreach (var id in removed)
            {
                if (_entries.Remove(id, out var entry)) entry.Cts?.Dispose();
                _finishedOrder.Remove(id);
            }
        }

        EmitRemoved(removed);
    }

    /// <summary>
    /// Scope follows <see cref="IEventBus"/>'s rule rather than strict equality: no requested scope matches
    /// everything, and a scope-less request is global so ANY requested scope matches it.
    /// </summary>
    private static bool Matches(Entry entry, string? module, string? scope) =>
        (module is null || string.Equals(entry.Module, module, StringComparison.OrdinalIgnoreCase))
        && (scope is null || entry.Scope is null || string.Equals(entry.Scope, scope, StringComparison.Ordinal));

    /// <summary>Drop the oldest finished entries over <c>MaxHistory</c>. Caller holds the lock.</summary>
    private List<string> PruneHistory()
    {
        var removed = new List<string>();
        while (_finishedOrder.Count > _options.MaxHistory)
        {
            var oldest = _finishedOrder.First!.Value;
            _finishedOrder.RemoveFirst();
            if (_entries.Remove(oldest, out var entry)) entry.Cts?.Dispose();
            removed.Add(oldest);
        }
        return removed;
    }

    private void EmitRemoved(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return;
        // No scope: a batch can span scopes, and deleting an id a subscriber never had is a no-op.
        _bus.Emit(_options.ModuleName, IpcRequestEvents.Removed, new { requestIds = ids.ToArray() });
    }

    private void Publish(Entry entry)
    {
        var status = ToStatus(entry);
        _bus.Emit(new EventMessage
        {
            Module = _options.ModuleName,
            Type = IpcRequestEvents.Updated,
            Payload = status,
            Scope = entry.Scope,
            // 🔴 A snapshot supersedes the snapshot before it: every transition publishes the WHOLE
            // status and the client folds by id last-write-wins, so a batch that drops the intermediate
            // ones lands on the same state.
            // ⚠ Deliberately NOT set on Removed, whose payload is a batch of DIFFERENT ids — superseding
            // one with another would silently lose removals.
            CoalesceKey = entry.Id,
        });
    }

    private static IpcRequestStatus ToStatus(Entry entry) => new()
    {
        Id = entry.Id,
        Module = entry.Module,
        Type = entry.Type,
        Scope = entry.Scope,
        State = entry.State,
        Progress = entry.Progress,
        Detail = entry.Detail,
        Error = entry.Error,
        StartedAt = entry.StartedAt,
        FinishedAt = entry.FinishedAt,
    };

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_options.Log, message, exception: failure);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Announce?.Dispose();
                entry.Cts?.Dispose();
            }
            _entries.Clear();
            _finishedOrder.Clear();
        }
    }

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Module { get; init; }
        public required string Type { get; init; }
        public string? Scope { get; init; }
        public IpcRequestState State { get; set; }
        public IpcProgress? Progress { get; set; }
        public IpcLabel? Detail { get; set; }
        public IpcError? Error { get; set; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; set; }
        public long Sequence { get; set; }
        public CancellationTokenSource? Cts { get; init; }

        /// <summary>True once the grace period expired and the page was told this request exists.</summary>
        public bool Announced { get; set; }

        /// <summary>When this entry last emitted — the anchor the progress throttle measures from.</summary>
        public DateTimeOffset LastEmitUtc { get; set; }

        /// <summary>The one-shot grace timer, disposed the moment the request finishes.</summary>
        public ITimer? Announce { get; set; }
    }

    /// <summary>The per-request scope handed to the dispatch path.</summary>
    private sealed class Scope(IpcRequestTracker tracker, string id, CancellationToken token) : IIpcRequestScope
    {
        private int _finished;

        public string RequestId { get; } = id;

        public CancellationToken CancellationToken { get; } = token;

        public void Report(IpcProgress? progress = null, IpcLabel? detail = null) =>
            tracker.Report(RequestId, progress, detail);

        public void Fail(IpcError error)
        {
            ArgumentNullException.ThrowIfNull(error);
            if (Interlocked.Exchange(ref _finished, 1) == 1) return;
            tracker.Finish(RequestId, IpcRequestState.Failed, error);
        }

        /// <summary>Completes the request if nothing else finished it.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1) return;
            // A cancelled token means the body unwound rather than succeeded; recording Completed there
            // would report success for work that stopped.
            tracker.Finish(RequestId,
                CancellationToken.IsCancellationRequested ? IpcRequestState.Cancelled : IpcRequestState.Completed,
                null);
        }
    }
}
