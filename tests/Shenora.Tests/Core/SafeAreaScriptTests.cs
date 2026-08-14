using Shenora;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Core;

/// <summary>
/// The safe-area script's DECISIONS, tested where they can be tested — with no device, no webview and no
/// platform. What is deliberately left to a device is only "the platform's numbers are right" and "the
/// script runs"; everything below is the part that was got wrong by hand first and would be got wrong
/// again.
/// </summary>
public class SafeAreaScriptTests
{
    private static readonly SafeAreaInsets Real = new(49, 0, 24, 0);

    [Fact]
    public void A_measurement_publishes_all_four_edges()
    {
        var js = SafeAreaScript.Build(new SafeAreaOptions(), Real);

        Assert.Contains("'--sa-top','49px'", js);
        Assert.Contains("'--sa-right','0px'", js);
        Assert.Contains("'--sa-bottom','24px'", js);
        Assert.Contains("'--sa-left','0px'", js);
    }

    // ── The decision the whole feature exists for ─────────────────────────────────────────────────

    [Fact]
    public void An_EMPTY_measurement_does_NOT_overwrite_the_configured_default()
    {
        // THE bug this feature is for. Android reports 0 on every edge for the whole first page load;
        // writing those zeros over a good default is how the first screen ends up under the status bar.
        var options = new SafeAreaOptions { Default = new SafeAreaInsets(24, 0, 24, 0) };

        var js = SafeAreaScript.Build(options, SafeAreaInsets.None);

        Assert.Contains("'--sa-top','24px'", js);
        Assert.DoesNotContain("'--sa-top','0px'", js);
    }

    [Fact]
    public void A_real_measurement_DOES_replace_the_default()
    {
        // The other direction, and without it the test above passes just as happily against a build
        // that ignores measurements entirely.
        var options = new SafeAreaOptions { Default = new SafeAreaInsets(24, 0, 24, 0) };

        var js = SafeAreaScript.Build(options, Real);

        Assert.Contains("'--sa-top','49px'", js);
        Assert.DoesNotContain("'--sa-top','24px'", js);
    }

    [Fact]
    public void With_no_default_and_no_measurement_NOTHING_is_published()
    {
        // An app that declines the default gets the plain env() behaviour it has today — the script must
        // not invent a value, because "0" is a claim about the device rather than an absence of one.
        var js = SafeAreaScript.Build(new SafeAreaOptions());

        Assert.DoesNotContain("--sa-top", js);
    }

    // ── Everything is individually declinable (D21) ───────────────────────────────────────────────

    [Fact]
    public void Settle_color_and_splash_are_each_absent_unless_asked_for()
    {
        var js = SafeAreaScript.Build(new SafeAreaOptions(), Real);

        Assert.DoesNotContain("--sa-settle", js);
        Assert.DoesNotContain("--sa-color", js);
        Assert.DoesNotContain("shenora-safe-splash", js);
    }

    [Fact]
    public void Each_one_appears_when_it_is()
    {
        var js = SafeAreaScript.Build(new SafeAreaOptions
        {
            Settle = TimeSpan.FromMilliseconds(180),
            Color = "#14161a",
            Splash = true,
        }, Real);

        Assert.Contains("'--sa-settle','180ms'", js);
        Assert.Contains("'--sa-color','#14161a'", js);
        Assert.Contains("shenora-safe-splash", js);
    }

    [Fact]
    public void The_variable_prefix_is_configurable()
    {
        // An app with its own design-token naming should not have to adopt ours.
        var js = SafeAreaScript.Build(new SafeAreaOptions { VariablePrefix = "--app-inset-" }, Real);

        Assert.Contains("'--app-inset-top','49px'", js);
        Assert.DoesNotContain("--sa-top", js);
    }

    // ── The splash's escape hatch ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_splash_ALWAYS_carries_a_self_dismissing_timeout()
    {
        // The failure mode a splash introduces is worse than the one it hides: if the platform never
        // reports, a page covered forever is a bricked app. The timeout must be in the script whether or
        // not a measurement ever arrives.
        var js = SafeAreaScript.Build(new SafeAreaOptions { Splash = true, SplashTimeout = TimeSpan.FromSeconds(3) });

        Assert.Contains("3000", js);
        Assert.Contains("__shenoraDismissSafeSplash", js);
    }

    [Fact]
    public void A_real_measurement_dismisses_the_splash_immediately()
    {
        var js = SafeAreaScript.Build(new SafeAreaOptions { Splash = true }, Real);
        Assert.Contains("window.__shenoraDismissSafeSplash();", js);
    }

    [Fact]
    public void An_empty_measurement_does_NOT_dismiss_the_splash()
    {
        // Dismissing on the first-load zeros would uncover the page at exactly the wrong moment — the
        // one the splash exists to cover.
        var js = SafeAreaScript.Build(new SafeAreaOptions { Splash = true }, SafeAreaInsets.None);
        Assert.DoesNotContain("window.__shenoraDismissSafeSplash();\"", js);
        Assert.DoesNotContain("';window.__shenoraDismissSafeSplash();})", js);
    }

    [Fact]
    public void The_splash_colour_falls_back_to_Color_then_to_transparent()
    {
        Assert.Contains("background:#111", SafeAreaScript.Build(new SafeAreaOptions
        { Splash = true, SplashColor = "#111", Color = "#222" }));
        Assert.Contains("background:#222", SafeAreaScript.Build(new SafeAreaOptions
        { Splash = true, Color = "#222" }));
        Assert.Contains("background:transparent", SafeAreaScript.Build(new SafeAreaOptions { Splash = true }));
    }

    // ── Hygiene ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_app_supplied_colour_containing_a_quote_cannot_break_the_script()
    {
        // Not a security boundary — the app is being protected from its own typo — but an unescaped
        // quote would silently break the ENTIRE injected script, which is the worst outcome available.
        var js = SafeAreaScript.Build(new SafeAreaOptions { Color = "rgb(0,0,0)'; alert('x" }, Real);

        Assert.DoesNotContain("'; alert('x", js);
        Assert.Contains("\\'", js);
    }

    [Fact]
    public void Fractional_insets_survive_as_fractions()
    {
        // iOS reports non-integer insets (the home indicator is 34, but scaled scenes are not integral).
        // Rounding here would move content by up to a pixel on every device that does it.
        var js = SafeAreaScript.Build(new SafeAreaOptions(), new SafeAreaInsets(48.75, 0, 0, 0));
        Assert.Contains("'--sa-top','48.75px'", js);
    }

    [Fact]
    public void The_script_reports_a_DELIVERY_marker()
    {
        // Load-bearing, not decoration. Evaluating script against a webview with no document does not
        // throw — it silently does nothing — so "the call succeeded" and "the page got it" are the same
        // observation without this. That is exactly how the first version shipped publishing to nobody.
        var js = SafeAreaScript.Build(new SafeAreaOptions(), Real);

        Assert.Contains($"return '{SafeAreaScript.DeliveredMarker}';", js);
    }

    [Fact]
    public void The_script_is_a_self_contained_expression()
    {
        // It is injected at document start, possibly before <body>, and possibly more than once. A
        // leaked global or a bare statement would collide with the page.
        var js = SafeAreaScript.Build(new SafeAreaOptions { Splash = true }, Real);
        Assert.StartsWith("(function(){", js);
        Assert.EndsWith("})();", js);
    }
}
