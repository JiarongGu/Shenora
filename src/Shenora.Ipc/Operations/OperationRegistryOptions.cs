namespace Shenora.Ipc;

/// <summary>Inputs for <see cref="OperationRegistry"/>. Validated at construction — a bad value names itself.</summary>
public sealed class OperationRegistryOptions
{
    /// <summary>
    /// The module the registry publishes <see cref="OperationEvents.Updated"/> under — distinct
    /// from the OWNING module carried inside each <see cref="OperationInfo"/>. One subscription,
    /// one snapshot source, one place for a client-side filter to allow or deny.
    /// </summary>
    public string ModuleName { get; init; } = "OPERATIONS";

    /// <summary>
    /// The progress-report frame rate: at most one emission per window, plus a trailing emit so
    /// the final value always lands. <see cref="TimeSpan.Zero"/> disables throttling entirely
    /// (every report emits). Lifecycle transitions (start, complete, fail, cancel) are never
    /// throttled by this.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Cap on retained FINISHED entries (oldest dropped first); running (and WAITING-band) work is
    /// never pruned.
    /// <para>
    /// <b>Known limit, recorded rather than solved (generic-library audit finding 5):</b> ONE global
    /// cap across every module and scope — there is no per-module or per-scope bounding seam, so a
    /// chatty module's finished history can crowd out a quiet one's under the same registry. No
    /// consumer has asked for per-module bounding; recording this is how the next one that does turns
    /// into evidence for a real seam instead of a re-argument from scratch.
    /// </para>
    /// </summary>
    public int MaxHistory { get; init; } = 50;

    /// <summary>Clock used for <see cref="OperationInfo.StartedAt"/>/<see cref="OperationInfo.FinishedAt"/> and progress throttling — overridable in tests for deterministic timing.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}
