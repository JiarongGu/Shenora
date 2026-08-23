using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Modules.Platform;

/// <summary>
/// Tells the page when the APP went away and came back, and — the part it cannot measure itself — how
/// long it was gone.
/// <para>
/// 🔴 <b>Use <c>document.visibilitychange</c> for "am I on screen".</b> It fires on both shells and is
/// the web platform's own answer; a kit event duplicating it would be worse, because it arrives later
/// and over IPC. This exists for the two things a hidden page genuinely cannot know:
/// </para>
/// <list type="number">
/// <item>
/// <b>HOW LONG it was away, measured by a clock that was not throttled.</b> A backgrounded page's timers
/// are throttled and its process may be frozen outright, so <c>Date.now()</c> deltas taken across the gap
/// are unreliable — and the decision an app actually makes on resume turns on the duration. Three seconds
/// in the notification shade needs no reconnect; forty minutes means the socket is dead and whatever it
/// was paired with may have come or gone.
/// </item>
/// <item>
/// <b>That this was an ACTIVITY transition</b> — MAUI's <c>Window.Stopped</c>/<c>Resumed</c>, i.e. the
/// user leaving the app — rather than anything else that can hide a document.
/// </item>
/// </list>
/// <para>
/// ⚠ <b>It reports; it does not act.</b> What to do about a long absence is the app's — reconnect,
/// re-probe, refetch, or nothing. The kit has no way to know what the page was holding.
/// </para>
/// </summary>
/// <remarks>
/// Portable: the shell supplies the two transitions and this owns the timing, so the arithmetic that
/// decides an app's reconnect is testable with no device. <c>MobileAppLifecycle</c> is the platform half.
/// </remarks>
public sealed class AppLifecycle
{
    /// <summary>The module these events are published under.</summary>
    public const string Module = "SHENORA.LIFECYCLE";

    /// <summary>Event: the app left the foreground. No payload.</summary>
    public const string StoppedType = "STOPPED";

    /// <summary>Event: the app came back, carrying an <see cref="AppLifecycleReport"/>.</summary>
    public const string ResumedType = "RESUMED";

    private readonly IEventBus _events;
    private readonly ILogger? _log;
    private readonly Lock _gate = new();

    // Monotonic, so a clock change mid-background cannot produce a negative or absurd duration — which a
    // page WOULD act on, since the whole payload is a number it branches against a threshold.
    private long? _stoppedAt;

    /// <param name="events">Where the transitions are published. The pump forwards them to the page.</param>
    /// <param name="log">Optional diagnostics.</param>
    public AppLifecycle(IEventBus events, ILogger? log = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _log = log;
    }

    /// <summary>
    /// The app left the foreground. Call from the shell's stop signal — on MAUI that is
    /// <c>Window.Stopped</c>.
    /// <para>
    /// ⚠ <b><c>Stopped</c>, not <c>Deactivated</c>.</b> The latter also fires for a dialog or the
    /// notification shade, which would report the app as backgrounded while it is still on screen — and
    /// a page reconnecting a socket every time a permission prompt appears is worse than one that never
    /// reconnects at all.
    /// </para>
    /// </summary>
    public void ReportStopped()
    {
        lock (_gate)
        {
            // Idempotent: a second stop without an intervening resume keeps the FIRST timestamp, so the
            // duration still covers the whole absence rather than restarting mid-way.
            _stoppedAt ??= Stopwatch.GetTimestamp();
        }
        Log("lifecycle: the app left the foreground");
        _events.Emit(Module, StoppedType);
    }

    /// <summary>
    /// The app came back. Call from the shell's resume signal — on MAUI that is <c>Window.Resumed</c>.
    /// <para>
    /// ⚠ A resume with no preceding stop reports <see cref="AppLifecycleReport.BackgroundMilliseconds"/>
    /// as null rather than zero. Zero would be a measurement, and this is the absence of one — the first
    /// resume after launch is exactly that case, and a page treating it as "away for 0 ms" would skip the
    /// reconnect it does on every other resume.
    /// </para>
    /// </summary>
    public void ReportResumed()
    {
        double? awayMs;
        lock (_gate)
        {
            awayMs = _stoppedAt is { } since
                ? Stopwatch.GetElapsedTime(since).TotalMilliseconds
                : null;
            _stoppedAt = null;
        }

        Log(awayMs is { } ms
            ? $"lifecycle: the app returned after {ms:0}ms in the background"
            : "lifecycle: the app is in the foreground (no preceding stop — a first launch or a resume "
            + "the shell did not report)");
        _events.Emit(Module, ResumedType, new AppLifecycleReport(awayMs));
    }

    private void Log(string message) => AppCallback.Log(_log, () => $"[Shenora] {message}");
}

/// <summary>
/// What an <see cref="AppLifecycle.ResumedType"/> event carries.
/// </summary>
/// <param name="BackgroundMilliseconds">
/// How long the app was away, or null when there was no preceding stop to measure from — a first launch,
/// or a shell that reported only the resume. ⚠ Null is not zero: see
/// <see cref="AppLifecycle.ReportResumed"/>.
/// </param>
public sealed record AppLifecycleReport(double? BackgroundMilliseconds);
