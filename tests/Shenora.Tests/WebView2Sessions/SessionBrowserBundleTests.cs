using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The seam that lets an off-screen session reach the app's OWN packaged bundle (E1, 2026-08-03).
/// Before it, a session browser got its own environment with none of the shell's serving set up, so a
/// packaged app pointing a <c>StreamingSession</c> at <c>https://app.local/…</c> rendered WebView2's
/// "can't reach this page" — and <c>SessionController</c> exposes no <c>CoreWebView2</c>, so it could
/// not be bolted on from outside either.
///
/// Two rules are worth a test and neither is reachable through a live browser: the both-or-neither
/// composition check, and the ORDER in which a blocking filter and the bundle are consulted.
/// </summary>
public class SessionBrowserBundleTests
{
    private sealed class StubProvider : IWebViewResourceProvider
    {
        public Stream? GetResourceStream(string virtualPath) => null;
        public bool Exists(string virtualPath) => false;
    }

    private static SessionBrowserOptions Options(string? virtualHost, IWebViewResourceProvider? provider) =>
        new()
        {
            ProfileDirectory = Path.Combine(Path.GetTempPath(), "shenora-tests", "session-bundle"),
            VirtualHost = virtualHost,
            ResourceProvider = provider,
        };

    // ── AssertBundleConfigured: both halves or neither ────────────────────────────────────────────

    [Fact]
    public void A_host_and_a_provider_together_are_a_valid_composition()
    {
        SessionBrowser.AssertBundleConfigured(Options("app.local", new StubProvider()));
    }

    [Fact]
    public void Configuring_no_bundle_at_all_is_a_valid_composition()
    {
        // The overwhelmingly common case: a session that renders third-party pages wants none of this.
        SessionBrowser.AssertBundleConfigured(Options(null, null));
    }

    [Fact]
    public void A_virtual_host_with_no_provider_is_refused_and_names_the_missing_half()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SessionBrowser.AssertBundleConfigured(Options("app.local", null)));

        // Failing at composition is the point (P5.5 H3): the alternative is a session that silently
        // serves nothing, whose symptom is indistinguishable from the bug this seam fixed.
        Assert.Contains(nameof(SessionBrowserOptions.ResourceProvider), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SessionBrowserOptions.VirtualHost), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_with_no_virtual_host_is_refused_and_names_the_missing_half()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SessionBrowser.AssertBundleConfigured(Options(null, new StubProvider())));

        Assert.Contains(nameof(SessionBrowserOptions.VirtualHost), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_EMPTY_virtual_host_counts_as_absent()
    {
        // "" is what an unset config value arrives as; it must not read as a configured host, which
        // would make a provider-less composition pass and a provider-ful one intercept `https:///*`.
        SessionBrowser.AssertBundleConfigured(Options("", null));
        Assert.Throws<InvalidOperationException>(
            () => SessionBrowser.AssertBundleConfigured(Options("", new StubProvider())));
    }

    // ── DecideRequest ─────────────────────────────────────────────────────────────────────────────

    private const string Prefix = "https://app.local/";
    private static bool BlockEverything(Uri request, Uri? page) => true;

    [Fact]
    public void A_request_to_the_app_s_own_origin_is_served_from_the_bundle()
    {
        Assert.Equal(SessionBrowser.SessionRequestAction.ServeBundle,
            SessionBrowser.DecideRequest("https://app.local/index.html", "https://app.local/", null, Prefix));
    }

    [Fact]
    public void A_request_to_anywhere_else_passes_through()
    {
        Assert.Equal(SessionBrowser.SessionRequestAction.Pass,
            SessionBrowser.DecideRequest("https://example.com/page", "https://example.com/", null, Prefix));
    }

    [Fact]
    public void With_no_bundle_configured_nothing_is_served()
    {
        Assert.Equal(SessionBrowser.SessionRequestAction.Pass,
            SessionBrowser.DecideRequest("https://app.local/index.html", "", null, null));
    }

    [Fact]
    public void The_host_match_is_case_insensitive()
    {
        // Hosts are case-insensitive in HTTP, and a page's own resolved subresource URLs are not
        // guaranteed to keep the casing the app configured.
        Assert.Equal(SessionBrowser.SessionRequestAction.ServeBundle,
            SessionBrowser.DecideRequest("https://APP.LOCAL/assets/x.js", "https://app.local/", null, Prefix));
    }

    [Fact]
    public void An_absent_request_uri_is_passed_rather_than_matched()
    {
        Assert.Equal(SessionBrowser.SessionRequestAction.Pass,
            SessionBrowser.DecideRequest(null, "https://app.local/", null, Prefix));
    }

    [Fact]
    public void THE_FILTER_IS_CONSULTED_BEFORE_THE_BUNDLE()
    {
        // The order invariant. An app that blocks a request has stated a policy; serving it from our
        // own provider anyway would override that policy through a path the app cannot see. This is
        // also why the two live as ONE decision instead of two WebResourceRequested subscriptions —
        // two handlers both assigning args.Response is last-writer-wins by subscription order.
        Assert.Equal(SessionBrowser.SessionRequestAction.Block,
            SessionBrowser.DecideRequest("https://app.local/index.html", "https://app.local/",
                BlockEverything, Prefix));
    }

    [Fact]
    public void A_filter_that_passes_a_bundle_request_still_gets_it_served()
    {
        var consulted = false;
        var action = SessionBrowser.DecideRequest("https://app.local/index.html", "https://app.local/",
            (_, _) => { consulted = true; return false; }, Prefix);

        Assert.Equal(SessionBrowser.SessionRequestAction.ServeBundle, action);
        Assert.True(consulted, "the app's filter must see the bundle request too, not be bypassed by it");
    }

    [Fact]
    public void A_filter_with_no_bundle_configured_still_blocks()
    {
        // The pre-E1 behaviour, unchanged: a session with only a request filter must keep filtering.
        Assert.Equal(SessionBrowser.SessionRequestAction.Block,
            SessionBrowser.DecideRequest("https://tracker.example.net/x.js", "https://example.com/",
                BlockEverything, null));
    }
}
