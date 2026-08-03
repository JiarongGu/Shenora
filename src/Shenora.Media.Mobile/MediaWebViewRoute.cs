using Microsoft.Maui.Controls;
using Shenora.Core;

namespace Shenora.Media;

/// <summary>
/// Serves media to a MAUI <c>HybridWebView</c> — the ~20 lines every mobile app would otherwise write, and
/// <b>the one place that knows which <see cref="MediaBodyMode"/> this platform needs</b>.
/// <para>
/// That last point is the whole reason these platform packages exist. Android's webview applies the
/// <c>Range</c> start to whatever body it is handed; iOS's passes the body through verbatim. So the SAME
/// portable request needs an unsliced body on one and a sliced body on the other (D44, measured on both).
/// An app should never have to know that, and after this it does not: it supplies a resolver and this
/// supplies the platform's rule.
/// </para>
/// <para>
/// Shared source, compiled into <c>Shenora.Media.Android</c> and <c>Shenora.Media.iOS</c>. Every line here
/// is identical on both faces; the only difference is the constant each package injects via
/// <see cref="PlatformBodyMode"/>, which is exactly the shape `Shenora.Mobile` already proved.
/// </para>
/// </summary>
public static class MediaWebViewRoute
{
    /// <summary>
    /// The body rule this platform needs, chosen at COMPILE time by whichever package this source was
    /// built into.
    /// <para>
    /// A compile-time constant rather than a runtime check on purpose: it cannot be got wrong by
    /// configuration, and it cannot drift between the two packages, because each one's csproj defines
    /// exactly one of the two symbols and the shared source cannot compile without one.
    /// </para>
    /// </summary>
    public static MediaBodyMode PlatformBodyMode =>
#if SHENORA_MEDIA_UNSLICED
        MediaBodyMode.Unsliced;   // Android: the platform skips to the range start itself
#elif SHENORA_MEDIA_SLICED
        MediaBodyMode.Sliced;     // iOS: the platform passes the body through verbatim
#else
        // A COMPILE error, not a runtime one, and that choice is the point: a third platform package must
        // DECIDE which rule its webview follows — measured on a device, the way these two were — and it
        // must not be able to ship without deciding. Silently inheriting either default produces a handler
        // that works on every faststart file and fails on every other one, which is the failure mode this
        // whole enum exists to prevent. Same fail-closed reasoning as the `partial` method that made a
        // fourth shell unable to compile until it defined what saving meant (CS8795).
#error Shenora.Media.Mobile requires SHENORA_MEDIA_SLICED or SHENORA_MEDIA_UNSLICED. Each platform package must declare which body rule its webview follows — verify it on a device, do not guess.
#endif

    /// <summary>
    /// Answer a <c>WebResourceRequested</c> event for media, or leave it entirely alone.
    /// <para>
    /// Wire it once: <c>webView.WebResourceRequested += (s, e) =&gt;
    /// MediaWebViewRoute.TryServe(e, Resolve, "video/mp4", options);</c>
    /// </para>
    /// </summary>
    /// <param name="e">The MAUI event args.</param>
    /// <param name="resolve">
    /// The app's map from a request URI to a source path — how it reads its own URL shape and picks the
    /// file. Return null and the request is refused with the kit's fixed 404. <b>Whatever this returns is
    /// still authorised against <paramref name="options"/></b>, so a resolver that trusts the page cannot
    /// widen what is reachable.
    /// </param>
    /// <param name="contentType">The MIME type to report — the app's call, since it knows its catalogue.</param>
    /// <param name="options">
    /// Where media may be served from. <see cref="MediaServingOptions.BodyMode"/> is IGNORED and replaced
    /// with <see cref="PlatformBodyMode"/>: the platform's rule is not a preference an app should override,
    /// and letting it be passed in is how a copy-pasted desktop configuration would break one shell.
    /// </param>
    /// <returns>True when this request was answered, false when it was not ours.</returns>
    public static bool TryServe(WebViewWebResourceRequestedEventArgs e, Func<Uri, string?> resolve,
                               string contentType, MediaServingOptions options)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(options);

        var requested = resolve(e.Uri);
        if (requested is null) return false;   // not a media route — leave `Handled` untouched

        // Authorised HERE, never in the app's resolver: a security check the caller can forget is a
        // security check that gets forgotten. The resolver says WHICH file; this says whether it may.
        var path = MediaAccess.ResolveLocal(requested, options);

        var response = path is null
            // A refusal is indistinguishable from a missing file, deliberately — a distinct "forbidden"
            // reply tells a page whether a path exists, which is the existence leak the desktop's own
            // static serving had to be fixed for.
            ? WebViewResourceResponse.NotFound()
            : MediaRangeServer.Serve(
                new WebViewResourceRequest { Uri = e.Uri, Method = e.Method, Headers = e.Headers },
                path, contentType, options with { BodyMode = PlatformBodyMode });

        // ⚠ The PORTABLE overload, with a header dictionary. `e.PlatformArgs` is NOT needed on either
        // platform — every header survives to the native response, which was verified on devices after
        // this repo had spent a session believing the opposite (D44).
        e.SetResponse(response.StatusCode, response.ReasonPhrase, response.Headers, response.Content);
        e.Handled = true;
        return true;
    }
}
