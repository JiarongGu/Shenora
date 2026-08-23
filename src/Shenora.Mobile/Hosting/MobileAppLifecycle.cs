using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Shenora.Modules.Platform;

namespace Shenora.Mobile;

/// <summary>
/// Feeds a MAUI <see cref="Window"/>'s foreground transitions into <see cref="AppLifecycle"/>, so the
/// page learns how long the app was away.
/// <para>
/// Both shells, no <c>#if</c>: <c>Stopped</c> and <c>Resumed</c> are MAUI's own, mapping to
/// <c>onStop</c>/<c>onStart</c> on Android and <c>didEnterBackground</c>/<c>willEnterForeground</c> on
/// iOS. ✅ Measured end to end on Android (API 36, backgrounded with HOME): one stop, one resume, and the
/// page received the duration.
/// </para>
/// </summary>
/// <remarks>
/// 🔴 <c>Stopped</c>/<c>Resumed</c>, NOT <c>Deactivated</c>/<c>Activated</c> — the same choice the
/// background-playback transfer documents, for the same reason: the latter pair also fires for a dialog,
/// a permission prompt or the notification shade, so an app would report itself backgrounded while still
/// on screen.
/// <para>
/// ⚠ <b>ONE REPORTER PER PROCESS.</b> MAUI's <see cref="Window"/> is process-scoped and outlives the
/// page, so a second reporter — which a configuration change produces, by building a new page whose
/// constructor makes one while the old page's is still attached — reports every transition TWICE. The
/// old page's <c>Unloaded</c> does not reliably run first (measured on the back gesture, where the same
/// shape put two callbacks on one dispatcher), so the newcomer displaces the incumbent rather than
/// trusting the order. <see cref="AppForegroundTracker"/> then drops whatever still arrives twice.
/// </para>
/// </remarks>
public sealed class MobileAppLifecycle : IDisposable
{
    private readonly AppForegroundTracker _foreground;
    private readonly ILogger? _log;
    private readonly Window _window;
    private readonly EventHandler _onStopped;
    private readonly EventHandler _onResumed;
    private bool _disposed;

    /// <summary>The reporter currently owning the process — see the class remarks.</summary>
    private static MobileAppLifecycle? _live;

    /// <param name="window">The page's window. Its transitions are what get reported.</param>
    /// <param name="lifecycle">The reporter. Register it with <c>AddShenoraAppLifecycle</c>.</param>
    /// <param name="log">
    /// Optional diagnostics. ⚠ <b>Pass one.</b> The failure mode here is a transition that is never
    /// reported, and without a log nothing in the process can say whether the shell ever saw one — which
    /// is indistinguishable from an app that simply never went away. It cost an adopter a day.
    /// </param>
    public MobileAppLifecycle(Window window, AppLifecycle lifecycle, ILogger? log = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(lifecycle);
        _log = log;
        _foreground = new AppForegroundTracker(lifecycle);

        Interlocked.Exchange(ref _live, this)?.Dispose();

        // ⚠ Deliberately NOT async lambdas. An `async` handler on an event is `async void`, so an escape
        // is an unhandled exception on the UI thread rather than a failed report — which on Android
        // crosses JNI and kills the process. Both bodies below are synchronous and cannot throw: the
        // tracker only compares a bool, `AppCallback.Log` guards the app's sink, and `Emit` is
        // fire-and-forget and guards every subscriber itself.
        //
        // 🔴 A CONFIGURATION CHANGE IS NOT THE USER LEAVING, and `Window.Stopped` cannot tell them apart.
        // MEASURED on an emulator: a font-scale change logged "the app left the foreground" every time,
        // so an app reconnecting its socket on a long absence would reconnect on every rotation instead.
        // `MobileWindowLifecycle` exists for exactly this question and already ships.
        // ⚠ A suppressed stop must leave the tracker's state alone, or the NEXT real departure is
        // suppressed too — pinned by a test, because a rotation on a device does not show it.
        _onStopped = (_, _) =>
        {
            var recreating = MobileWindowLifecycle.IsRecreating;
            Log($"lifecycle: the window stopped (recreating={recreating})");
            _foreground.Hidden(recreating);
        };
        _onResumed = (_, _) =>
        {
            Log("lifecycle: the window resumed");
            _foreground.Shown();
        };

        _window.Stopped += _onStopped;
        _window.Resumed += _onResumed;
        Log("lifecycle: watching the window's foreground transitions");
    }

    private void Log(string message) => AppCallback.Log(_log, () => $"[Shenora.Mobile] {message}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.CompareExchange(ref _live, null, this);
        _window.Stopped -= _onStopped;
        _window.Resumed -= _onResumed;
    }
}
