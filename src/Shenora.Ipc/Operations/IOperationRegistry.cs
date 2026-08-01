namespace Shenora.Ipc;

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
    /// <paramref name="scope"/> follows the SAME rule as <see cref="Shenora.Core.IEventBus"/>'s scope
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

    /// <summary>Drop all finished (terminal) history, keeping running work untouched.</summary>
    void ClearFinished();

    /// <summary>
    /// Decline a pending offer in the WAITING band (§5A.2): <see cref="OperationStatus.Paused"/> or
    /// <see cref="OperationStatus.Interrupted"/> → <see cref="OperationStatus.Cancelled"/> — terminal,
    /// so it enters bounded history and is prunable/clearable like any other finished entry, and
    /// publishes an <see cref="OperationEvents.Updated"/> snapshot like any other terminal transition
    /// (unlike <see cref="ClearFinished"/>/<see cref="RequestResume"/>, which remove an entry with no
    /// corresponding wire event).
    /// <para>
    /// <b>Refuses <see cref="OperationStatus.Running"/></b> — returns <c>false</c> and changes nothing.
    /// Declining a pending offer and cancelling LIVE work are different acts: cancelling is
    /// <see cref="Cancel"/>'s job, permission-checked against <see cref="OperationOptions.Cancellable"/>;
    /// routing around that check by letting <c>Dismiss</c> also accept <see cref="OperationStatus.Running"/>
    /// is the exact conflation that produced this branch's only Critical (§5A.3). A separate member,
    /// not <c>Cancel</c> accepting more states, for the same reason.
    /// </para>
    /// <para>
    /// Signals the entry's own <see cref="CancellationToken"/> FIRST when one exists (a
    /// <see cref="OperationStatus.Paused"/> entry still has one; an <see cref="OperationStatus.Interrupted"/>
    /// entry never does — see <see cref="RegisterInterrupted"/>), so a paused body still parked on its
    /// token unwinds the same way a running one does under <see cref="Cancel"/>.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>false</c> — and changes nothing — for an unknown id, a <see cref="OperationStatus.Running"/>
    /// one, or one already terminal: the same honest-refusal shape as every other transition here.
    /// </returns>
    bool Dismiss(string id);

    /// <summary>
    /// Announce a crash-interrupted, resumable operation from the APP's own checkpoint — the kit
    /// holds the offer; the app owns what to resume and how. Requires
    /// <see cref="OperationOptions.Resumable"/> true and a non-empty
    /// <see cref="OperationOptions.ResumePayload"/> (the opaque checkpoint token), throwing
    /// <see cref="ArgumentException"/> naming whichever is missing — a silently-accepted unusable
    /// entry would be worse than a loud rejection.
    /// <para>
    /// Deduped on <c>(module, kind, resumePayload)</c> among already-<see cref="OperationStatus.Interrupted"/>
    /// entries: re-announcing the SAME checkpoint (a profile/session switch, say) returns the
    /// existing id rather than stacking a second offer for what is still the same interrupted
    /// checkpoint.
    /// </para>
    /// <para>
    /// The returned entry is a pending OFFER, not finished history: the registry's automatic history
    /// pruning (capped by <see cref="OperationRegistryOptions.MaxHistory"/>) never evicts it — only
    /// <see cref="RequestResume"/> removes it.
    /// </para>
    /// </summary>
    string RegisterInterrupted(string module, OperationOptions options);

    /// <summary>
    /// The user asked to resume operation <paramref name="id"/>. Accepts an entry in the WAITING band
    /// (§5A.2) — <see cref="OperationStatus.Paused"/> OR <see cref="OperationStatus.Interrupted"/> —
    /// returning <c>false</c> and changing nothing otherwise (the same honest-refusal shape as
    /// <see cref="Cancel"/> for an unknown or wrong-state id). On success, emits
    /// <see cref="OperationEvents.ResumeRequested"/> with
    /// <c>{ operationId, module, kind, resumePayload, scope, status }</c> — <c>status</c> lets a
    /// handler tell the two cases apart, because it CANNOT look the entry up afterward for the
    /// <see cref="OperationStatus.Interrupted"/> case (see the asymmetry below).
    /// <para>
    /// <b>The asymmetry is deliberate (§5A.4), not an inconsistency to tidy away:</b> for
    /// <see cref="OperationStatus.Paused"/>, the entry is LEFT IN PLACE — the app calls
    /// <see cref="IOperation.Resume"/> on its own handle once it has actually resumed, so the client
    /// asking is not the state changing. For <see cref="OperationStatus.Interrupted"/>, the entry is
    /// still REMOVED, because there is no live handle to flip — the body died with the process, and the
    /// resumed operation registers a FRESH one (via <see cref="Start"/>/<see cref="Run"/>) when it
    /// actually restarts. This call only carries the app's opaque token across; it never resumes
    /// anything itself.
    /// </para>
    /// </summary>
    bool RequestResume(string id);
}
