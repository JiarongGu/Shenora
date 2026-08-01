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

    /// <summary>Finished successfully. Terminal — <see cref="OperationInfo.Progress"/> is forced to 100.</summary>
    Completed,

    /// <summary>Finished with a structured error (<see cref="OperationInfo.Error"/>). Terminal.</summary>
    Failed,

    /// <summary>Stopped on request — a normal outcome, not a fault. Terminal.</summary>
    Cancelled,

    /// <summary>
    /// A crash-interrupted, resumable operation announced from the app's own checkpoint. Not
    /// actively running, but not one of the terminal-finish statuses above either — a pending
    /// resume offer, distinct from finished history.
    /// </summary>
    Interrupted,

    /// <summary>
    /// Stopped mid-flight WITHOUT crashing, awaiting a decision (expired cloud credentials, a
    /// throttling provider, DNS not yet propagated, a migration awaiting confirmation) — the
    /// WAITING band alongside <see cref="Interrupted"/> (§5A.2): not progressing, but not one of
    /// the terminal-finish statuses either, and never pruned as history. Reached from
    /// <see cref="Running"/> via <see cref="IOperation.Pause"/>; exits via
    /// <see cref="IOperation.Resume"/> (back to <see cref="Running"/>),
    /// <see cref="IOperationRegistry.Dismiss"/> (to <see cref="Cancelled"/>), or a direct
    /// <see cref="IOperation.Complete"/>/<see cref="IOperation.Fail(string, IReadOnlyDictionary{string, string}?, string?)"/>
    /// (a paused deploy can still fail on a deadline).
    /// </summary>
    Paused,
}

/// <summary>
/// Human-facing text the HOST must never format itself: an untranslated fallback plus an app i18n
/// key and interpolation parameters — the same <c>{code, parameters}</c> shape as
/// <see cref="IpcError"/>, applied to labels instead of errors. The app renders; the kit only
/// carries the pieces (headless by design).
/// </summary>
/// <param name="Text">Untranslated fallback, for logs/dev or an app with no i18n layer.</param>
/// <param name="Key">The app's own i18n key (e.g. <c>"deploy.stage.upload"</c>).</param>
/// <param name="Parameters">Interpolation values for <paramref name="Key"/>.</param>
public sealed record OperationLabel(
    string? Text = null,
    string? Key = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

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

    /// <summary>Initial progress; null (the default) means indeterminate, not zero.</summary>
    public int? Progress { get; init; }

    /// <summary>
    /// Whether this operation can later be re-announced as interrupted-and-resumable after a crash.
    /// <para>
    /// Governs ONLY the crash-checkpoint path (<see cref="IOperationRegistry.RegisterInterrupted"/>) —
    /// NOT <see cref="IOperation.Pause"/>/<see cref="IOperation.Resume"/> (coordinator ruling, D23's
    /// lifecycle-completion amendment). A <see cref="OperationStatus.Paused"/> operation is resumable
    /// BY CONSTRUCTION — <see cref="IOperation.Resume"/> exists and requires no flag — so gating
    /// <see cref="IOperationRegistry.RequestResume"/>'s Paused case on this property would silently
    /// break the ordinary pause/resume flow for an operation that (like most) never set it. Do not
    /// re-add that check thinking it was an oversight; it was checked against the test suite and
    /// deliberately dropped when this property's only consumer used to be <c>RegisterInterrupted</c>'s
    /// entry (which already throws if this is false, making the old check vacuous there too).
    /// </para>
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>Opaque app checkpoint token carried on a resumable operation; the kit never reads it.</summary>
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

    /// <summary>0–100, clamped; null = indeterminate.</summary>
    public int? Progress { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Title"/>.</summary>
    public OperationLabel? Title { get; init; }

    /// <summary>The latest label from a progress report, if any.</summary>
    public OperationLabel? Detail { get; init; }

    /// <summary>
    /// Why the operation is (or WAS) <see cref="OperationStatus.Paused"/> — an app-defined string,
    /// like <see cref="Kind"/> (e.g. <c>"credentials"</c>/<c>"transient"</c>/<c>"dns"</c>/
    /// <c>"migration"</c>): the app's own taxonomy driving what its UI offers, never the kit's.
    /// <para>
    /// Lifetime (coordinator ruling, D23's lifecycle-completion amendment — stated here so the
    /// asymmetry reads as intent, not an oversight): set when <see cref="IOperation.Pause"/> runs,
    /// CLEARED when <see cref="IOperation.Resume"/> runs (back to null), but RETAINED through a
    /// later terminal transition — <see cref="IOperation.Complete"/>, <see cref="IOperation.Fail(string, IReadOnlyDictionary{string, string}?, string?)"/>,
    /// or <see cref="IOperationRegistry.Dismiss"/> — reached directly from
    /// <see cref="OperationStatus.Paused"/> without an intervening <c>Resume</c>. "Failed while paused
    /// waiting on credentials" is useful history for whoever reads the finished entry; only an actual
    /// <c>Resume</c> means the app has moved past the reason, which is why clearing is <c>Resume</c>'s
    /// job and not automatic on every exit from <see cref="OperationStatus.Paused"/>.
    /// </para>
    /// </summary>
    public string? PauseReason { get; init; }

    /// <summary>
    /// Structured failure — set only when <see cref="Status"/> is <see cref="OperationStatus.Failed"/>.
    /// Never raw exception text: an unexpected failure crosses as <see cref="IpcErrorCodes.UnknownError"/>
    /// plus the exception type name, with the detail logged host-side only.
    /// </summary>
    public IpcError? Error { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Cancellable"/>.</summary>
    public bool Cancellable { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.Resumable"/>.</summary>
    public bool Resumable { get; init; }

    /// <summary>Echoes <see cref="OperationOptions.ResumePayload"/>.</summary>
    public string? ResumePayload { get; init; }

    /// <summary>When the operation was started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When a terminal transition happened; null while <see cref="Status"/> is Running.</summary>
    public DateTimeOffset? FinishedAt { get; init; }
}
