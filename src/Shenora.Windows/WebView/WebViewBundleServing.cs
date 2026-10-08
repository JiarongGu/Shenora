using System.Text;
using Microsoft.Web.WebView2.Core;
using Shenora.Core.WebView;
using Shenora.Core.Shell;
using Shenora;

namespace Shenora.Windows;

/// <summary>
/// Serving an <see cref="IWebViewResourceProvider"/> over a virtual host — the ONE implementation,
/// shared by <see cref="WebViewHost"/> (the app shell's own frontend) and <see cref="SessionBrowser"/>
/// (an off-screen session rendering that same frontend). Keep it that way: the details below are where
/// this path gets subtly wrong.
/// <para>
/// 🔴 Serving is SYNCHRONOUS and inline on the UI thread, unlike a deferred scheme: the bundle is in
/// memory and includes the MAIN DOCUMENT, and deferring the main document stalls the initial
/// navigation (see <see cref="WebViewHost"/>).
/// </para>
/// </summary>
internal static class WebViewBundleServing
{
    /// <summary>
    /// The URI prefix a configured bundle is served under (<c>https://{host}/</c>), or null when this
    /// composition serves no bundle. Both halves are required — a virtual host with no provider has
    /// nothing behind it, and a provider with no host has no address.
    /// </summary>
    internal static string? Prefix(string? virtualHost, IWebViewResourceProvider? provider) =>
        virtualHost is { Length: > 0 } host && provider is not null ? $"https://{host}/" : null;

