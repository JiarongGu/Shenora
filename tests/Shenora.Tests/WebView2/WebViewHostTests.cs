using Shenora.WebView2;
using Microsoft.Web.WebView2.Core;

namespace Shenora.Tests.WebView2;

/// <summary>
/// The pure parts of the host: the start-URL decision, header policies, and option defaults.
/// <c>InitializeAsync</c> spawns a real browser process and is proven by the sample-app e2e
/// (same precedent as environment creation).
/// </summary>
public class WebViewHostTests
{
    private static WebViewHostOptions Options(bool isDev = false, string? devUrl = null,
        string? productionUrl = null, string? virtualHost = null) => new()
    {
        Environment = new WebViewEnvironmentOptions
        {
            UserDataFolder = @"C:\MyApp\data\webview2",
            IsDevelopment = isDev,
        },
        DevUrl = devUrl,
        ProductionUrl = productionUrl,
        VirtualHost = virtualHost,
    };

    // ── The startup bundle sanity check (P5.5 H3) ─────────────────────────────────────────────────
    // A mistyped or stale ResourcePrefix matches nothing, so every request 404s and the app opens a
    // BLACK WINDOW with no error anywhere — and the prefix depends on MSBuild's manifest-name mangling,
    // so it is the last thing anyone suspects. ResolveStartUrl already throws actionably when the URL
    // configuration is missing; this closes the case where the URL is fine and the content is not.

    private sealed class StubProvider(bool hasIndex) : IWebViewResourceProvider
    {
        public Stream? GetResourceStream(string virtualPath) => null;

        public bool Exists(string virtualPath) =>
            hasIndex && virtualPath.Equals("index.html", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_bundle_start_document_with_no_index_throws_actionably()
    {
        var options = Options(productionUrl: null, virtualHost: "app.local");
        options = new WebViewHostOptions
        {
            Environment = options.Environment,
            VirtualHost = "app.local",
            ResourceProvider = new StubProvider(hasIndex: false),
        };
        var url = WebViewHost.ResolveStartUrl(options);

        var ex = Assert.Throws<InvalidOperationException>(() => WebViewHost.AssertBundleServable(url, options));
        Assert.Contains("index.html", ex.Message, StringComparison.Ordinal);
        Assert.Contains("blank", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_servable_bundle_passes()
    {
        var options = new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = @"C:\MyApp\data\webview2" },
            VirtualHost = "app.local",
            ResourceProvider = new StubProvider(hasIndex: true),
        };
        WebViewHost.AssertBundleServable(WebViewHost.ResolveStartUrl(options), options);
    }

    [Fact]
    public void A_dev_url_start_document_never_consults_the_provider()
    {
        // The reason this check does NOT live in the provider's constructor: a provider with nothing to
        // serve is legitimate when the page loads from a dev URL, which is the normal state of a fresh
        // clone whose bundle has not been built yet.
        var options = new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = @"C:\MyApp\data\webview2", IsDevelopment = true },
            DevUrl = "http://localhost:3517",
            VirtualHost = "app.local",
            ResourceProvider = new StubProvider(hasIndex: false),
        };
        WebViewHost.AssertBundleServable(WebViewHost.ResolveStartUrl(options), options);
    }

    [Fact]
    public void A_production_url_elsewhere_never_consults_the_provider()
    {
        // The provider may exist purely for subresources — that is the app's business.
        var options = new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = @"C:\MyApp\data\webview2" },
            ProductionUrl = "https://example.invalid/app",
            VirtualHost = "app.local",
            ResourceProvider = new StubProvider(hasIndex: false),
        };
        WebViewHost.AssertBundleServable(WebViewHost.ResolveStartUrl(options), options);
    }

    [Fact]
    public void Development_navigates_to_the_dev_url()
    {
        Assert.Equal("http://localhost:3517",
            WebViewHost.ResolveStartUrl(Options(isDev: true, devUrl: "http://localhost:3517")));
    }

    [Fact]
    public void Development_without_a_dev_url_throws_actionably()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WebViewHost.ResolveStartUrl(Options(isDev: true)));
        Assert.Contains("DevUrl", ex.Message);
    }

    [Fact]
    public void Production_prefers_the_explicit_url_over_the_virtual_host()
    {
        Assert.Equal("http://127.0.0.1:5211/",
            WebViewHost.ResolveStartUrl(Options(productionUrl: "http://127.0.0.1:5211/", virtualHost: "app.local")));
    }

    [Fact]
    public void Production_defaults_to_the_virtual_host_index()
    {
        Assert.Equal("https://app.local/index.html",
            WebViewHost.ResolveStartUrl(Options(virtualHost: "app.local")));
    }

    [Fact]
    public void Production_without_a_target_throws_actionably()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WebViewHost.ResolveStartUrl(Options()));
        Assert.Contains("ProductionUrl", ex.Message);
    }

    [Fact]
    public void Options_carry_the_family_defaults()
    {
        var options = Options();
        Assert.True(options.UseSharedEnvironment);
        Assert.Equal(TimeSpan.FromSeconds(25), options.InitTimeout);
        Assert.True(options.AllowExternalDrop);
        Assert.True(options.PreventDefaultFileDrop);
        Assert.True(options.BlockBrowserShortcutsInProduction);
        Assert.True(options.OpenExternalLinksInSystemBrowser);
        Assert.True(options.ReloadOnRenderProcessFailure);
        Assert.Equal([CoreWebView2PermissionKind.ClipboardRead], options.PermittedPermissions);
        Assert.Empty(options.DeferredSchemes);
        Assert.Empty(options.FolderMappings);
        Assert.Empty(options.InjectedGlobals);
    }

    [Fact]
    public void Deferred_scheme_default_cache_allows_cache_busting_callers()
    {
        var scheme = new WebViewDeferredScheme
        {
            Scheme = "app",
            Handler = _ => Task.FromResult((Array.Empty<byte>(), "text/plain")),
        };
        Assert.Equal("public, max-age=86400", scheme.CacheControl);
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("assets/app-abc123.js", "application/javascript")]
    [InlineData("assets/style.css", "text/css")]
    [InlineData("font.woff2", "font/woff2")]
    [InlineData("weird.xyz", "application/octet-stream")]
    public void Content_types_follow_the_family_map(string path, string expected)
    {
        Assert.Equal(expected, WebViewContentTypes.FromPath(path));
    }

    [Fact]
    public void Cache_policy_is_no_cache_html_immutable_assets()
    {
        // The source served EVERYTHING immutable incl. index.html — the stale-bundle trap.
        Assert.Equal("no-cache", WebViewContentTypes.CacheControlFromPath("index.html"));
        Assert.Equal("public, max-age=31536000, immutable",
            WebViewContentTypes.CacheControlFromPath("assets/app-abc123.js"));
    }
}
