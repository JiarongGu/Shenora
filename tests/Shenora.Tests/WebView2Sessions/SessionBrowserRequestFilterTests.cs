using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// <see cref="SessionBrowserOptions.RequestFilter"/> is the app's request-layer blocking boundary —
/// the SSRF-shaped predicate the design points at for redirect and subresource policy, since an async
/// navigation guard cannot be enforced in <c>NavigationStarting</c> (no deferral). It had NO tests
/// before P5.5 H7: the rule lived inside a <c>WebResourceRequested</c> lambda over a live
/// <c>CoreWebView2</c>, so nothing could reach it — the same shape as the pool's reset probe, which is
/// exactly how that bug survived five phase reviews.
///
/// The `pageUri` normalization is the subtle half and the reason a test matters: a same-host filter
/// that receives a non-web page source would treat the page's OWN next document as third-party and
/// 403 it, which presents as a blank session with no diagnosis.
/// </summary>
public class SessionBrowserRequestFilterTests
{
    /// <summary>A same-origin policy — the shape an adopting app actually writes.</summary>
    private static bool BlockCrossHost(Uri request, Uri? pageUri) =>
        pageUri is not null && !string.Equals(request.Host, pageUri.Host, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_filter_sees_the_request_and_the_page_host()
    {
        (Uri Request, Uri? Page)? seen = null;
        var blocked = SessionBrowser.ShouldBlockRequest(
            "https://cdn.example.net/a.js", "https://app.example.com/page",
            (request, page) => { seen = (request, page); return false; });

        Assert.False(blocked);
        Assert.Equal("https://cdn.example.net/a.js", seen!.Value.Request.ToString());
        Assert.Equal("app.example.com", seen.Value.Page!.Host);
    }

    [Fact]
    public void A_cross_host_subresource_is_blocked_by_a_same_host_policy()
    {
        Assert.True(SessionBrowser.ShouldBlockRequest(
            "https://evil.example.net/steal.js", "https://app.example.com/page", BlockCrossHost));
    }

    [Fact]
    public void A_same_host_subresource_is_allowed()
    {
        Assert.False(SessionBrowser.ShouldBlockRequest(
            "https://app.example.com/main.js", "https://app.example.com/page", BlockCrossHost));
    }

    // ── The pageUri normalization (the documented trap) ───────────────────────────────────────────
    // A pool instance sits on about:blank between leases, and a fresh control's Source is empty. If
    // either reached a same-host filter as a real page host, the filter would block the page's own
    // FIRST document — every lease serving a blank page.
    [Theory]
    [InlineData("")]                       // before the first navigation commits
    [InlineData("about:blank")]            // a returned/reset pool instance
    [InlineData(null)]                     // no source at all
    [InlineData("data:text/html,<b>x")]    // non-web scheme
    [InlineData("file:///C:/tmp/x.html")]  // non-web scheme
    public void A_non_web_page_source_reaches_the_filter_as_null(string? pageSource)
    {
        Uri? seenPage = new Uri("https://sentinel.invalid"); // must be overwritten with null
        var blocked = SessionBrowser.ShouldBlockRequest(
            "https://app.example.com/first-document", pageSource,
            (_, page) => { seenPage = page; return false; });

        Assert.Null(seenPage);
        Assert.False(blocked);
    }

    [Fact]
    public void The_first_document_of_a_reset_pool_instance_is_not_blocked_as_cross_host()
    {
        // The regression this normalization exists for, stated as a behaviour: same policy, same
        // request, only the page source differs — and a blank/about:blank source must not turn the
        // page's own document into a third-party request.
        Assert.False(SessionBrowser.ShouldBlockRequest(
            "https://app.example.com/page", "about:blank", BlockCrossHost));
        Assert.False(SessionBrowser.ShouldBlockRequest(
            "https://app.example.com/page", "", BlockCrossHost));
    }

    // ── Robustness ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a uri")]
    [InlineData("/relative/path")]
    public void An_unparseable_request_uri_is_passed_without_consulting_the_filter(string? requestUri)
    {
        var consulted = false;
        var blocked = SessionBrowser.ShouldBlockRequest(
            requestUri, "https://app.example.com/page", (_, _) => { consulted = true; return true; });

        // Nothing to describe to a policy, so the policy is not asked — and it is NOT blocked, which
        // matches the fail-open stance below.
        Assert.False(blocked);
        Assert.False(consulted);
    }

    [Fact]
    public void A_throwing_filter_FAILS_OPEN()
    {
        // Deliberate, and the opposite of the navigation guard's fail-closed stance: this predicate
        // runs on every subresource of every page, so failing closed on a buggy app predicate would
        // present as a blank page with nothing logged. The guard is the boundary that must hold.
        Assert.False(SessionBrowser.ShouldBlockRequest(
            "https://evil.example.net/x.js", "https://app.example.com/page",
            (_, _) => throw new InvalidOperationException("app policy blew up")));
    }

    [Fact]
    public void Non_http_request_schemes_still_reach_the_filter()
    {
        // The request side is NOT scheme-filtered — only the page source is. An app that wants to
        // block ws://, blob: or a custom scheme must be able to see it.
        var seen = new List<string>();
        foreach (var uri in new[] { "ws://app.example.com/socket", "blob:https://app.example.com/abc", "app://local/x" })
        {
            SessionBrowser.ShouldBlockRequest(uri, "https://app.example.com/page",
                (request, _) => { seen.Add(request.Scheme); return false; });
        }

        Assert.Equal(["ws", "blob", "app"], seen);
    }
}
