using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Tests.Core;

/// <summary>
/// The middleware composition every shell's interceptor runs on (D45). All of it is provable with no
/// webview — which is the reason it lives in <c>Shenora</c> instead of being hand-rolled three times:
/// before this, the only way to find out whether a route ran in the right order was to launch a device.
/// </summary>
public class WebViewResourcePipelineTests
{
    private static WebViewResourceRequest Request(string uri = "https://app.local/media?x") =>
        new() { Uri = new Uri(uri), Method = "GET", Headers = new Dictionary<string, string>() };

    private static WebViewResourceResponse Answer(string body) =>
        WebViewResourceResponse.Bytes(System.Text.Encoding.UTF8.GetBytes(body), "text/plain");

    private static string BodyText(WebViewResourceResponse response)
    {
        using var reader = new StreamReader(response.Content);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Build_returns_null_when_empty()
    {
        var pipeline = new WebViewResourcePipeline();

        Assert.True(pipeline.IsEmpty);
        // Null, not an always-declining handler: on desktop the difference is a thread-pool hop and a
        // deferral on every request the app's own page makes.
        Assert.Null(pipeline.Build());
    }

    [Fact]
    public async Task Middleware_run_in_registration_order()
    {
        var pipeline = new WebViewResourcePipeline();
        var order = new List<string>();

        pipeline.Use((request, next, token) => { order.Add("first"); return next(request, token); });
        pipeline.Use((request, next, token) => { order.Add("second"); return next(request, token); });

        await pipeline.Build()!(Request(), CancellationToken.None);

        // The chain is composed back-to-front, so this is exactly the assertion that catches composing it
        // the other way — which reads identically and inverts every app's routing.
        Assert.Equal(["first", "second"], order);
    }

    [Fact]
    public async Task A_declining_middleware_falls_through_to_the_next()
    {
        var pipeline = new WebViewResourcePipeline();
        pipeline.Use((request, next, token) => next(request, token));
        pipeline.Use((_, _, _) => Task.FromResult<WebViewResourceResponse?>(Answer("second answered")));

        var response = await pipeline.Build()!(Request(), CancellationToken.None);

        Assert.Equal("second answered", BodyText(response!));
    }

    [Fact]
    public async Task An_answering_middleware_stops_the_chain()
    {
        var pipeline = new WebViewResourcePipeline();
        var reachedSecond = false;
        pipeline.Use((_, _, _) => Task.FromResult<WebViewResourceResponse?>(Answer("first")));
        pipeline.Use((request, next, token) => { reachedSecond = true; return next(request, token); });

        var response = await pipeline.Build()!(Request(), CancellationToken.None);

        Assert.Equal("first", BodyText(response!));
        Assert.False(reachedSecond);
    }

    [Fact]
    public async Task Nothing_claiming_the_request_yields_null_so_the_platform_handles_it()
    {
        var pipeline = new WebViewResourcePipeline();
        pipeline.Use((request, next, token) => next(request, token));

        // Null is the whole "not ours" contract — it is what leaves the app's bundle to the platform.
        Assert.Null(await pipeline.Build()!(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task Middleware_can_post_process_what_the_next_one_returned()
    {
        var pipeline = new WebViewResourcePipeline();
        // The cross-cutting case the whole middleware shape exists for: a layer that WRAPS rather than
        // terminates. If this did not work, a cache/metric/log layer would have to be built into every route.
        pipeline.Use(async (request, next, token) =>
        {
            var inner = await next(request, token);
            if (inner is null) return null;
            var wrapped = new Dictionary<string, string>(inner.Headers, StringComparer.OrdinalIgnoreCase)
            {
                ["X-Wrapped"] = "yes",
            };
            return new WebViewResourceResponse
            {
                StatusCode = inner.StatusCode,
                ReasonPhrase = inner.ReasonPhrase,
                Headers = wrapped,
                Content = inner.Content,
            };
        });
        pipeline.Use((_, _, _) => Task.FromResult<WebViewResourceResponse?>(Answer("inner")));

        var response = await pipeline.Build()!(Request(), CancellationToken.None);

        Assert.Equal("yes", response!.Headers["X-Wrapped"]);
        Assert.Equal("inner", BodyText(response));
    }

    [Fact]
    public async Task Disposing_a_registration_removes_only_that_route()
    {
        var pipeline = new WebViewResourcePipeline();
        var ran = new List<string>();
        var first = pipeline.Use((request, next, token) => { ran.Add("first"); return next(request, token); });
        pipeline.Use((request, next, token) => { ran.Add("second"); return next(request, token); });

        first.Dispose();
        await pipeline.Build()!(Request(), CancellationToken.None);

        Assert.Equal(["second"], ran);
        Assert.False(pipeline.IsEmpty);
    }

    [Fact]
    public async Task Two_registrations_of_the_SAME_delegate_are_removed_independently()
    {
        var pipeline = new WebViewResourcePipeline();
        var hits = 0;
        Task<WebViewResourceResponse?> Count(WebViewResourceRequest request, WebViewResourceHandler next,
                                             CancellationToken token)
        {
            hits++;
            return next(request, token);
        }

        var a = pipeline.Use(Count);
        pipeline.Use(Count);

        a.Dispose();
        await pipeline.Build()!(Request(), CancellationToken.None);

        // ONE, not zero. Removal is by reference identity: two registrations of the same method group are
        // EQUAL delegates, so an Equals-based removal would silently drop both — and an app registering the
        // same helper for two roots would lose the one it kept.
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task One_delegate_OBJECT_registered_twice_loses_one_slot_per_dispose()
    {
        var pipeline = new WebViewResourcePipeline();
        var hits = 0;
        WebViewResourceMiddleware count = (request, next, token) => { hits++; return next(request, token); };

        var a = pipeline.Use(count);
        var b = pipeline.Use(count);

        a.Dispose();
        await pipeline.Build()!(Request(), CancellationToken.None);
        // ONE slot left. Both slots hold the same reference here, so a filter on reference equality
        // stripped BOTH on the first dispose — making the second handle's Dispose a silent no-op and
        // `Use`'s "remove just this one" untrue.
        Assert.Equal(1, hits);

        b.Dispose();
        Assert.True(pipeline.IsEmpty);
    }

    [Fact]
    public void Disposing_a_registration_twice_removes_one_route()
    {
        var pipeline = new WebViewResourcePipeline();
        var registration = pipeline.Use((request, next, token) => next(request, token));
        pipeline.Use((request, next, token) => next(request, token));

        registration.Dispose();
        registration.Dispose();

        Assert.False(pipeline.IsEmpty);
        Assert.NotNull(pipeline.Build());
    }

    [Fact]
    public void Clear_drops_every_route()
    {
        var pipeline = new WebViewResourcePipeline();
        pipeline.Use((request, next, token) => next(request, token));

        pipeline.Clear();

        Assert.True(pipeline.IsEmpty);
        Assert.Null(pipeline.Build());
    }

    [Fact]
    public async Task A_built_handler_keeps_running_the_snapshot_it_was_built_from()
    {
        var pipeline = new WebViewResourcePipeline();
        pipeline.Use((_, _, _) => Task.FromResult<WebViewResourceResponse?>(Answer("original")));
        var handler = pipeline.Build()!;

        // Registering after the build must not reach into the composed chain. The shells build once per
        // request precisely so a route added mid-request cannot half-apply.
        pipeline.Use((_, _, _) => Task.FromResult<WebViewResourceResponse?>(Answer("added later")));

        Assert.Equal("original", BodyText((await handler(Request(), CancellationToken.None))!));
    }

    [Fact]
    public void Use_rejects_null()
        => Assert.Throws<ArgumentNullException>(() => new WebViewResourcePipeline().Use(null!));
}
