using Microsoft.Extensions.Logging;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Mobile;

/// <summary>Inputs for <see cref="MobileIpcBridge"/>.</summary>
public sealed class MobileIpcBridgeOptions
{
    /// <summary>The pipeline incoming requests are dispatched into.</summary>
    public required IMessageDispatcher Dispatcher { get; init; }

    /// <summary>
    /// When set, every event emitted on the bus is forwarded to the page as a batched notification.
    /// Buffering starts at bridge CONSTRUCTION, so events emitted while the page is still loading
    /// are not lost; delivery starts once the client reports ready.
    /// </summary>
    public IEventBus? EventBus { get; init; }

    /// <summary>Notification flush interval — the family's measured ~50 ms / 20 fps default.</summary>
    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Cap on buffered notifications; over the cap the OLDEST is dropped.</summary>
    public int MaxQueuedNotifications { get; init; } = 10_000;

    /// <summary>Per-channel delivery policy, applied at enqueue. Default: deliver everything.</summary>
    public Func<IpcNotification, bool>? NotificationFilter { get; init; }

    /// <summary>
    /// What to tell the client this shell is and can do, answered in the handshake so one page can ship to
    /// every shell. Declared by the APP — it depends on what this app composed, not only on the platform.
    /// Null says nothing, which the client reads as "assume nothing".
    /// </summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>Invoked on each ready handshake — the moment to clear per-page state.</summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>
    /// Release the webview's platform handler when this bridge is disposed. <b>On by default, and turning
    /// it off on Android costs you the app.</b>
    /// </summary>
    /// <remarks>
    /// 🔴 <b>MEASURED, because the failure is a process death nobody can attribute.</b> Android recreates
    /// the window for a font-scale or locale change, which disposes the OLD window's <c>MauiContext</c>
    /// scope — and MAUI's own <c>MauiHybridWebViewClient.ShouldInterceptRequest</c> then resolves a logger
    /// from that dead scope for a request the outgoing webview is still serving, throwing out of a
    /// JNI-invoked override with nothing managed above it. On an API 36 emulator one font-scale change
    /// killed the app in <b>8 of 10</b> trials; released, <b>0 of 10</b> over ten consecutive changes in
    /// one process, with the rebuilt page handshaking every time.
    /// <para>
    /// ⚠ <b>Stopping the webview is NOT the same remedy</b> — the same experiment with the platform view's
    /// <c>StopLoading()</c> was 10 of 10. The HANDLER is what holds the dead scope.
    /// </para>
    /// <para>
    /// ⚠ <b>A page that navigates away and back loses its document whatever this is set to</b>, so it costs a
    /// navigating page nothing. Measured on an API 36 emulator by swapping the window's page away and back: the
    /// view came back and answered with its document reloaded, with this on, with it off (MAUI's own
    /// <c>HandlerDisconnectPolicy.Automatic</c> then released the handler), and with MAUI's release off too.
    /// </para>
    /// <para>
    /// ⚠ iOS does not recreate a window for a configuration change, so this is a no-op there.
    /// </para>
    /// </remarks>
    public bool ReleaseHandlerOnDispose { get; init; } = true;

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }
}
