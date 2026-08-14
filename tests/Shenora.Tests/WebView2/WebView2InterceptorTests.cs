using Shenora;
using Shenora.Windows;
using Shenora.Core.WebView;

namespace Shenora.Tests.WebView2;

/// <summary>
/// The desktop shell's half of the D45 interceptor. Two things here are worth a test and neither needs a
/// browser: the range-delivery constant a file route reads, and which origins a middleware can see at all.
/// <para>
/// What a test cannot reach is the wiring — that the filter is registered, that the bundle falls through to the
/// pipeline, that a 206 survives the hop to the page. That is the desktop sample's <c>InterceptorProbe</c>,
/// which runs it through a real WebView2; this repo has already shipped a resource feature that was broken in
/// exactly that gap while every unit test passed.
/// </para>
/// </summary>
public class WebView2InterceptorTests
{
    [Fact]
    public void The_desktop_delivers_sliced_bodies()
    {
        var host = new WebViewHost(new Microsoft.Web.WebView2.WinForms.WebView2(), new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = Path.GetTempPath() },
        });

        // Sliced means "a handler must slice the file itself" — ordinary HTTP. Measured rather than assumed:
        // see the remarks on WebView2Interceptor.RangeDelivery for the experiment in the sample that shows a
        // ten-byte body answered for `bytes=10-19` arriving as ten bytes, which Unsliced delivery could not do.
        Assert.Equal(WebViewRangeDelivery.Sliced, host.Interceptor.RangeDelivery);
    }

    /// <summary>
    /// 🔴 The desktop half of the app-level pipeline (D64): a route the app declared once with
    /// <c>app.UseFiles(…)</c> reaches THIS host's interceptor, because it travelled with the options.
    /// Applied during CONSTRUCTION, before anything can navigate — and before <c>InitializeAsync</c>,
    /// which is why the interceptor exists on the host from construction in the first place.
    /// <para>
    /// This is the whole reason a secondary window is covered now: it is built from the same options, so
    /// it gets the same routes without the app wiring each window by hand.
    /// </para>
    /// </summary>
    [Fact]
    public void An_app_level_pipeline_reaches_the_hosts_interceptor()
    {
        var applied = new List<IWebViewInterceptor>();
        var pipeline = new WebViewPipeline();
        pipeline.Use(applied.Add);

        var host = new WebViewHost(new Microsoft.Web.WebView2.WinForms.WebView2(), new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = Path.GetTempPath() },
            Pipeline = pipeline,
        });

        Assert.Same(host.Interceptor, Assert.Single(applied));
    }

    /// <summary>
    /// A host given no pipeline serves only its own routes — the deliberately isolated case (a session
    /// browser). It must not throw, because <c>Pipeline</c> is nullable precisely so that stays expressible.
    /// </summary>
    [Fact]
    public void A_host_with_no_pipeline_is_left_exactly_as_it_was()
    {
        var host = new WebViewHost(new Microsoft.Web.WebView2.WinForms.WebView2(), new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = Path.GetTempPath() },
        });

        Assert.NotNull(host.Interceptor);
    }

    [Fact]
    public void Production_needs_no_extra_filter_because_the_bundle_already_registers_the_page_origin()
        => Assert.Empty(WebView2Interceptor.ExtraFilters(isDevelopment: false, devUrl: "http://localhost:3517"));

    [Fact]
    public void Development_filters_the_dev_server_origin()
    {
        // Without this the D44 relative-URL contract would hold in a packaged build and 404 through every day
        // of development, because the page's origin in dev is Vite's and nothing registers a filter for it.
        Assert.Equal(["http://localhost:3517/*"],
            WebView2Interceptor.ExtraFilters(isDevelopment: true, devUrl: "http://localhost:3517"));
    }

    [Fact]
    public void A_dev_url_carrying_a_path_still_filters_the_whole_origin()
    {
        // The ORIGIN, not the string: filtering "http://localhost:3517/index.html*" would match one document
        // and miss every route under it.
        Assert.Equal(["http://localhost:3517/*"],
            WebView2Interceptor.ExtraFilters(isDevelopment: true, devUrl: "http://localhost:3517/index.html"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void An_absent_or_unparseable_dev_url_registers_nothing(string? devUrl)
        => Assert.Empty(WebView2Interceptor.ExtraFilters(isDevelopment: true, devUrl));
}
