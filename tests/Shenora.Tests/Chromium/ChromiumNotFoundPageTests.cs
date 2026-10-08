using Microsoft.Extensions.Logging;
using Shenora.Chromium;
using Shenora.Chromium.Serving;
using Shenora.Core.WebView;
using Shenora.Tests.TestSupport;
using static Shenora.Tests.TestSupport.ChromiumServingCalls;

namespace Shenora.Tests.Chromium;

/// <summary>What a page load that finds nothing shows: the bundle's own page, else the kit's; a fetch keeps the plain 404.</summary>
public class ChromiumNotFoundPageTests
{
    private static readonly ChromiumOrigins Origins = ChromiumOrigins.For("app.local", null, isDevelopment: false);

    private static ChromiumServing With(IWebViewResourceProvider provider, string? page = "404.html", ILogger? log = null) =>
        new(null, Origins, new ChromiumInterceptor(), log: log, provider: provider, notFoundPage: page);

    [Fact]
    public async Task A_page_load_that_finds_nothing_shows_the_bundles_404_marked()
    {
        var serving = With(new FakeResourceProvider(("404.html", "<html><head></head><body>app's own</body></html>")));
        var page = await ServeAsync(serving, "https://app.local/missing?x=1", ChromiumRoute.BundlePage);

        Assert.Equal(404, page.Status);
        Assert.Contains("app's own", page.Body);
        Assert.Contains(ChromiumTransport.HostGlobal, page.Body);
    }

    [Theory]
    [InlineData("/404.html")]
    [InlineData(@"\404.html")]
    public async Task The_page_is_found_however_its_path_is_written(string page)
    {
        var serving = With(new FakeResourceProvider(("404.html", "<p>own</p>")), page);
        Assert.Contains("own", (await ServeAsync(serving, "https://app.local/x", ChromiumRoute.BundlePage)).Body);
    }

    [Theory]
    [InlineData(null)]                 // the option off
    [InlineData("404.html")]           // the bundle has none
    [InlineData("../404.html")]        // outside the bundle
    public async Task Otherwise_the_kits_page_shows(string? page)
    {
        var serving = With(new FakeResourceProvider(("../404.html", "<p>escaped</p>")), page);
        var response = await ServeAsync(serving, "https://app.local/x", ChromiumRoute.BundlePage);
        var kit = new StreamReader(WebViewResourceResponse.NotFoundDocument().Content).ReadToEnd();

        Assert.Equal(404, response.Status);
        Assert.Equal(kit, response.Body);
    }

    [Fact]
    public async Task A_404_html_that_fails_to_read_shows_the_kits_page()
    {
        var log = new RecordingLogger();
        var response = await ServeAsync(With(new ThrowingResourceProvider(), log: log),
            "https://app.local/x", ChromiumRoute.BundlePage);

        Assert.Contains("Not Found</p>", response.Body);
        Assert.DoesNotContain("secret", response.Body);
        Assert.Contains(log.Lines, l => l.Contains("404.html"));
    }

    [Fact]
    public async Task Anything_but_a_page_load_keeps_the_plain_404()
    {
        var serving = With(new FakeResourceProvider(("404.html", "<p>own</p>")));
        var fetched = await ServeAsync(serving, "https://app.local/data.json", ChromiumRoute.Bundle);
        Assert.Equal((404, "Not Found"), (fetched.Status, fetched.Body));
    }

    [Fact]
    public async Task Each_miss_is_a_warning_naming_the_path_the_source_and_the_page_shown()
    {
        var log = new RecordingLogger();
        await ServeAsync(With(new FakeResourceProvider(), log: log), "https://app.local/settings", ChromiumRoute.BundlePage);

        var line = Assert.Single(log.Lines, l => l.StartsWith("Warning"));
        Assert.Contains("/settings", line);
        Assert.Contains(nameof(FakeResourceProvider), line);
        Assert.Contains("the kit's not-found page", line);
    }

    [Fact]
    public async Task A_folder_bundle_has_the_same_page()
    {
        using var root = new TempBundle(("index.html", "<p>app</p>"), ("404.html", "<p>folder's own</p>"));
        var serving = new ChromiumServing(root.Path, Origins, new ChromiumInterceptor(), notFoundPage: "404.html");
        var page = await ServeAsync(serving, "https://app.local/missing", ChromiumRoute.BundlePage);
        Assert.Equal(404, page.Status);
        Assert.Contains("folder's own", page.Body);
    }

    [Fact]
    public void The_kits_page_is_fixed_light_or_dark_and_draggable()
    {
        var response = WebViewResourceResponse.NotFoundDocument();
        var body = new StreamReader(response.Content).ReadToEnd();

        Assert.Equal(404, response.StatusCode);
        Assert.StartsWith("text/html", response.Headers["Content-Type"]);
        Assert.Contains("content=\"light dark\"", body);
        Assert.Contains("-webkit-app-region:drag", body);
        Assert.Contains(">Not Found<", body);
    }
}
