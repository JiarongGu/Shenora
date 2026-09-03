using Microsoft.Maui.Handlers;

namespace Shenora.Mobile;

/// <summary>
/// Lets <see cref="MediaSurfaceView"/> show through a hole the page leaves.
/// <para>
/// 🔴 <b>This is the assumption the whole shell-draws-the-picture design rests on, so prove it FIRST and on
/// a device.</b> If the webview cannot be made see-through, the picture is never visible and no amount of
/// player work rescues it — and the failure is indistinguishable from a player that never started.
/// </para>
/// <para>
/// ⚠ <b>The PAGE's own background is a layer this cannot reach.</b> A transparent webview in front of an
/// opaque <c>body</c> is still opaque, so the page has to make the picture's region transparent itself.
/// That is also the safety catch: while the page paints a background, enabling this changes nothing
/// visible, so it can ship ahead of the page half.
/// </para>
/// </summary>
/// <remarks>
/// ⚠ <b>Internal on purpose.</b> <c>UseShenoraMediaSurface</c> is the only caller and the only public way
/// in — a second entry point could only be called without the handler that gives it a point, and the
/// symptom of getting that wrong (no picture) is the same as every other way this feature fails.
/// </remarks>
internal static class MobileWebViewTransparency
{
    /// <summary>
    /// Make every <c>HybridWebView</c> this app realizes see-through.
    /// <para>
    /// ⚠ Appended to the handler MAPPER rather than applied to one control, so it composes with the kit's
    /// other mappings and reaches a webview MAUI rebuilds after a configuration change.
    /// </para>
    /// </summary>
    public static void Enable()
    {
        HybridWebViewHandler.Mapper.AppendToMapping(nameof(MobileWebViewTransparency), (handler, _) =>
        {
            var view = handler.PlatformView;
            if (view is null) return;

#if ANDROID
            // The widget's own background. The page's `body` is the second layer and is the app's.
            view.SetBackgroundColor(global::Android.Graphics.Color.Transparent);
#elif IOS || MACCATALYST
            view.Opaque = false;
            view.BackgroundColor = UIKit.UIColor.Clear;
            // 🔴 THE THIRD LAYER, and the usual reason this "does not work" with the two above correct: the
            // scroll view carries its own background and paints over everything when it is left set.
            view.ScrollView.BackgroundColor = UIKit.UIColor.Clear;
#endif
        });
    }
}
