using Microsoft.Maui.Controls;
using Shenora.Modules.Platform;

namespace Shenora.Mobile;

/// <summary>
/// Feeds a MAUI <see cref="Window"/>'s foreground transitions into <see cref="AppLifecycle"/>.
/// <para>
/// Both shells, no <c>#if</c>: <c>Stopped</c> and <c>Resumed</c> are MAUI's own, mapping to
/// <c>onStop</c>/<c>onResume</c> on Android and <c>didEnterBackground</c>/<c>willEnterForeground</c> on
/// iOS.
/// </para>
/// </summary>
/// <remarks>
/// 🔴 <b><c>Stopped</c>/<c>Resumed</c>, NOT <c>Deactivated</c>/<c>Activated</c></b> — the same choice
/// the background-playback transfer documents, for the same reason: the latter pair also fires for a
/// dialog or the notification shade, so an app would report itself backgrounded while still on screen.
/// <para>
/// ⚠ <b>MAUI's <see cref="Window"/> is PROCESS-scoped and outlives the page.</b> A subscription left
/// attached is not merely a leak — the next page attaches a SECOND one, and every transition is then
/// reported twice. Hence <see cref="IDisposable"/>, and hence holding the handlers in fields: an
/// anonymous lambda cannot be unsubscribed.
/// </para>
/// </remarks>
public sealed class MobileAppLifecycle : IDisposable
{
    private readonly AppLifecycle _lifecycle;
    private readonly Window _window;
    private readonly EventHandler _onStopped;
    private readonly EventHandler _onResumed;
    private bool _disposed;

    /// <param name="window">The page's window. Its transitions are what get reported.</param>
    /// <param name="lifecycle">The reporter. Register it with <c>AddShenoraAppLifecycle</c>.</param>
    public MobileAppLifecycle(Window window, AppLifecycle lifecycle)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

        // ⚠ Deliberately NOT async lambdas. An `async` handler on an event is `async void`, so an escape
        // is an unhandled exception on the UI thread rather than a failed report — which on Android
        // crosses JNI and kills the process. Both bodies below are synchronous and cannot throw:
        // `Emit` is fire-and-forget and guards every subscriber itself.
        //
        // 🔴 A CONFIGURATION CHANGE IS NOT THE USER LEAVING, and `Window.Stopped` cannot tell them apart.
        // MEASURED on an emulator: a font-scale change logged "the app left the foreground" every time,
        // so an app reconnecting its socket on a long absence would reconnect on every rotation instead.
        // `MobileWindowLifecycle` exists for exactly this question and already ships.
        // ⚠ Skipping the STOP is what makes the pair consistent: the resume that follows then finds
        // nothing to measure and honestly reports null rather than a fabricated few milliseconds.
        _onStopped = (_, _) =>
        {
            if (MobileWindowLifecycle.IsRecreating) return;
            _lifecycle.ReportStopped();
        };
        _onResumed = (_, _) => _lifecycle.ReportResumed();

        _window.Stopped += _onStopped;
        _window.Resumed += _onResumed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Stopped -= _onStopped;
        _window.Resumed -= _onResumed;
    }
}
