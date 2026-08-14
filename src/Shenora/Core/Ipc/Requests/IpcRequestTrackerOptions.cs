namespace Shenora.Core.Ipc;

/// <summary>Options for <see cref="IpcRequestTracker"/>. Validated at construction, not on first use.</summary>
public sealed class IpcRequestTrackerOptions
{
    /// <summary>
    /// The module the tracker's own routes and events live under. Renameable so it cannot collide with an
    /// app's own module; the duplicate-module guard catches a collision at composition.
    /// </summary>
    public string ModuleName { get; set; } = "SHENORA.REQUESTS";

    /// <summary>
    /// 🔴 <b>How long a request may run before the page is told anything at all.</b> A request that finishes
    /// inside this window emits NO notification whatsoever — the response was the answer, and nobody wanted
    /// a spinner for 5 ms of work.
    /// <para>
    /// <b>This is what replaced the declaration.</b> There used to be a <c>Run()</c> that a module author
    /// called to say "this one is long-running", which is a judgement made at authoring time about
    /// something only the clock knows at run time. Now every request is tracked and the clock decides.
    /// </para>
    /// <para>
    /// 50 ms is not a new number: it is <c>NotificationPumpOptions.FlushInterval</c>'s own default, the
    /// family's measured sweet spot, and it is also roughly the threshold below which a human does not want
    /// a progress indicator. Lowering it makes a progress feed more fluent AND makes "this is taking a
    /// while" fire sooner — a coupling that is correct rather than incidental.
    /// </para>
    /// <para>
    /// ⚠ <b>It never delays the RESPONSE.</b> This suppresses NOTIFICATIONS only. A 5 ms request still
    /// answers at 5 ms; parking the response here would add latency to every fast call in the app to save
    /// a notification nobody would have seen.
    /// </para>
    /// </summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Minimum gap between PROGRESS snapshots for one request once it is being announced. A busy body can
    /// report hundreds of times a second; the page only ever renders the last one. Terminal transitions are
    /// never throttled. Zero disables throttling.
    /// </summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How many FINISHED requests to retain for the page's history list. Oldest are evicted first, each
    /// eviction announced through <see cref="IpcRequestEvents.Removed"/> so a long-lived client store
    /// mirrors a bounded list rather than growing forever.
    /// <para>
    /// ⚠ Only requests that were ANNOUNCED can enter history at all — a request that finished inside the
    /// grace period was never told to anyone, so there is nothing to retain or evict.
    /// </para>
    /// </summary>
    public int MaxHistory { get; set; } = 50;

    /// <summary>Clock, injectable so tests drive the grace period and throttle deterministically.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Diagnostics sink. Guarded — a throwing sink cannot fault a dispatch.</summary>
    public Action<string>? Log { get; set; }
}
