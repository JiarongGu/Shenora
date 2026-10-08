using Shenora.Windows;
using Shenora.Core.WebView;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.WebView2;

/// <summary>
/// The virtual-host serving path, which is now ONE implementation shared by <c>WebViewHost</c> (the app
/// shell) and <c>SessionBrowser</c> (an off-screen session rendering the app's own frontend). It lived
/// inline in a <c>WebResourceRequested</c> lambda over a live <c>CoreWebView2</c> until then, so none
/// of it was reachable from a test — and both halves below are the kind of thing that fails ONLY in a
/// packaged build, because dev serves the frontend from Vite over http and never comes through here.
/// </summary>
public class WebViewBundleServingTests
{
    private sealed class StubProvider : IWebViewResourceProvider
    {
        public Stream? GetResourceStream(string virtualPath) => null;
        public bool Exists(string virtualPath) => false;
    }

    // ── Prefix: both halves or nothing ────────────────────────────────────────────────────────────

    [Fact]
    public void A_host_and_a_provider_together_make_the_serving_prefix()
    {
        Assert.Equal("https://app.local/", WebViewBundleServing.Prefix("app.local", new StubProvider()));
    }

    [Fact]
    public void A_host_with_no_provider_serves_nothing()
    {
        // Nothing behind the address. Composition-checked loudly for a session
        // (SessionBrowser.AssertBundleConfigured); here it simply must not produce a prefix that would
        // intercept every request and 404 it.
        Assert.Null(WebViewBundleServing.Prefix("app.local", null));
    }

    [Fact]
    public void A_provider_with_no_host_serves_nothing()
    {
        Assert.Null(WebViewBundleServing.Prefix(null, new StubProvider()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_absent_host_serves_nothing(string? host)
    {
        Assert.Null(WebViewBundleServing.Prefix(host, new StubProvider()));
    }

    // ── ResolveBundlePath ─────────────────────────────────────────────────────────────────────────

    private const string Prefix = "https://app.local/";

    [Fact]
    public void The_bare_origin_resolves_to_the_start_document()
    {
        Assert.Equal("index.html", WebViewBundleServing.ResolveBundlePath("https://app.local/", Prefix));
    }

    [Theory]
    [InlineData("https://app.local/index.html", "index.html")]
    [InlineData("https://app.local/assets/index-abc123.js", "assets/index-abc123.js")]
    [InlineData("https://app.local/nested/deep/style.css", "nested/deep/style.css")]
    public void A_path_under_the_host_is_the_bundle_path(string uri, string expected)
    {
        Assert.Equal(expected, WebViewBundleServing.ResolveBundlePath(uri, Prefix));
    }

    [Theory]
    [InlineData("https://app.local/assets/x.js?v=7", "assets/x.js")]
    [InlineData("https://app.local/?t=1", "index.html")]
    public void A_cache_busting_query_is_not_part_of_the_path(string uri, string expected)
    {
        Assert.Equal(expected, WebViewBundleServing.ResolveBundlePath(uri, Prefix));
    }

    [Theory]
    // Spaces and CJK asset names are normal in this family and arrive percent-encoded; without the
    // unescape they miss the manifest and 404 in production only.
    [InlineData("https://app.local/assets/my%20file.png", "assets/my file.png")]
    [InlineData("https://app.local/assets/%E7%A5%9E%E9%98%99.png", "assets/神阙.png")]
    public void A_percent_encoded_path_is_decoded(string uri, string expected)
    {
        Assert.Equal(expected, WebViewBundleServing.ResolveBundlePath(uri, Prefix));
    }

    [Fact]
    public void The_query_is_stripped_BEFORE_the_path_is_unescaped()
    {
        // The asymmetric case that pins the ORDER. A filename containing a question mark arrives as
        // %3F; unescaping first would turn it into a '?' and the query strip would then truncate the
        // name to "a" — a 404 on a file that exists. Every symmetric test above passes either way,
        // which is exactly why this one is here.
        Assert.Equal("a?b.txt", WebViewBundleServing.ResolveBundlePath("https://app.local/a%3Fb.txt", Prefix));
        // …and a real query still goes, even when the path also carries an encoded one.
        Assert.Equal("a?b.txt", WebViewBundleServing.ResolveBundlePath("https://app.local/a%3Fb.txt?v=2", Prefix));
    }

    // ── IsPageLoad: a document GET, as on the Chromium shell ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Document, "GET", true)]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Document, "get", true)]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Document, "POST", false)]   // a form post
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Script, "GET", false)]
    [InlineData(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.Fetch, "GET", false)]
    public void A_page_load_is_a_document_GET(Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext context, string method, bool pageLoad) =>
        Assert.Equal(pageLoad, WebViewBundleServing.IsPageLoad(context, method));

    // ── Miss: what a request the bundle and the app's routes left unanswered gets ─────────────────────────────────

    private static string Body(WebViewResourceResponse r) => new StreamReader(r.Content).ReadToEnd();
    private static readonly string Kit = Body(WebViewResourceResponse.NotFoundDocument());

    [Fact]
    public void A_page_load_gets_the_bundles_404_with_status_404()
    {
        var warned = new List<string>();
        var r = WebViewBundleServing.Miss(true, new FakeResourceProvider(("404.html", "<p>own</p>")), "404.html", "settings", m => warned.Add(m()));

        Assert.Equal((404, "<p>own</p>"), (r.StatusCode, Body(r)));
        Assert.StartsWith("text/html", r.Headers["Content-Type"]);
        Assert.Contains(warned, w => w.Contains("settings") && w.Contains("404.html"));
    }

    // The provider holds only an "escaped" file, which no case may reach: the option off, a page the bundle lacks, and a
    // path out of the bundle all show the kit's page.
    [Theory]
    [InlineData(null)]
    [InlineData("missing.html")]
    [InlineData("../404.html")]
    public void Otherwise_a_page_load_gets_the_kits_page(string? page) =>
        Assert.Equal(Kit, Body(WebViewBundleServing.Miss(true, new FakeResourceProvider(("../404.html", "escaped")), page, "x", _ => { })));

    [Fact]
    public void A_404_html_that_throws_gets_the_kits_page_and_a_warning()
    {
        var warned = new List<string>();
        var r = WebViewBundleServing.Miss(true, new ThrowingResourceProvider(), "404.html", "x", m => warned.Add(m()));

        Assert.Equal(Kit, Body(r));
        Assert.Contains(warned, w => w.Contains("failed"));
    }

    [Fact]
    public void A_provider_that_serves_nothing_says_so_in_the_warning()
    {
        var warned = new List<string>();
        var provider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
        {
            Assembly = typeof(WebViewBundleServingTests).Assembly,
            ResourcePrefix = "No.Such.Prefix",
        });
        WebViewBundleServing.Miss(true, provider, "404.html", "x", m => warned.Add(m()));
        Assert.Contains(warned, w => w.Contains("serves nothing") && w.Contains("ResourcePrefix"));
    }

    [Fact]
    public void Anything_but_a_page_load_keeps_the_plain_404() =>
        Assert.Equal("Not Found", Body(WebViewBundleServing.Miss(false, new FakeResourceProvider(("404.html", "<p>own</p>")), "404.html", "x.js", _ => { })));
}
