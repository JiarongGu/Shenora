using Shenora.Windows;

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
}
