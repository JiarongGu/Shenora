using Microsoft.Extensions.DependencyInjection;
using Shenora.Core.WebView;

namespace Shenora.Tests.WebView2;

/// <summary>
/// The app-level resource pipeline (D64): declared once on the built app, applied to every webview it
/// hosts. These pin the two properties the move exists for — reach, and the refusal to reach PARTLY.
/// </summary>
public class WebViewPipelineTests
{
    /// <summary>
    /// 🔴 The reason the pipeline moved off a single interceptor: a second window gets the same routes,
    /// with the app declaring them ONCE. Secondary windows and session browsers used to get nothing
    /// unless wired again by hand, and a window serving no routes looks exactly like one whose routes
    /// were never needed.
    /// </summary>
    [Fact]
    public void Every_interceptor_receives_every_step_in_order()
    {
        var pipeline = new WebViewPipeline();
        var first = new RecordingInterceptor();
        var second = new RecordingInterceptor();

        pipeline.Use(i => ((RecordingInterceptor)i).Applied.Add("files"));
        pipeline.Use(i => ((RecordingInterceptor)i).Applied.Add("player"));

        pipeline.ApplyTo(first);
        pipeline.ApplyTo(second);

        Assert.Equal(["files", "player"], first.Applied);
        Assert.Equal(["files", "player"], second.Applied);
    }

    /// <summary>
    /// 🔴 THE ANTI-SILENCE GUARANTEE. A step added after a webview already exists could not reach it, so
    /// it would serve some windows and not others — with nothing to see. Freezing turns that into a loud
    /// composition error, and the message has to name the fix, because the error is a mistake about WHEN
    /// rather than about what.
    /// </summary>
    [Fact]
    public void Declaring_a_step_after_a_webview_exists_FAILS_rather_than_serving_some_windows()
    {
        var pipeline = new WebViewPipeline();
        pipeline.Use(_ => { });
        pipeline.ApplyTo(new RecordingInterceptor());

        var error = Assert.Throws<InvalidOperationException>(() => pipeline.Use(_ => { }));

        Assert.Contains("already serving", error.Message);
        Assert.Contains("before the first window", error.Message);
    }

    /// <summary>
    /// The freeze must not cost the case it exists to protect: a window opened LATER still gets every
    /// step, because the list is frozen rather than emptied.
    /// </summary>
    [Fact]
    public void A_window_opened_later_still_gets_the_whole_pipeline()
    {
        var pipeline = new WebViewPipeline();
        pipeline.Use(i => ((RecordingInterceptor)i).Applied.Add("files"));
        pipeline.ApplyTo(new RecordingInterceptor());

        var late = new RecordingInterceptor();
        pipeline.ApplyTo(late);

        Assert.Equal(["files"], late.Applied);
    }

    /// <summary>
    /// A throwing step fails the webview LOUDLY. Everywhere else the kit guards app callbacks, because
    /// they run on a UI-thread event path with no caller left to catch; this one runs during
    /// CONSTRUCTION, where swallowing would produce a window that silently serves nothing — the exact
    /// failure the type exists to remove.
    /// </summary>
    [Fact]
    public void A_throwing_step_fails_construction_instead_of_serving_nothing()
    {
        var pipeline = new WebViewPipeline();
        pipeline.Use(_ => throw new InvalidOperationException("bad route"));

        var error = Assert.Throws<InvalidOperationException>(() => pipeline.ApplyTo(new RecordingInterceptor()));

        Assert.Equal("bad route", error.Message);
    }

    /// <summary>
    /// 🔴 THE DEFAULT-WIRING TEST (D63): `app.Use…()` reaches a pipeline the KIT registered, resolved off
    /// the built app — not one this test handed over. Sabotage the `TryAddSingleton` in
    /// `ShenoraApplicationBuilder.Build` and this is what fails.
    /// </summary>
    [Fact]
    public void A_built_application_exposes_the_pipeline_its_own_composition_registered()
    {
        using var app = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.App",
            BaseDirectory = @"C:\MyApp",
            GetEnvironmentVariable = _ => null,
        }).Build();

        app.Use(i => ((RecordingInterceptor)i).Applied.Add("from-the-app"));

        // The SAME instance a shell would resolve to hand its webview.
        Assert.Same(app.Pipeline, app.Services.GetRequiredService<WebViewPipeline>());

        var interceptor = new RecordingInterceptor();
        app.Pipeline.ApplyTo(interceptor);
        Assert.Equal(["from-the-app"], interceptor.Applied);
    }

    private sealed class RecordingInterceptor : IWebViewInterceptor
    {
        public List<string> Applied { get; } = [];

        public WebViewRangeDelivery RangeDelivery => WebViewRangeDelivery.Sliced;

        public IDisposable Use(WebViewResourceMiddleware middleware) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}
