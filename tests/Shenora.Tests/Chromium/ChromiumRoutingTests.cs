using System.Net;
using System.Text;
using Shenora.Chromium;
using Shenora.Chromium.Serving;
using Shenora.Core.WebView;

namespace Shenora.Tests.Chromium;

/// <summary>
/// Where a Chromium window's requests go, and what its own origins answer. The IPC rules are the part a
/// mistake turns into a hole, so every way a request can fail to be the app's own is a case.
/// </summary>
public class ChromiumRoutingTests
{
    private static readonly ChromiumOrigins Origins = ChromiumOrigins.For("app.local", "http://localhost:5173/", isDevelopment: true);

    private static ChromiumRoute Classify(string url, string method = "GET", bool own = true, bool main = true,
        bool navigation = false, string? initiator = "https://app.local") =>
        ChromiumRouting.Classify(new Uri(url), method, own, main, navigation, initiator, Origins);

    [Theory]
    [InlineData(ChromiumRouting.ClipboardPermission, "https://app.local", true)]
    [InlineData(ChromiumRouting.ClipboardPermission, "http://localhost:5173", true)]     // the dev server, in development
    [InlineData(ChromiumRouting.ClipboardPermission, "https://example.com", false)]      // a page the app navigated to
    [InlineData(ChromiumRouting.ClipboardPermission, null, false)]
    [InlineData(ChromiumRouting.ClipboardPermission, "not an origin", false)]
    [InlineData(1u << 8, "https://app.local", false)]                                    // any other permission
    [InlineData(ChromiumRouting.ClipboardPermission | 1u << 8, "https://app.local", false)] // clipboard bundled with another
    public void A_permission_prompt_allows_only_the_apps_own_clipboard_read(uint requested, string? origin, bool allowed) =>
        Assert.Equal(allowed, ChromiumRouting.AllowsPermission(requested, origin, Origins));

    [Fact]
    public void The_clipboard_flag_is_CEFs() =>
        Assert.Equal((uint)Shenora.Chromium.Interop.cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD, ChromiumRouting.ClipboardPermission);

    [Fact]
    public void The_apps_own_post_to_the_ipc_route_is_ipc()
    {
        Assert.Equal(ChromiumRoute.Ipc, Classify("https://app.local/__shenora/ipc", "POST"));
    }

    [Theory]
    [InlineData("GET", true, true, "https://app.local")]           // not a post
    [InlineData("POST", false, true, "https://app.local")]         // another browser sharing the handler
    [InlineData("POST", true, false, "https://app.local")]         // an iframe
    [InlineData("POST", true, true, "https://evil.example")]       // initiated elsewhere: a no-cors POST
    [InlineData("POST", true, true, null)]                         // no initiator is not a trusted one
    public void Anything_else_aimed_at_the_ipc_route_is_refused(string method, bool own, bool main, string? initiator)
    {
        Assert.Equal(ChromiumRoute.Refused, Classify("https://app.local/__shenora/ipc", method, own, main, initiator: initiator));
    }

    [Fact]
    public void The_apps_origin_is_the_bundle_and_everything_else_is_the_network()
    {
        Assert.Equal(ChromiumRoute.Bundle, Classify("https://app.local/assets/app.js"));
        Assert.Equal(ChromiumRoute.Network, Classify("https://example.com/"));
        // Same host, other scheme or port: a different origin.
        Assert.Equal(ChromiumRoute.Network, Classify("http://app.local/"));
        Assert.Equal(ChromiumRoute.Network, Classify("https://app.local:8443/"));
    }

    [Fact]
    public void In_development_the_dev_servers_document_is_fetched_to_be_marked_and_the_rest_left_to_it()
    {
        Assert.Equal(ChromiumRoute.DevDocument, Classify("http://localhost:5173/", navigation: true));
        Assert.Equal(ChromiumRoute.Network, Classify("http://localhost:5173/src/main.tsx"));
        Assert.Equal(ChromiumRoute.Ipc, Classify("http://localhost:5173/__shenora/ipc", "POST", initiator: "http://localhost:5173"));
        Assert.Equal(ChromiumRoute.Refused, Classify("http://localhost:5173/__shenora/ipc", "POST", initiator: "https://app.local"));
    }

    [Fact]
    public void Outside_development_the_dev_server_is_just_another_origin()
    {
        var production = ChromiumOrigins.For("app.local", "http://localhost:5173/", isDevelopment: false);

        Assert.Equal(ChromiumRoute.Network,
            ChromiumRouting.Classify(new Uri("http://localhost:5173/__shenora/ipc"), "POST", true, true, false, "http://localhost:5173", production));
    }

    [Fact]
    public async Task The_bundle_serves_its_files_and_marks_its_documents()
    {
        using var root = new TempBundle(("index.html", "<html><head><script src=app.js></script></head></html>"), ("app.js", "console.log(1)"));
        var serving = new ChromiumServing(root.Path, Origins, new ChromiumInterceptor());

        var page = await ServeAsync(serving, "https://app.local/");
        var script = await ServeAsync(serving, "https://app.local/app.js");

        Assert.Equal(200, page.Status);
        Assert.Contains(ChromiumTransport.HostGlobal, page.Body);
        Assert.True(page.Body.IndexOf(ChromiumTransport.HostGlobal, StringComparison.Ordinal) < page.Body.IndexOf("app.js", StringComparison.Ordinal));
        Assert.Equal("console.log(1)", script.Body);
        Assert.DoesNotContain(ChromiumTransport.HostGlobal, script.Body);
    }

