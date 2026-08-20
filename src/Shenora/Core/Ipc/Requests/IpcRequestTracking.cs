namespace Shenora.Core.Ipc;

/// <summary>
/// What a request is doing: IN FLIGHT or DONE. Crosses the wire as its camelCase name
/// (<c>"running"</c>, <c>"completed"</c>, …) — <see cref="IpcJson"/> installs the converter.
/// </summary>
public enum IpcRequestState
{
    /// <summary>In flight. The only state that accepts progress.</summary>
    Running,

    /// <summary>Answered successfully. Terminal.</summary>
    Completed,

    /// <summary>Answered with a structured error (<see cref="IpcRequestStatus.Error"/>). Terminal.</summary>
    Failed,

    /// <summary>Aborted. A normal result, not a fault. Terminal.</summary>
    Cancelled,
}

/// <summary>
/// How far a request has got, in the APP's own unit — never a kit-assumed percent, and never clamped
/// or validated.
/// </summary>
/// <param name="Value">How far along, in the app's own unit.</param>
/// <param name="Total">
/// The denominator when one is known. ⚠ <c>null</c> means there is NO known total — an absolute count
/// with nothing to divide by — never zero.
/// </param>
/// <param name="Unit">App-defined (<c>"bytes"</c>, <c>"files"</c>, …); the kit interprets it not at all.</param>
public sealed record IpcProgress(double Value, double? Total = null, string? Unit = null);

/// <summary>
/// Human-facing text the HOST must never format itself: an untranslated fallback plus an app i18n key
/// and interpolation parameters — the same <c>{code, parameters}</c> shape as <see cref="IpcError"/>.
/// </summary>
/// <param name="Text">Untranslated fallback, for logs/dev or an app with no i18n layer.</param>
/// <param name="Key">The app's own i18n key (e.g. <c>"import.stage.upload"</c>).</param>
/// <param name="Parameters">Interpolation values for <paramref name="Key"/>.</param>
public sealed record IpcLabel(
    string? Text = null,
    string? Key = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>
/// One in-flight (or recently finished) request, as the page sees it.
/// <para>
/// 🔴 <b>One identity, end to end (D66).</b> Every field except the state comes from the request itself
/// — <see cref="Id"/>, <see cref="Type"/>, <see cref="Scope"/> and <see cref="StartedAt"/> are the
/// request's own — so a page has nothing to correlate.
/// </para>
/// </summary>
public sealed record IpcRequestStatus
{
    /// <summary>The REQUEST's own id (<see cref="IpcRequest.Id"/>).</summary>
    public required string Id { get; init; }

    /// <summary>The module the request targeted.</summary>
    public required string Module { get; init; }

    /// <summary>The action within that module.</summary>
    public required string Type { get; init; }

    /// <summary>The request's own routing scope, echoed so a scoped store can filter.</summary>
    public string? Scope { get; init; }

    /// <summary>Where it has got to.</summary>
    public IpcRequestState State { get; init; }

    /// <summary>Last reported progress, passed through UNCHANGED — never clamped or rewritten.</summary>
    public IpcProgress? Progress { get; init; }

    /// <summary>The latest label from a progress report, if any.</summary>
    public IpcLabel? Detail { get; init; }

    /// <summary>
    /// Structured failure — set only when <see cref="State"/> is <see cref="IpcRequestState.Failed"/>.
    /// Never raw exception text.
    /// </summary>
    public IpcError? Error { get; init; }

    /// <summary>When the request was sent (<see cref="IpcRequest.Timestamp"/>).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When it reached a terminal state; null while running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>Event names the request tracker publishes. Constants, so an app matches by symbol.</summary>
public static class IpcRequestEvents
{
    /// <summary>
    /// A full <see cref="IpcRequestStatus"/> snapshot — every transition uses this ONE type, so folding is
    /// last-write-wins by id with no cross-type ordering hazard.
    /// </summary>
    public const string Updated = "REQUEST_UPDATED";

    /// <summary>
    /// One or more request ids left the tracker with no corresponding <see cref="Updated"/> snapshot —
    /// history eviction and <c>CLEAR_FINISHED</c>. Payload is <c>{ requestIds: string[] }</c>, a BATCH;
    /// a client folds it by deleting those ids. Emitted with no scope (global).
    /// </summary>
    public const string Removed = "REQUEST_REMOVED";
}

/// <summary>
/// The live view of requests that outlived their own send: what is in flight, and the ability to abort one.
/// <para>
/// <b>There is nothing to declare.</b> Every request is tracked from the moment it is dispatched, and the
/// GRACE PERIOD decides which ones the page ever hears about.
/// </para>
/// <para>
/// ⚠ <b>Host-initiated work does NOT belong here</b> (D66). A scheduled or recovered mission has no request
/// behind it, so it reports on its own event stream — see <c>MissionEvents</c>.
/// </para>
/// </summary>
public interface IIpcRequestTracker
{
    /// <summary>
    /// Snapshot of known requests: in flight first (oldest first), then retained finished history
    /// (newest first), capped by <c>MaxHistory</c>.
    /// <para>
    /// <paramref name="scope"/> follows <see cref="Shenora.Core.Events.IEventBus"/>'s scope rule rather than
    /// strict equality: <c>null</c> returns every scope, and a scope-less request matches ANY requested one.
    /// </para>
    /// </summary>
    IReadOnlyList<IpcRequestStatus> GetAll(string? module = null, string? scope = null);

    /// <summary>
    /// Abort a request in flight: cancels the token the route runs under, then records
    /// <see cref="IpcRequestState.Cancelled"/>. Returns false, changing nothing, for an unknown id or one
    /// already finished.
    /// </summary>
    bool Cancel(string requestId);

    /// <summary>Drop retained finished history, filtered exactly like <see cref="GetAll"/>.</summary>
    void ClearFinished(string? module = null, string? scope = null);

    /// <summary>
    /// Begin tracking <paramref name="request"/>. Called by the dispatch path, never by app code.
    /// <para>
    /// ⚠ <b>Publishes NOTHING yet.</b> The snapshot goes out only if the request is still running when the
    /// grace period expires — see <see cref="IpcRequestTrackerOptions.GracePeriod"/>.
    /// </para>
    /// </summary>
    /// <param name="request">The request being dispatched.</param>
    /// <param name="cancellationToken">The caller's lifetime, linked into the scope's own token.</param>
    IIpcRequestScope Begin(IpcRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A request's tracking scope for as long as it is in flight. Disposing it completes the request.
/// </summary>
public interface IIpcRequestScope : IDisposable
{
    /// <summary>The request's own id.</summary>
    string RequestId { get; }

    /// <summary>
    /// The token the route should observe. Cancelled by <see cref="IIpcRequestTracker.Cancel"/> and by the
    /// caller's own lifetime.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Report progress on this request. Silent while inside the grace period.</summary>
    void Report(IpcProgress? progress = null, IpcLabel? detail = null);

    /// <summary>Record a structured failure. Terminal; further calls are no-ops.</summary>
    void Fail(IpcError error);
}
