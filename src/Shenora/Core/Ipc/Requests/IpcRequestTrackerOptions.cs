using Microsoft.Extensions.Logging;

namespace Shenora.Core.Ipc;

/// <summary>Options for <see cref="IpcRequestTracker"/>. Validated at construction, not on first use.</summary>
public sealed class IpcRequestTrackerOptions
{
    /// <summary>
    /// The module the tracker's own routes and events live under. Renameable so it cannot collide with an
    /// app's own module.
    /// </summary>
    public string ModuleName { get; set; } = "SHENORA.REQUESTS";

    /// <summary>
    /// 🔴 <b>How long a request may run before the page is told anything at all.</b> A request that
    /// finishes inside this window emits NO notification whatsoever — no running snapshot, no progress,
    /// no completion. Every request is tracked, and this is what decides which ones the page hears about,
    /// so nothing has to be declared "long-running" at authoring time.
    /// <para>
    /// ⚠ <b>It never delays the RESPONSE</b> — it suppresses NOTIFICATIONS only. Parking the response
    /// here would add latency to every fast call in the app to save a notification nobody would see.
    /// </para>
    /// </summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Minimum gap between PROGRESS snapshots for one request once it is being announced. Terminal
    /// transitions are never throttled. Zero disables throttling.
    /// </summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How many FINISHED requests to retain for the page's history list. Oldest are evicted first, each
    /// eviction announced through <see cref="IpcRequestEvents.Removed"/>. ⚠ Only requests that were
    /// ANNOUNCED can enter history at all — one that finished inside <see cref="GracePeriod"/> was never
    /// told to anyone.
    /// </summary>
    public int MaxHistory { get; set; } = 50;

    /// <summary>Clock, injectable so tests drive the grace period and throttle deterministically.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Diagnostics sink. Guarded — a throwing sink cannot fault a dispatch.</summary>
    public ILogger? Log { get; set; }
}
