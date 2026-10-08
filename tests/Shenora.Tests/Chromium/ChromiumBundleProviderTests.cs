using System.Reflection;
using Shenora.Chromium;
using Shenora.Chromium.Serving;
using Shenora.Core.WebView;
using Shenora.Tests.TestSupport;
using static Shenora.Tests.TestSupport.ChromiumServingCalls;

namespace Shenora.Tests.Chromium;

/// <summary>A Chromium bundle from a provider (an embedded bundle above all), as the WebView2 shell serves one.</summary>
public class ChromiumBundleProviderTests
{
    private static readonly ChromiumOrigins Origins = ChromiumOrigins.For("app.local", "http://localhost:5173/", isDevelopment: false);

    private static EmbeddedResourceProvider Embedded() => new(new EmbeddedResourceProviderOptions
    {
        Assembly = Assembly.GetExecutingAssembly(),
        ResourcePrefix = "Shenora.Tests.TestAssets.wwwroot",
    });

    [Fact]
    public async Task An_embedded_bundle_serves_its_files_and_marks_its_documents()
    {
        var serving = new ChromiumServing(null, Origins, new ChromiumInterceptor(), provider: Embedded());

        var page = await ServeAsync(serving, "https://app.local/");
        var script = await ServeAsync(serving, "https://app.local/assets/app-abc123.js");

        Assert.Equal(200, page.Status);
        Assert.Contains("shenora-test-index", page.Body);
        Assert.Contains(ChromiumTransport.HostGlobal, page.Body);
        Assert.Equal(200, script.Status);
        Assert.Contains("shenora-test-asset", script.Body);
        Assert.Equal("application/javascript", script.Type);   // WebViewContentTypes.FromPath's
        Assert.DoesNotContain(ChromiumTransport.HostGlobal, script.Body);
    }

    // The path is unescaped before the provider is asked, so a CJK name arrives as itself. (In memory: the repo keeps
    // its file names ASCII, which doctor enforces.)
    [Fact]
    public async Task A_percent_encoded_path_reaches_the_provider_unescaped()
    {
        var serving = new ChromiumServing(null, Origins, new ChromiumInterceptor(), provider: new FakeResourceProvider(("日本/字.js", "cjk")));
        Assert.Equal("cjk", (await ServeAsync(serving, "https://app.local/%E6%97%A5%E6%9C%AC/%E5%AD%97.js")).Body);
    }

    [Fact]
    public async Task A_miss_in_the_provider_falls_through_to_the_apps_pipeline()
    {
        var interceptor = new ChromiumInterceptor();
        interceptor.Use((request, next, ct) => request.Uri.AbsolutePath == "/api/hello"
            ? Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.Bytes("hello"u8.ToArray(), "text/plain"))
            : next(request, ct));
        var serving = new ChromiumServing(null, Origins, interceptor, provider: Embedded());

        Assert.Equal("hello", (await ServeAsync(serving, "https://app.local/api/hello")).Body);
        Assert.Equal(404, (await ServeAsync(serving, "https://app.local/api/nothing")).Status);
    }

    // A provider that throws is a fixed 404, never handed to the app's routes: that would turn a provider fault into a
    // request the app's own middleware answers (the WebView2 shell's rule).
    [Fact]
    public async Task A_provider_that_throws_is_a_fixed_404_and_a_log_line()
    {
        var log = new RecordingLogger();
        var routed = false;
        var interceptor = new ChromiumInterceptor();
        interceptor.Use((request, next, ct) => { routed = true; return next(request, ct); });
        var serving = new ChromiumServing(null, Origins, interceptor, log: log, provider: new ThrowingResourceProvider());

        var response = await ServeAsync(serving, "https://app.local/app.js");

        Assert.Equal(404, response.Status);
        Assert.Equal("Not Found", response.Body);
        Assert.DoesNotContain("secret", response.Body);
        Assert.False(routed);
        Assert.Contains(log.Lines, l => l.Contains("app.js") && l.Contains("failed"));
    }

    // The provider's own stream is handed on: an embedded file is already in memory, and copying it on CEF's IO thread
    // for every request (once to a buffer, again to an array) bought nothing.
    [Fact]
    public async Task A_providers_file_is_handed_on_without_a_copy()
    {
        var provider = new RecordingProvider();
        var serving = new ChromiumServing(null, Origins, new ChromiumInterceptor(), provider: provider);
        var request = new WebViewResourceRequest { Uri = new Uri("https://app.local/app.js"), Method = "GET", Headers = new Dictionary<string, string>() };

        var response = await serving.ServeAsync(ChromiumRoute.Bundle, request, CancellationToken.None);

        Assert.Same(provider.Last, response.Content);
        Assert.Equal("application/javascript", response.Headers["Content-Type"]);
    }

    private sealed class RecordingProvider : IWebViewResourceProvider
    {
        public Stream? Last { get; private set; }
        public Stream? GetResourceStream(string virtualPath) => Last = new MemoryStream("x"u8.ToArray());
        public bool Exists(string virtualPath) => true;
    }

    [Fact]
    public void The_provider_is_warmed_once_serving_is_built()
    {
        var provider = new FakeResourceProvider();
        _ = new ChromiumServing(null, Origins, new ChromiumInterceptor(), provider: provider);
        Assert.Equal(1, provider.Warmed);
    }

    [Fact]
    public void A_folder_and_a_provider_together_are_refused()
    {
        var options = new ChromiumHostOptions { ContentRoot = "wwwroot", ResourceProvider = new FakeResourceProvider() };
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Bundle test" });

        var shell = Assert.Throws<ArgumentException>(() => builder.UseChromium(options));
        var engine = Assert.Throws<ArgumentException>(() =>
            new ChromiumEngine(new ChromiumEngineOptions { ContentRoot = "wwwroot", ResourceProvider = new FakeResourceProvider() }));

        Assert.Contains(nameof(ChromiumHostOptions.ContentRoot), shell.Message);
        Assert.Contains(nameof(ChromiumHostOptions.ResourceProvider), shell.Message);
        Assert.Contains(nameof(ChromiumEngineOptions.ResourceProvider), engine.Message);
    }
}
