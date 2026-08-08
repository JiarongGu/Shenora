using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// Tracks long-running work started by any module: id, owning module, app-defined kind and scope,
/// status, progress, timestamps. Mechanism only — the kit ships no queue, scheduler, retry,
/// priority, or phase model, and no opinion on what an operation IS or means. See
/// <see cref="OperationEvents"/> for the event this registry publishes on every transition.
/// </summary>
public interface IOperationRegistry
{
    /// <summary>
    /// Start a tracked operation owned by <paramref name="module"/> and get its handle. Publishes
    /// an immediate <see cref="OperationStatus.Running"/> snapshot.
    /// </summary>
    IOperation Start(string module, OperationOptions options);

    /// <summary>
    /// Start the operation, hand <paramref name="work"/> OFF to the background, and finish it:
    /// <c>Complete</c> on success, <c>Cancel</c> on <see cref="OperationCanceledException"/>,
    /// <c>Fail</c> otherwise (never a raw exception — see the type doc). Returns the operation id
    /// IMMEDIATELY.
    /// <para>
    /// This is the primitive; <see cref="IModuleContext.Run"/> is a one-line delegation onto it. It
    /// lives HERE, not on the context, so it is reachable from ordinary services too — a download
    /// service starting an installer fetch, not only an IPC facade route. One implementation,
    /// reachable from both call-site shapes.
    /// </para>
    /// <para>
    /// The work gets the OPERATION's own token, never a caller's — see
    /// <see cref="IOperation.CancellationToken"/>: work handed off outlives whatever started it, and
    /// capturing a caller's token (a request, say) kills it the moment that caller's lifetime ends.
    /// </para>
    /// </summary>
    string Run(string module, OperationOptions options, Func<IOperation, CancellationToken, Task> work);


    /// <summary>
    /// Snapshot of currently-known operations (running plus retained finished history, capped by
    /// <see cref="OperationRegistryOptions.MaxHistory"/>), optionally filtered by owning module
    /// and/or scope. Running operations sort first.
    /// <para>
    /// <paramref name="scope"/> follows the SAME rule as <see cref="Shenora.Core.Events.IEventBus"/>'s scope
    /// matching, not strict equality: <c>null</c> (the default) returns every scope, and an operation
    /// started with no <see cref="OperationOptions.Scope"/> of its own matches ANY requested scope —
    /// a scope-less operation is global, the same way a scope-less event reaches scoped subscribers.
    /// </para>
    /// </summary>
    IReadOnlyList<OperationInfo> GetAll(string? module = null, string? scope = null);

    /// <summary>
    /// Cancel a running operation by id: cancels its own <see cref="CancellationToken"/> first,
    /// then transitions it to <see cref="OperationStatus.Cancelled"/>. Returns false — and changes
    /// nothing — for an unknown id, one that has already reached a terminal state, or one started
    /// with <see cref="OperationOptions.Cancellable"/> false: that flag is documented as "exposes a
    /// WORKING cancel", so honoring a cancel the operation never opted into would flip its status
    /// while the body kept running to completion underneath it.
    /// </summary>
    bool Cancel(string id);

    /// <summary>
    /// Drop finished (terminal) history, keeping running (and WAITING-band) work untouched.
    /// Filtered EXACTLY like <see cref="GetAll"/> — same two keys, same rule (<paramref name="scope"/>
    /// follows <see cref="Shenora.Core.Events.IEventBus"/>'s scope matching: <c>null</c> clears every scope,
    /// and an unscoped finished entry is cleared by ANY requested scope).
    /// <para>
    /// Was unfilterable for one release (generic-library audit finding 1): the kit ships secondary
    /// windows and a scoped container router, so "clear completed" in one scoped window used to wipe
    /// every OTHER scope's finished history too — the read side (<see cref="GetAll"/>) had the filter
    /// from day one; the removal side did not.
    /// </para>
    /// </summary>
    void ClearFinished(string? module = null, string? scope = null);



}
