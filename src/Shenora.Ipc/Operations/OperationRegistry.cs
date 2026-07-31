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

    /// <summary>Drop the oldest finished entries over <see cref="OperationRegistryOptions.MaxHistory"/>. Caller holds <see cref="_lock"/>.</summary>
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
    /// lifecycle transition (start/terminal — always emits now and always will) from a progress
    /// report (<c>false</c> — every report emits unthrottled today; a follow-up adds a
    /// <see cref="OperationRegistryOptions.ProgressInterval"/>-based frame rate here with a
    /// trailing emit, without touching any caller of this method).
    /// </summary>
    private void Publish(Entry entry, bool immediate)
    {
        _ = immediate; // throttling arrives in a follow-up task; every transition emits for now.
        EmitNow(entry);
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
