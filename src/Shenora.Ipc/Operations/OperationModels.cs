namespace Shenora.Ipc;

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

    /// <summary>
    /// Not progressing, awaiting a decision — the APP names why via
    /// <see cref="OperationInfo.WaitReason"/> (e.g. <c>"credentials"</c>/<c>"dns"</c>/
    /// <c>"queued"</c>/<c>"rate-limited"</c>), never a kit-owned reason enum. Not one of the
    /// terminal-finish statuses, and never pruned as history — a waiting entry is a pending offer,
    /// not something finished.
    /// <para>
    /// Reached two ways, told apart by <see cref="OperationInfo.ResumePayload"/> rather than by a
    /// second status (there is only one WAITING status — this collapses what used to be two,
    /// <c>Paused</c> and <c>Interrupted</c>, which every transition in this registry already treated
    /// as one band): <see cref="IOperation.Wait"/> on a live <see cref="Running"/> handle (no
    /// <see cref="OperationInfo.ResumePayload"/> — the body is still there, just stopped), or
    /// <see cref="IOperationRegistry.RegisterWaiting"/> announcing a crash-interrupted checkpoint
    /// directly (a non-empty <see cref="OperationInfo.ResumePayload"/> — no live body at all, the
    /// process that owned it is gone). Exits via <see cref="IOperation.Resume"/> (back to
    /// <see cref="Running"/>), <see cref="IOperationRegistry.Dismiss"/> (to
    /// <see cref="Cancelled"/>), or a direct
    /// <see cref="IOperation.Complete"/>/<see cref="IOperation.Fail(string, IReadOnlyDictionary{string, string}?, string?)"/>
    /// (a waiting operation can still fail on a deadline).
    /// </para>
    /// </summary>
    Waiting,
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

    /// <summary>
    /// Opaque app checkpoint token; presence is what makes an operation resumable after a crash.
    /// <para>
    /// There used to be a separate <c>Resumable</c> bool gating
    /// <see cref="IOperationRegistry.RegisterWaiting"/> — removed (generic-library audit finding
    /// 2) because it was consulted NOWHERE else: every entry it ever produced already forced it
    /// <c>true</c> to get past that same check, so the flag was a required-true tautology, not a
    /// choice. <see cref="IOperationRegistry.RegisterWaiting"/>'s own non-empty-payload
    /// requirement already expresses "this is resumable" — a second flag added no information. It also
    /// governed nothing for <see cref="IOperation.Wait"/>/<see cref="IOperation.Resume"/>: a
    /// <see cref="OperationStatus.Waiting"/> operation reached via <c>Wait</c> is resumable BY
    /// CONSTRUCTION (<see cref="IOperation.Resume"/> needs no flag), so a future re-add gating
    /// <see cref="IOperationRegistry.RequestResume"/>'s live-handle case on a resumable bool would
    /// silently break the ordinary wait/resume flow for an operation that — like most — never set one.
    /// </para>
    /// <para>
    /// This is also what <see cref="IOperationRegistry.RequestResume"/> keys its drop-vs-keep decision
    /// on now that <see cref="OperationStatus"/> carries only one WAITING value: non-null means this
    /// entry has no live handle (a reconstructed offer — either
    /// <see cref="IOperationRegistry.RegisterWaiting"/>'s checkpoint, or one the app itself attached
    /// here), so the entry is removed and the app starts fresh work; null means an ordinary live
    /// <see cref="IOperation.Wait"/> — the entry stays for the app's own <see cref="IOperation.Resume"/>
    /// to flip.
    /// </para>
    /// </summary>
    public string? ResumePayload { get; init; }
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
    /// Why the operation is (or WAS) <see cref="OperationStatus.Waiting"/> — an app-defined string,
    /// like <see cref="Kind"/> (e.g. <c>"credentials"</c>/<c>"dns"</c>/<c>"queued"</c>/
    /// <c>"rate-limited"</c>): the app's own taxonomy driving what its UI offers, never the kit's.
    /// Optional — a wait whose cause is self-evident (the user clicked Pause) has nothing to name.
    /// <para>
    /// Lifetime (coordinator ruling, D23's lifecycle-completion amendment — stated here so the
    /// asymmetry reads as intent, not an oversight): set when <see cref="IOperation.Wait"/> runs,
    /// CLEARED when <see cref="IOperation.Resume"/> runs (back to null), but RETAINED through a
    /// later terminal transition — <see cref="IOperation.Complete"/>, <see cref="IOperation.Fail(string, IReadOnlyDictionary{string, string}?, string?)"/>,
    /// or <see cref="IOperationRegistry.Dismiss"/> — reached directly from
    /// <see cref="OperationStatus.Waiting"/> without an intervening <c>Resume</c>. "Failed while waiting
    /// on credentials" is useful history for whoever reads the finished entry; only an actual
    /// <c>Resume</c> means the app has moved past the reason, which is why clearing is <c>Resume</c>'s
    /// job and not automatic on every exit from <see cref="OperationStatus.Waiting"/>.
    /// </para>
    /// </summary>
    public string? WaitReason { get; init; }

    /// <summary>
    /// Structured failure — set only when <see cref="Status"/> is <see cref="OperationStatus.Failed"/>.
    /// Never raw exception text: an unexpected failure crosses as <see cref="IpcErrorCodes.UnknownError"/>
    /// plus the exception type name, with the detail logged host-side only.
    /// </summary>
    public IpcError? Error { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Cancellable"/>.</summary>
    public bool Cancellable { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.ResumePayload"/>.</summary>
    public string? ResumePayload { get; init; }

    /// <summary>When the operation was started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When a terminal transition happened; null while <see cref="Status"/> is Running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}