    [Theory]
    [InlineData("https://app.local/../secret.txt")]
    [InlineData("https://app.local/%2e%2e%2fsecret.txt")]
    [InlineData("https://app.local/..%5Csecret.txt")]
    public async Task Nothing_outside_the_bundle_is_reachable(string url)
    {
        using var root = new TempBundle(("index.html", "<p>app</p>"));
        File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root.Path)!, "secret.txt"), "secret");
        var serving = new ChromiumServing(root.Path, Origins, new ChromiumInterceptor());

        var response = await ServeAsync(serving, url);

        Assert.Equal(404, response.Status);
        Assert.DoesNotContain("secret", response.Body);
    }

    [Fact]
    public async Task A_miss_falls_through_to_the_apps_pipeline_then_to_a_404()
    {
        using var root = new TempBundle(("index.html", "<p>app</p>"));
        var interceptor = new ChromiumInterceptor();
        interceptor.Use((request, next, ct) => request.Uri.AbsolutePath == "/api/hello"
            ? Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.Bytes("hello"u8.ToArray(), "text/plain"))
            : next(request, ct));
        var serving = new ChromiumServing(root.Path, Origins, interceptor);

        Assert.Equal("hello", (await ServeAsync(serving, "https://app.local/api/hello")).Body);
        Assert.Equal(404, (await ServeAsync(serving, "https://app.local/api/nothing")).Status);
    }

    [Fact]
    public async Task A_range_is_answered_with_exactly_its_slice()
    {
        using var root = new TempBundle(("clip.bin", "0123456789"));
        var serving = new ChromiumServing(root.Path, Origins, new ChromiumInterceptor());

        var response = await ServeAsync(serving, "https://app.local/clip.bin", ("Range", "bytes=2-4"));

        Assert.Equal(206, response.Status);
        Assert.Equal("234", response.Body);   // CEF sends what it is given: Sliced delivery
    }

    [Fact]
    public async Task A_refusal_is_a_fixed_403()
    {
        var response = await ServeAsync(new ChromiumServing(null, Origins, new ChromiumInterceptor()), "https://app.local/__shenora/ipc",
            route: ChromiumRoute.Refused);

        Assert.Equal(403, response.Status);
        Assert.Equal("forbidden", response.Body);
    }

    [Fact]
    public async Task The_dev_document_is_marked_and_a_dead_dev_server_is_a_fixed_502()
    {
        var dev = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><head></head><body>vite</body></html>", Encoding.UTF8, "text/html"),
        }));
        var marked = await ServeAsync(new ChromiumServing(null, Origins, new ChromiumInterceptor(), dev), "http://localhost:5173/",
            route: ChromiumRoute.DevDocument);

        var dead = new HttpClient(new StubHandler(_ => throw new HttpRequestException(@"connection refused to C:\secret")));
        var failed = await ServeAsync(new ChromiumServing(null, Origins, new ChromiumInterceptor(), dead), "http://localhost:5173/",
            route: ChromiumRoute.DevDocument);

        Assert.Contains(ChromiumTransport.HostGlobal, marked.Body);
        Assert.Contains("vite", marked.Body);
        Assert.Equal(502, failed.Status);
        Assert.DoesNotContain("secret", failed.Body);
    }

    private static async Task<(int Status, string Body)> ServeAsync(ChromiumServing serving, string url, params (string, string)[] headers) =>
        await ServeAsync(serving, url, ChromiumRoute.Bundle, headers);

    private static async Task<(int Status, string Body)> ServeAsync(ChromiumServing serving, string url, ChromiumRoute route, params (string, string)[] headers)
    {
        var request = new WebViewResourceRequest
        {
            Uri = new Uri(url),
            Method = "GET",
            Headers = headers.ToDictionary(h => h.Item1, h => h.Item2, StringComparer.OrdinalIgnoreCase),
        };
        var response = await serving.ServeAsync(route, request, CancellationToken.None);
        using var reader = new StreamReader(response.Content);
        return (response.StatusCode, await reader.ReadToEndAsync());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }

    /// <summary>A bundle folder INSIDE its own temp folder, so a sibling file can prove containment.</summary>
    private sealed class TempBundle : IDisposable
    {
        private readonly string _outer = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shenora-chromium-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }

        public TempBundle(params (string Name, string Text)[] files)
        {
            Path = System.IO.Path.Combine(_outer, "bundle");
            Directory.CreateDirectory(Path);
            foreach (var (name, text) in files) File.WriteAllText(System.IO.Path.Combine(Path, name), text);
        }

        public void Dispose()
        {
            try { Directory.Delete(_outer, recursive: true); } catch (IOException) { }
        }
    }
}
