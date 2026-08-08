namespace Shenora.Core.Ipc;

/// <summary>
/// Lifecycle state of a tracked operation. Crosses the wire as its camelCase name (<c>"running"</c>,
/// <c>"completed"</c>, …) for free — <see cref="IpcJson"/> already installs a camelCase
/// <c>JsonStringEnumConverter</c>, so no per-type wiring is needed.
/// </summary>
public enum OperationStatus
{
    /// <summary>In progress. The only status that accepts further progress reports.</summary>
    Running,

    /// <summary>
    /// Finished successfully. Terminal. <see cref="OperationInfo.Progress"/> is set to its own
    /// <c>Total</c> when one was ever reported (the honest "all of it") and otherwise left exactly
    /// as last reported — the kit never invents a number the app never gave it (see
    /// <see cref="IOperation.Complete"/>).
    /// </summary>
    Completed,

    /// <summary>Finished with a structured error (<see cref="OperationInfo.Error"/>). Terminal.</summary>
    Failed,

    /// <summary>Stopped on request — a normal outcome, not a fault. Terminal.</summary>
    Cancelled,

    // 🔴 NO `Waiting`, and its absence is D66's answer rather than an omission (2026-08-08). A request is
    // IN FLIGHT or DONE — the XHR model this whole subsystem is being folded into has no parked state, and
    // neither does this one now.
    //
    // It went because of what actually used it: the ONLY driver in the repo was app code wrapping a queued
    // MISSION, and no adopter drove `WAIT`/`RESUME`/`DISMISS` at all. So `waiting` was describing
    // host-initiated work the whole time, which is exactly the case D66 says does not fold into a request
    // — and that is why it never sat comfortably beside Running/Completed. Host work keeps the event stream
    // and cancel handle it already has.
}

/// <summary>
/// Human-facing text the HOST must never format itself: an untranslated fallback plus an app i18n
/// key and interpolation parameters — the same <c>{code, parameters}</c> shape as
/// <see cref="IpcError"/>, applied to labels instead of errors. The app renders; the kit only
/// carries the pieces (headless by design).
/// </summary>
/// <param name="Text">Untranslated fallback, for logs/dev or an app with no i18n layer.</param>
/// <param name="Key">The app's own i18n key (e.g. <c>"import.stage.upload"</c>).</param>
/// <param name="Parameters">Interpolation values for <paramref name="Key"/>.</param>
public sealed record OperationLabel(
    string? Text = null,
    string? Key = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>
/// How far a tracked operation has gotten, in the APP's own unit — never a kit-assumed percent
/// (generic-library audit, before publish: percent is not the mechanism, it is one way an app
/// happens to measure — bytes transferred against a known total, items processed against a known
/// total, an absolute count with no known denominator, or a genuine percent are all the SAME shape
/// here, distinguished only by whether <see cref="Total"/> is set).
/// </summary>
/// <param name="Value">How far along, in the app's own unit.</param>
/// <param name="Total">
/// The denominator, when one is known (e.g. total bytes, total item count). <c>null</c> means there
/// is NO known total — an absolute count with nothing to divide by (bytes streamed so far off a
/// chunked response, say) — never zero. A UI renders a ratio when this is set and a bare figure
/// otherwise.
/// </param>
/// <param name="Unit">
/// App-defined, like <see cref="OperationOptions.Kind"/> (e.g. <c>"bytes"</c>, <c>"files"</c>,
/// <c>"percent"</c>) — the kit never interprets it and ships no taxonomy of units.
/// </param>
public sealed record OperationProgress(double Value, double? Total = null, string? Unit = null);

/// <summary>Inputs to starting a tracked operation.</summary>
public sealed record OperationOptions
{
    /// <summary>
    /// App-defined operation kind (e.g. <c>"IMPORT"</c>, <c>"DEPLOY"</c>, <c>"SCAN"</c>). The kit
    /// ships no enum here — what an operation IS stays the app's domain, never the kit's.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Optional display title, i18n-ready.</summary>
    public OperationLabel? Title { get; init; }

    /// <summary>App-defined scope; also drives the published event's scope and client-side filtering.</summary>
    public string? Scope { get; init; }

    // NO ResumePayload (0.2.0 design pass, D1). An opaque app checkpoint token used to live here, to
    // support announcing a crash-interrupted operation the kit never started. That was single-app
    // provenance the design doc itself flagged, and it cost more than it carried: because the field
    // was APP-controlled, nothing could reliably answer "does this entry still have a live body?",
    // and every attempt to (a second status, then this field, then an internal provenance flag)
    // produced a defect. Crash recovery belongs to the app — it owns the checkpoint, and a resumed
    // run is a fresh Start()/Run() like any other. Carry your token in your own store.

    /// <summary>Whether a client should be offered a cancel affordance for this operation.</summary>
    public bool Cancellable { get; init; }

    /// <summary>
    /// Initial progress, in the app's own unit — see <see cref="OperationProgress"/>. Passed through
    /// UNCHANGED: the kit does not clamp, validate, or otherwise interpret <see cref="OperationProgress.Value"/>/
    /// <see cref="OperationProgress.Total"/>/<see cref="OperationProgress.Unit"/> (generic-library audit,
    /// before publish — silently rewriting an app's own data, the previous 0–100 clamp, is worse than
    /// passing it through: a value above its own total is the app's bug to see, not the kit's to hide).
    /// Null (the default) means indeterminate, not zero.
    /// </summary>
    public OperationProgress? Progress { get; init; }
}

/// <summary>
/// A full snapshot of one tracked operation — the operations-module event payload AND the element
/// type of a LIST response. Every transition (start, progress, terminal) publishes one of these, so
/// a client folds by <see cref="Id"/> with no cross-type ordering hazard: last write wins.
/// </summary>
public sealed record OperationInfo
{
    /// <summary>Unique per operation instance.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The OWNING module — supplied by the registry from the caller's own module, never a
    /// hand-typed literal. The event itself is published under the operations module; this is
    /// how a consumer tells them apart.
    /// </summary>
    public required string Module { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Kind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Scope"/>.</summary>
    public string? Scope { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public OperationStatus Status { get; init; }

    /// <summary>
    /// How far along, in the app's own unit — see <see cref="OperationProgress"/>. Passed through from
    /// whatever was last reported, never clamped or otherwise rewritten by the kit. Null = indeterminate.
    /// </summary>
    public OperationProgress? Progress { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Title"/>.</summary>
    public OperationLabel? Title { get; init; }

    /// <summary>The latest label from a progress report, if any.</summary>
    public OperationLabel? Detail { get; init; }

    /// <summary>
    /// Structured failure — set only when <see cref="Status"/> is <see cref="OperationStatus.Failed"/>.
    /// Never raw exception text: an unexpected failure crosses as <see cref="IpcErrorCodes.UnknownError"/>
    /// plus the exception type name, with the detail logged host-side only.
    /// </summary>
    public IpcError? Error { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Cancellable"/>.</summary>
    public bool Cancellable { get; init; }

    /// <summary>When the operation was started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When a terminal transition happened; null while <see cref="Status"/> is Running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}
