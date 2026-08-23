namespace Shenora.Mobile;

/// <summary>
/// What a mobile window's RECREATION requires, and MAUI does not answer: whether a destruction is one,
/// and how to let go of the window that is going away.
/// <para>
/// 🔴 Load-bearing for the <c>Window.Destroying → ShenoraApplication.Stop()</c> wiring: on Android a
/// configuration change the manifest does not declare (font scale, locale — MAUI's template declares
/// orientation and theme, not these) destroys and recreates the window mid-session, and treating that as
/// shutdown cancels every in-flight request. Measured on a device: a save whose picker was open completed
/// as <c>OPERATION_CANCELLED</c> with the user's chosen file created and left empty. Skip the stop when
/// <see cref="IsRecreating"/> answers true; <c>Start</c> is idempotent.
/// </para>
/// </summary>
public static class MobileWindowLifecycle
{
    /// <summary>True while the current activity is being destroyed only to be recreated.</summary>
    public static bool IsRecreating =>
#if ANDROID
        Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.IsChangingConfigurations == true;
#else
        false;
#endif

    /// <summary>
    /// Let go of a view's platform handler as its page unloads. 🔴 <b>Call this for the webview from the
    /// page's <c>Unloaded</c>, or a configuration change takes the whole app down.</b>
    /// </summary>
    /// <param name="view">The view going away — the page's webview. Null and no-handler are no-ops.</param>
    /// <remarks>
    /// 🔴 <b>MEASURED, because the failure is a process death an adopter cannot attribute.</b> Android
    /// recreates the window for a font-scale or locale change, which disposes the OLD window's
    /// <c>MauiContext</c> scope — and MAUI's own <c>MauiHybridWebViewClient.ShouldInterceptRequest</c>
    /// then resolves a logger from that scope for a request the outgoing webview is still serving,
    /// throwing <c>ObjectDisposedException</c> out of a JNI-invoked override with nothing managed above
    /// it. On an API 36 emulator, one font-scale change killed the app in <b>8 of 10</b> trials; with this
    /// call it was <b>0 of 10</b>, over ten consecutive changes in one process, and the rebuilt page
    /// handshook every time.
    /// <para>
    /// ⚠ <b>Stopping the webview is NOT the fix</b> — the same experiment with the platform view's
    /// <c>StopLoading()</c> instead was 10 of 10. The handler is what holds the dead scope.
    /// </para>
    /// <para>
    /// ⚠ <b>Android only; a no-op elsewhere</b>, deliberately. iOS does not recreate a window for a
    /// configuration change, so there is nothing to release there and disconnecting a live handler would
    /// be a change with no benefit. Same shape as <see cref="IsRecreating"/>.
    /// </para>
    /// <para>
    /// ⚠ <b>Why the kit does not do this for you from <c>MobileIpcBridge.Dispose</c></b>, which would be
    /// the mechanism rather than a step to remember: a page that unloads and RELOADS the same view
    /// instance — an ordinary navigation — would have its handler disconnected under it, and that case is
    /// unmeasured. The page knows which teardown it is in; the bridge does not.
    /// </para>
    /// </remarks>
    public static void ReleaseHandler(Microsoft.Maui.IView? view)
    {
#if ANDROID
        view?.Handler?.DisconnectHandler();
#endif
    }
}
