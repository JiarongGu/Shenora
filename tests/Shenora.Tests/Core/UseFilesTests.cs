using System.Text;
using Shenora.Core.WebView;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Core;

/// <summary>
/// 🔴 <b>The <c>UseFiles</c> MIDDLEWARE — 0 of 34 lines covered until 2026-08-14.</b>
///
/// <para>
/// The pieces underneath it were well tested (<c>WebViewFilesTests</c> pins <c>ResolveContained</c>,
/// <c>ServeRangeTests</c> pins the range arithmetic) but the WIRING an adopter actually calls was not:
/// resolve → contain → 404 → serve, plus the fall-through that separates a middleware from a handler.
/// It is covered end-to-end by the desktop sample's <c>InterceptorProbe</c> only, which does not run in
/// the suite — so a change here fails on a device rather than at a keystroke. That mattered more than
/// usual this week, because <c>ResolveContained</c>'s comparison was changed to be platform-correct and
/// nothing in the unit suite exercised the path that calls it.
/// </para>
/// </summary>
public class UseFilesTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    private WebViewFileOptions Options(Func<Uri, string?>? resolve = null, string[]? roots = null)
    {
        // The app's own route shape: anything under /files/ maps into the temp root.
        Func<Uri, string?> byRoute = uri => uri.AbsolutePath.StartsWith("/files/", StringComparison.Ordinal)
            ? Path.Combine(_dir.Root, uri.AbsolutePath["/files/".Length..])
            : null;

        return new WebViewFileOptions
        {
            Resolve = resolve ?? byRoute,
            AllowedRoots = roots ?? [_dir.Root],
        };
    }

    /// <summary>
    /// "Not mine" must fall through the REST of the pipeline, not terminate it — the whole difference
    /// between a middleware and a handler. A route registered AFTER this one must still get its turn.
    /// </summary>
    [Fact]
    public async Task A_uri_the_resolver_declines_falls_THROUGH_to_the_next_middleware()
    {
        var interceptor = new FakeInterceptor();
        interceptor.UseFiles(Options());

        var reached = false;
        interceptor.Use((request, next, ct) =>
        {
            reached = true;
            return Task.FromResult<WebViewResourceResponse?>(WebViewResourceResponse.NotFound());
        });

        var response = await interceptor.AskAsync("https://app.local/something-else");

        Assert.True(reached, "UseFiles terminated the pipeline for a URI its resolver declined");
        Assert.NotNull(response);
    }

    /// <summary>A resolved, contained, existing file is served with its bytes and a derived content type.</summary>
    [Fact]
    public async Task A_resolved_file_is_served()
    {
        _dir.WriteFile("clip.txt", "hello");
        var interceptor = new FakeInterceptor();
        interceptor.UseFiles(Options());

        var response = await interceptor.AskAsync("https://app.local/files/clip.txt");

        Assert.NotNull(response);
        Assert.Equal(200, response!.StatusCode);
        Assert.Equal("hello", ReadBody(response));
    }

    /// <summary>
    /// 🔴 A path OUTSIDE the allowed roots is refused with the same fixed 404 a missing file gets, so a
    /// page cannot tell "forbidden" from "absent" and probe for existence by comparing responses.
    /// </summary>
    [Fact]
    public async Task A_path_outside_the_allowed_roots_is_indistinguishable_from_a_missing_file()
    {
        var outside = Path.Combine(Path.GetTempPath(), "shenora-outside-secret.txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var interceptor = new FakeInterceptor();
            // A deliberately generous resolver — containment is not the resolver's job to remember.
            interceptor.UseFiles(Options(resolve: _ => outside));

            var refused = await interceptor.AskAsync("https://app.local/files/anything");

            var interceptor2 = new FakeInterceptor();
            interceptor2.UseFiles(Options(resolve: _ => Path.Combine(_dir.Root, "does-not-exist.txt")));
            var missing = await interceptor2.AskAsync("https://app.local/files/does-not-exist.txt");

            Assert.NotNull(refused);
            Assert.NotNull(missing);
            Assert.Equal(404, refused!.StatusCode);
            Assert.Equal(missing!.StatusCode, refused.StatusCode);
            // The refusal must not carry the path, or the 404 leaks what the containment check hid.
            Assert.DoesNotContain("secret", ReadBody(refused), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    /// <summary>
    /// A traversal segment in the RESOLVED PATH is refused. Note where it has to come from, which this
    /// test had to be corrected to reflect: <see cref="Uri"/> collapses <c>..</c> out of
    /// <see cref="Uri.AbsolutePath"/> before a resolver ever sees it, so traversal typed into the URL PATH
    /// never reaches here at all (it simply stops matching the route and falls through). The live vector is
    /// the one <see cref="WebViewFileOptions.AllowedRoots"/> documents — a URL carrying a path the PAGE
    /// supplied, in a query parameter or a payload — which the app's resolver hands over verbatim.
    /// </summary>
    [Fact]
    public async Task A_traversal_segment_in_the_RESOLVED_path_is_refused()
    {
        // ⚠ The escaping target must EXIST, or this passes for the wrong reason: a missing file is a 404
        // whether or not containment ran, so the test would stay green with the check removed. (Caught by
        // sabotage 2026-08-14 — the same trap a `NUL` test fell into earlier the same day.)
        var escaped = Path.GetFullPath(Path.Combine(_dir.Root, "..", "shenora-escaped.txt"));
        File.WriteAllText(escaped, "escaped");
        try
        {
            var interceptor = new FakeInterceptor();
            // The shape a page-supplied `?path=` produces: relative to the root, and escaping it.
            interceptor.UseFiles(Options(resolve: _ => Path.Combine(_dir.Root, "..", "shenora-escaped.txt")));

            var response = await interceptor.AskAsync("https://app.local/files/anything");

            Assert.NotNull(response);
            Assert.Equal(404, response!.StatusCode);
            Assert.DoesNotContain("escaped", ReadBody(response), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(escaped);
        }
    }

    /// <summary>
    /// The companion fact, pinned so the test above cannot be misread: traversal in the URL path is
    /// normalized by <see cref="Uri"/> itself, so the request stops matching the app's route and falls
    /// THROUGH rather than being refused. Different mechanism, same safety — worth stating because a
    /// reader who assumed a 404 here would draw the wrong conclusion about where containment applies.
    /// </summary>
    [Fact]
    public async Task Traversal_in_the_URL_path_is_normalized_away_before_the_resolver_sees_it()
    {
        var interceptor = new FakeInterceptor();
        string? seen = null;
        interceptor.UseFiles(Options(resolve: uri => { seen = uri.AbsolutePath; return null; }));

        var response = await interceptor.AskAsync("https://app.local/files/../../etc/passwd");

        Assert.Equal("/etc/passwd", seen);   // the `/files/` prefix is gone — Uri resolved it away
        Assert.Null(response);               // so it is not ours, and the pipeline declines
    }

    /// <summary>
    /// The <c>Range</c> header reaches the file server through this wiring — a route that dropped it
    /// would serve whole files to every seek and nothing would fail loudly.
    /// </summary>
    [Fact]
    public async Task A_range_request_is_answered_206_through_the_middleware()
    {
        _dir.WriteFile("clip.bin", "ABCDEFGHIJ");
        var interceptor = new FakeInterceptor();
        interceptor.UseFiles(Options());

        var response = await interceptor.AskAsync("https://app.local/files/clip.bin", range: "bytes=2-5");

        Assert.NotNull(response);
        Assert.Equal(206, response!.StatusCode);
        Assert.Equal("CDEF", ReadBody(response));
    }

    /// <summary>
    /// 🔴 <c>RangeDelivery</c> is read FROM THE INTERCEPTOR, never passed in — D44 is a measured platform
    /// fact whose failure mode is silent, so the wiring must not let a caller supply it wrong. Under
    /// Android's <c>Unsliced</c> rule the body is the whole resource from the range start and the
    /// <c>Content-Range</c> says so.
    /// </summary>
    [Fact]
    public async Task The_platform_delivery_rule_comes_from_the_interceptor()
    {
        _dir.WriteFile("clip.bin", "ABCDEFGHIJ");
        var interceptor = new FakeInterceptor(WebViewRangeDelivery.Unsliced);
        interceptor.UseFiles(Options());

        var response = await interceptor.AskAsync("https://app.local/files/clip.bin", range: "bytes=2-5");

        Assert.NotNull(response);
        Assert.Equal(206, response!.StatusCode);
        // Unsliced: {from}→EOF, because the platform truncates the front itself and streams the rest.
        Assert.Equal("bytes 2-9/10", response.Headers["Content-Range"]);
    }

    /// <summary>The app's own content type wins over the extension-derived one when it supplies one.</summary>
    [Fact]
    public async Task An_app_supplied_content_type_wins()
    {
        _dir.WriteFile("clip.bin", "x");
        var interceptor = new FakeInterceptor();
        interceptor.UseFiles(Options() with { ContentType = _ => "application/x-shenora-test" });

        var response = await interceptor.AskAsync("https://app.local/files/clip.bin");

        Assert.NotNull(response);
        Assert.Equal("application/x-shenora-test", response!.Headers["Content-Type"]);
    }

    /// <summary>
    /// Disposing the registration removes the route — the same contract every other <c>Use</c> has, and
    /// what keeps a route from outliving the feature it served.
    /// </summary>
    [Fact]
    public async Task Disposing_the_registration_removes_the_route()
    {
        _dir.WriteFile("clip.txt", "hello");
        var interceptor = new FakeInterceptor();
        var registration = interceptor.UseFiles(Options());

        Assert.NotNull(await interceptor.AskAsync("https://app.local/files/clip.txt"));

        registration.Dispose();

        // Nothing left to serve it: the pipeline declines, which is "the platform would have handled it".
        Assert.Null(await interceptor.AskAsync("https://app.local/files/clip.txt"));
    }

    private static string ReadBody(WebViewResourceResponse response)
    {
        if (response.Content is null) return string.Empty;
        using var reader = new StreamReader(response.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
