namespace Shenora.Core.Ipc;

/// <summary>
/// A handle to one tracked operation, returned by <see cref="IOperationRegistry.Start"/>. Every
/// mutating call publishes a fresh <see cref="OperationInfo"/> snapshot (see
/// <see cref="OperationEvents.Updated"/>) — the registry owns the state, this handle is just a
/// convenient way to mutate it without re-passing the id everywhere.
/// </summary>
public interface IOperation
{
    /// <summary>Same id as the <see cref="OperationInfo"/> this operation publishes.</summary>
    string Id { get; }

    /// <summary>
    /// The operation's OWN token — never a request's. Work handed off to the background outlives
    /// the request that started it, so capturing a request's token and observing it instead would
    /// kill the operation the moment that request's lifetime ends (e.g. a page navigating away).
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Report progress and/or a detail label. A null argument leaves that field unchanged — call
    /// with just one of the two to update only it. Ignored once the operation has reached a
    /// terminal state (idempotent-finish, not a progress race).
    /// </summary>
    /// <param name="progress">
    /// In the app's own unit — see <see cref="OperationProgress"/>. Passed through unchanged: the kit
    /// does not clamp, validate, or otherwise interpret it (generic-library audit, before publish —
    /// percent is not the mechanism, it is one app's unit; <see cref="OperationProgress.Total"/> is the
    /// denominator when known, null when there isn't one, and <see cref="OperationProgress.Unit"/> is an
    /// app-defined string like <see cref="OperationOptions.Kind"/>).
    /// </param>
    /// <param name="detail">Optional human-facing detail label.</param>
    void Report(OperationProgress? progress = null, OperationLabel? detail = null);

    /// <summary>
    /// Finish successfully. Idempotent — a no-op once terminal.
    /// <see cref="OperationInfo.Progress"/> is set to its own <see cref="OperationProgress.Total"/> when
    /// one was ever reported (the honest "all of it"); otherwise it is left exactly as last reported —
    /// the kit never invents a number the app never gave it (generic-library audit, before publish: the
    /// previous behavior forced <c>Progress = 100</c> unconditionally, which assumed every consumer
    /// measures in percent).
    /// </summary>
    /// <remarks>Accepts <see cref="OperationStatus.Running"/> OR <see cref="OperationStatus.Waiting"/> — a waiting operation can still complete once the human unblocks it out of band.</remarks>
    void Complete();

    /// <summary>
    /// Finish with a structured error — never raw exception text. Safe to call after another
    /// terminal transition (the "<see cref="Complete"/> at the end + <c>Fail</c> in the catch"
    /// pattern): idempotent, a no-op once terminal.
    /// </summary>
    /// <param name="code">Error code / i18n key (e.g. <c>"IMPORT_FAILED"</c>).</param>
    /// <param name="parameters">Optional interpolation values for the client's translation.</param>
    /// <param name="message">Optional untranslated message for host logs only; never sent as the code.</param>
    /// <remarks>Accepts <see cref="OperationStatus.Running"/> OR <see cref="OperationStatus.Waiting"/> — a waiting operation can still fail on a deadline.</remarks>
    void Fail(string code, IReadOnlyDictionary<string, string>? parameters = null, string? message = null);

    /// <summary>Finish, carrying the app's own structured failure through unchanged. Idempotent.</summary>
    /// <remarks>Accepts <see cref="OperationStatus.Running"/> OR <see cref="OperationStatus.Waiting"/> — a waiting operation can still fail on a deadline.</remarks>
    void Fail(OperationException error);