    /// <summary>
    /// The bundle path a request URI asks for. The caller must already have matched
    /// <paramref name="prefix"/>.
    /// <para>
    /// ⚠ ORDER: strip the query FIRST, then unescape. The other way round turns a <c>%3F</c> inside a
    /// filename into a <c>?</c> and truncates the name there. Unescaping is needed at all because
    /// bundle filenames carry spaces and CJK characters, which otherwise miss the manifest and 404 in
    /// production only (dev serves from Vite, never through here).
    /// </para>
    /// </summary>
    internal static string ResolveBundlePath(string uri, string prefix)
    {
        var path = uri[prefix.Length..];
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0) path = path[..queryIndex];
        path = Uri.UnescapeDataString(path);
        return path.Length == 0 ? "index.html" : path;
    }

    /// <summary>
    /// Answer one bundle request on <paramref name="args"/>, or 404. Runs on the UI thread.
    /// </summary>
    /// <param name="args">The intercepted request.</param>
    /// <param name="environment">The environment that mints the response (UI-thread affine).</param>
    /// <param name="provider">The bundle behind the virtual host.</param>
    /// <param name="uri">The raw request URI (already matched against <paramref name="prefix"/>).</param>
    /// <param name="prefix">The virtual-host prefix from <see cref="Prefix"/>.</param>
    /// <param name="log">The caller's GUARDED lazy log sink — a throwing app sink must not escape into
    /// a WebView2 event handler, and building a message may itself touch a torn-down COM object.</param>
    /// <param name="pageLoad">The request is a document load: a miss shows the not-found page (<see cref="Miss"/>).</param>
    /// <param name="notFoundPage">The bundle's not-found page, or null for the kit's.</param>
    /// <param name="warn">Where a page load's miss is reported; <paramref name="log"/> when null.</param>
    internal static void Serve(CoreWebView2WebResourceRequestedEventArgs args, CoreWebView2Environment environment,
                               IWebViewResourceProvider provider, string uri, string prefix,
                               Action<Func<string>> log, bool pageLoad = false, string? notFoundPage = null,
                               Action<Func<string>>? warn = null)
    {
        if (TryServe(args, environment, provider, uri, prefix, log)) return;

        var path = ResolveBundlePath(uri, prefix);
        log(() => $"[Shenora.Windows] 404 for bundle resource '{path}'");
        try { args.Response = ToWebView2(environment, Miss(pageLoad, provider, notFoundPage, path, warn ?? log)); }
        catch { /* the webview may be tearing down */ }
    }

    /// <summary>
    /// Serve one bundle request and report whether it was ANSWERED — false meaning the bundle does not
    /// contain that path, with nothing set on <paramref name="args"/>, so the caller can look elsewhere
    /// (since D45 the interceptor middleware shares this origin, and
    /// <c>https://app.local/media?…</c> arrives here first).
    /// <para>
    /// ⚠ A provider that THROWS is answered 404 (true), not declined: quietly handing a failing bundle
    /// path to an app's file middleware would turn a provider fault into a disk read for it.
    /// </para>
    /// </summary>
    internal static bool TryServe(CoreWebView2WebResourceRequestedEventArgs args, CoreWebView2Environment environment,
                                  IWebViewResourceProvider provider, string uri, string prefix,
                                  Action<Func<string>> log)
    {
        try
        {
            var path = ResolveBundlePath(uri, prefix);

            var stream = provider.GetResourceStream(path);
            if (stream is null) return false;

            var headers = $"Content-Type: {WebViewContentTypes.FromPath(path)}\n" +
                          $"Cache-Control: {WebViewContentTypes.CacheControlFromPath(path)}\n" +
                          "Access-Control-Allow-Origin: *";
            args.Response = environment.CreateWebResourceResponse(stream, 200, "OK", headers);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                // 🔴 The BODY says nothing about the exception. These responses carry
                // `Access-Control-Allow-Origin: *`, so page script can fetch and read the text, and
                // `ex.Message` routinely means a full local filesystem path. The diagnosis goes to the
                // host log.
                log(() => $"[Shenora.Windows] Serving '{uri}' failed: {ex}");
                args.Response = NotFound(environment);
            }
            catch
            {
                // the webview may be tearing down
            }
            return true;
        }
    }

    /// <summary>
    /// What a request on the bundle's host gets when neither the bundle nor the app's routes answered: a page load, the
    /// bundle's <paramref name="notFoundPage"/> with status 404, else the kit's page; anything else, the fixed plain 404.
    /// </summary>
    internal static WebViewResourceResponse Miss(bool pageLoad, IWebViewResourceProvider provider, string? notFoundPage,
                                                 string path, Action<Func<string>> warn)
    {
        if (!pageLoad) return WebViewResourceResponse.NotFound();
        WebViewResourceResponse? own = null;
        var page = notFoundPage?.Replace('\\', '/').TrimStart('/');
        if (page is { Length: > 0 } && !page.Split('/').Contains(".."))
        {
            try
            {
                if (provider.GetResourceStream(page) is { } stream)
                    own = new WebViewResourceResponse
                    {
                        Content = stream,
                        StatusCode = 404,
                        ReasonPhrase = "Not Found",
                        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Content-Type"] = WebViewContentTypes.FromPath(page),
                            ["Cache-Control"] = "no-store",
                        },
                    };
            }
            catch (Exception ex)
            {
                warn(() => $"[Shenora.Windows] Reading the bundle's '{page}' failed: {ex}");
            }
        }
        var source = provider is EmbeddedResourceProvider { CanServe: false }
            ? "the resource provider EmbeddedResourceProvider, which serves nothing: no embedded resource matches its "
              + "ResourcePrefix (the project's RootNamespace and the folder, such as MyApp.wwwroot)"
            : $"the resource provider {provider.GetType().Name}";
        warn(() => $"[Shenora.Windows] No page at '{path}' in {source}: showing "
                   + (own is null ? "the kit's not-found page" : $"the bundle's '{page}'"));
        return own ?? WebViewResourceResponse.NotFoundDocument();
    }

    /// <summary>A kit response as WebView2's, minted by <paramref name="environment"/> (UI-thread affine).</summary>
    internal static CoreWebView2WebResourceResponse ToWebView2(CoreWebView2Environment environment, WebViewResourceResponse response) =>
        environment.CreateWebResourceResponse(response.Content, response.StatusCode, response.ReasonPhrase,
            string.Join("\n", response.Headers.Select(h => $"{h.Key}: {h.Value}")));

    /// <summary>
    /// 🔴 The one 404 body served to the page, and it is CONSTANT. Every response here carries
    /// <c>Access-Control-Allow-Origin: *</c>, so page script can read whatever is in it; the reason a
    /// request failed belongs in the host log.
    /// </summary>
    private static readonly byte[] NotFoundBody = Encoding.UTF8.GetBytes("Not Found");

    internal static CoreWebView2WebResourceResponse NotFound(CoreWebView2Environment environment) =>
        environment.CreateWebResourceResponse(
            new MemoryStream(NotFoundBody, writable: false),
            404, "Not Found", "Content-Type: text/plain");
}
