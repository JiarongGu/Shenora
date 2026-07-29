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
