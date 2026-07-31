namespace Shenora.Ipc;

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
    void Report(int? progress = null, OperationLabel? detail = null);

    /// <summary>Finish successfully. Forces <see cref="OperationInfo.Progress"/> to 100. Idempotent — a no-op once terminal.</summary>
    void Complete();

    /// <summary>
    /// Finish with a structured error — never raw exception text. Safe to call after another
    /// terminal transition (the "<see cref="Complete"/> at the end + <c>Fail</c> in the catch"
    /// pattern): idempotent, a no-op once terminal.
    /// </summary>
    /// <param name="code">Error code / i18n key (e.g. <c>"IMPORT_FAILED"</c>).</param>
    /// <param name="parameters">Optional interpolation values for the client's translation.</param>
    /// <param name="message">Optional untranslated message for host logs only; never sent as the code.</param>
    void Fail(string code, IReadOnlyDictionary<string, string>? parameters = null, string? message = null);

    /// <summary>Finish, carrying the app's own structured failure through unchanged. Idempotent.</summary>
    void Fail(OperationException error);

    /// <summary>
    /// Cancel this operation: cancels <see cref="CancellationToken"/> first, then transitions to
    /// <see cref="OperationStatus.Cancelled"/> — a body observing the token sees the cancellation
    /// rather than racing a completed-then-cancelled flip. Idempotent.
    /// </summary>
    void Cancel();
}
