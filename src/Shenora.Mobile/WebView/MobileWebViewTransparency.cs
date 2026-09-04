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
/// ✅ <b>DEMONSTRATED ON ANDROID</b> (2026-09-04, API 36 / WebView 133.0.6943.137): with this mapping
/// applied and the page in a stage of its own, the shell's clip is composited under the document and
/// photographed there. <c>samples/Shenora.Sample.Maui</c> walks it a layer at a time; the numbers are in
/// <c>docs/design/media.md</c>.
/// </para>
/// <para>
/// 🔴 <b>THIS AND THE DOCUMENT ARE THE WHOLE CHAIN — the page's and the activity's backgrounds are NOT in
/// it.</b> Measured in the same run: the picture is visible with the MAUI page's background left fully
/// OPAQUE, because a <c>SurfaceView</c> punches its hole through everything the window drew before it.
/// An earlier remark here counted four layers and had a sample clearing all of them; two of those changes
/// did nothing and one of them cannot be made at all (a transparent <c>ColorDrawable</c> does not repaint
/// an already-drawn window).
/// </para>
/// <para>
/// ⚠ <b>The PAGE's own background is the layer this cannot reach.</b> A transparent webview in front of an
/// opaque <c>body</c> is still opaque, so the page has to make the picture's region transparent itself —
/// and its CONTENT too, which is the half that gets forgotten. That is also the safety catch: while the
/// page paints a background, enabling this changes nothing visible, so it can ship ahead of the page half.
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
    /// <param name="log">
    /// Diagnostics, and this one earns its place. ⚠ <b>A mapper that never runs and one that runs
    /// perfectly produce the same screen</b> — an opaque webview — so without a line here the first
    /// question a missing picture raises is unanswerable. Measured: three device screenshots were spent
    /// before a control proved the occluder was above the picture rather than the picture missing.
    /// </param>
    public static void Enable(Action<string>? log = null)
    {
        HybridWebViewHandler.Mapper.AppendToMapping(nameof(MobileWebViewTransparency), (handler, _) =>
        {
            var view = handler.PlatformView;
            if (view is null)
            {
                log?.Invoke("webview transparency: the handler has no platform view — NOT applied");
                return;
            }

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
            log?.Invoke("webview transparency: applied — a native layer can now show through");
        });
        log?.Invoke("webview transparency: mapping registered (it runs when a webview is realized)");
    }
}