    /// <summary>
    /// Cancel this operation: cancels <see cref="CancellationToken"/> first, then transitions to
    /// <see cref="OperationStatus.Cancelled"/> — a body observing the token sees the cancellation
    /// rather than racing a completed-then-cancelled flip. Idempotent.
    /// <para>
    /// Deliberately UNCONDITIONAL — unlike <see cref="IOperationRegistry.Cancel(string)"/>, the
    /// registry's public BY-ID route (what an external client's "cancel this operation" request goes
    /// through), which refuses when <see cref="OperationOptions.Cancellable"/> is false. That refusal
    /// exists because an arbitrary caller holding only an id has no standing to stop work it never
    /// started; this method is called through the handle <see cref="IOperationRegistry.Start"/>
    /// returned to the operation's OWN owner, so there is no such permission question to ask — "the
    /// work is over" is a fact to record, not a request to grant. <see cref="OperationRegistry.Run"/>'s
    /// own guarded body relies on exactly this: its catch calls this method (via the same handle) when
    /// the work throws <see cref="OperationCanceledException"/>, and a non-<c>Cancellable</c> operation
    /// must still be able to end as <see cref="OperationStatus.Cancelled"/> rather than being stranded
    /// <see cref="OperationStatus.Running"/> forever.
    /// </para>
    /// <para>
    /// This is also the exit <see cref="OperationRegistry"/>'s owner-path terminal cancel accepts from
    /// ANY non-terminal status (§5A.2) — not only <see cref="OperationStatus.Running"/> — because a
    /// <see cref="OperationStatus.Waiting"/> body is still parked on this same token and must be able to
    /// unwind the same way a running one does.
    /// </para>
    /// </summary>
    void Cancel();

    /// <summary>
    /// Wait: <see cref="OperationStatus.Running"/> → <see cref="OperationStatus.Waiting"/> (§5A.3),
    /// for work that stops progressing WITHOUT crashing — the app names why with
    /// <paramref name="reason"/>, from a host discovering its own blocker (expired cloud credentials, a
    /// throttling provider, DNS not yet propagated) to a queue front-end parking a just-started
    /// operation (<c>"queued"</c>) before real work begins. Called by the operation's OWN owner once it
    /// has actually stopped — never by the kit itself. Deliberately distinct from
    /// <see cref="IOperationRegistry.RequestWait"/> (generic-library audit finding 3, renamed from
    /// <c>RequestPause</c>): that is the client ASKING; this is the state actually changing — the same
    /// ASK/ACT split <see cref="IOperationRegistry.RequestResume"/> already draws against
    /// <see cref="Resume"/>. A wait can originate either side of that split: the HOST's own discovery of
    /// a blocker calls this directly with no ask involved; a human clicking Pause on visible work goes
    /// through <c>RequestWait</c>/<c>WAIT</c> first, and the owner calls this once it has actually
    /// honored that ask.
    /// <para>
    /// Ignored (logged, no-op) unless the operation is currently <see cref="OperationStatus.Running"/>
    /// — the same honest-refusal shape as every other transition here.
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// App-defined, like <see cref="OperationOptions.Kind"/> (e.g. <c>"credentials"</c>/<c>"dns"</c>/
    /// <c>"queued"</c>/<c>"rate-limited"</c>) — the app's own taxonomy for what its UI offers; the kit
    /// never interprets it, and ships no reason enum. Optional (generic-library audit finding 5): a
    /// consumer whose wait is self-evident (the user clicked Pause) has nothing to name and should not
    /// have to invent a filler string just to call this.
    /// </param>
    /// <param name="detail">Optional human-facing detail label, same shape as <see cref="Report"/>'s.</param>
    void Wait(string? reason = null, OperationLabel? detail = null);

    /// <summary>
    /// Resume: <see cref="OperationStatus.Waiting"/> → <see cref="OperationStatus.Running"/>, clearing
    /// <see cref="OperationInfo.WaitReason"/> (§5A.3). Called by the operation's own owner once it has
    /// ACTUALLY resumed — never by the kit itself. Deliberately distinct from
    /// <see cref="IOperationRegistry.RequestResume"/>: that is the client ASKING; this is the state
    /// actually changing, the same split that fixed this branch's only Critical (§5A.4).
    /// <para>
    /// Ignored (logged, no-op) unless the operation is currently <see cref="OperationStatus.Waiting"/>.
    /// </para>
    /// </summary>
    void Resume();
}
