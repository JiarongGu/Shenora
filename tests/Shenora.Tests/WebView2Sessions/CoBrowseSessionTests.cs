using System.Globalization;
using System.Text.Json;
using Shenora.WebView2.Sessions;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The co-browse input protocol's pure builders — the wire shapes are kept IDENTICAL to the
/// source for mechanical adoption, so these pin them (clamps, modifier bitmask, VK map,
/// invariant-culture formatting). The live screencast/dispatch loop is the sample-e2e's subject.
/// </summary>
public class CoBrowseSessionTests
{
    [Fact]
    public void Metrics_json_clamps_and_defaults_the_dpr()
    {
        using var doc = JsonDocument.Parse(CoBrowseSession.BuildMetricsOverrideJson(5000, 100, null));
        var root = doc.RootElement;

        Assert.Equal(1560, root.GetProperty("width").GetInt32());   // clamped to the source bounds
        Assert.Equal(240, root.GetProperty("height").GetInt32());
        Assert.Equal(1.5, root.GetProperty("deviceScaleFactor").GetDouble()); // the crisp default
        Assert.False(root.GetProperty("mobile").GetBoolean());
        Assert.Equal(1560, root.GetProperty("screenWidth").GetInt32()); // screen mirrors the viewport
    }

    [Fact]
    public void Metrics_json_is_invariant_culture()
    {
        // "1,50" on a comma-decimal locale is broken JSON — the source fixed this live.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = CoBrowseSession.BuildMetricsOverrideJson(800, 600, 1.5);

            // The invariant is that the number is written with a DOT and parses back to the value it
            // was given — not that it renders in one particular way. This used to assert
            // `Contains("\"deviceScaleFactor\":1.50")`, which pinned the exact digit padding of the
            // format string (P5.5 H7): switching "0.00" to "0.##" or to a plain double would have
            // failed a culture test for a reason that has nothing to do with culture.
            Assert.DoesNotContain("1,5", json, StringComparison.Ordinal); // the comma-decimal break
            using var doc = JsonDocument.Parse(json);                     // parseable regardless of locale
            Assert.Equal(1.5, doc.RootElement.GetProperty("deviceScaleFactor").GetDouble());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("pressed", false, "mousePressed", 1)]
    [InlineData("released", false, "mouseReleased", 0)]
    [InlineData("moved", false, "mouseMoved", 0)]   // a free move — no button held
    [InlineData("moved", true, "mouseMoved", 1)]    // a DRAG move — the held button carries through (else drags can't work)
    public void Mouse_json_maps_events_and_scales_fractions_to_css_px(string clientEvent, bool buttonHeld, string cdpType, int buttons)
    {
        using var doc = JsonDocument.Parse(CoBrowseSession.BuildMouseEventJson(clientEvent, 0.5, 0.25, 1280, 860, buttonHeld));
        var root = doc.RootElement;

        Assert.Equal(cdpType, root.GetProperty("type").GetString());
        Assert.Equal(buttons, root.GetProperty("buttons").GetInt32());
        Assert.Equal(640, root.GetProperty("x").GetDouble());  // 0.5 × 1280
        Assert.Equal(215, root.GetProperty("y").GetDouble());  // 0.25 × 860
        Assert.Equal("left", root.GetProperty("button").GetString());
    }

    [Fact]
    public void Wheel_json_carries_the_delta()
    {
        using var doc = JsonDocument.Parse(CoBrowseSession.BuildWheelEventJson(0.1, 0.9, -120, 1000, 750));
        var root = doc.RootElement;

        Assert.Equal("mouseWheel", root.GetProperty("type").GetString());
        Assert.Equal(100, root.GetProperty("x").GetDouble());
        Assert.Equal(675, root.GetProperty("y").GetDouble());
        Assert.Equal(0, root.GetProperty("deltaX").GetDouble());
        Assert.Equal(-120, root.GetProperty("deltaY").GetDouble());
    }

    [Fact]
    public void Key_jsons_are_a_down_up_pair_with_the_modifier_bitmask_and_vk()
    {
        var pair = CoBrowseSession.BuildKeyEventJsons("a", alt: false, ctrl: true, meta: false, shift: true);

        Assert.Equal(2, pair.Length);
        using var down = JsonDocument.Parse(pair[0]);
        using var up = JsonDocument.Parse(pair[1]);
        Assert.Equal("keyDown", down.RootElement.GetProperty("type").GetString());
        Assert.Equal("keyUp", up.RootElement.GetProperty("type").GetString());
        Assert.Equal(2 | 8, down.RootElement.GetProperty("modifiers").GetInt32()); // ctrl=2 | shift=8
        Assert.Equal('A', down.RootElement.GetProperty("windowsVirtualKeyCode").GetInt32()); // Ctrl+A works
        Assert.Equal("KeyA", down.RootElement.GetProperty("code").GetString());
        Assert.Equal("a", down.RootElement.GetProperty("key").GetString()); // DOM key stays as sent
    }

    [Fact]
    public void An_unknown_key_omits_the_vk_and_code_so_cdp_infers_from_key()
    {
        using var doc = JsonDocument.Parse(
            CoBrowseSession.BuildKeyEventJsons("F13", alt: false, ctrl: false, meta: false, shift: false)[0]);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("windowsVirtualKeyCode", out _));
        Assert.False(root.TryGetProperty("code", out _));
        Assert.Equal(0, root.GetProperty("modifiers").GetInt32());
    }

    [Theory]
    [InlineData("Enter", 13, "Enter")]
    [InlineData("Backspace", 8, "Backspace")]
    [InlineData("ArrowLeft", 37, "ArrowLeft")]
    [InlineData(" ", 32, "Space")]
    [InlineData("z", 'Z', "KeyZ")]   // lowercase letters normalize to the uppercase VK
    [InlineData("Q", 'Q', "KeyQ")]
    [InlineData("7", '7', "Digit7")]
    public void The_vk_map_covers_navigation_editing_letters_and_digits(string key, int vk, string code)
    {
        Assert.Equal((vk, code), CoBrowseSession.KeyInfo(key));
    }

    [Fact]
    public async Task Start_validates_the_options_before_touching_the_ui()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        CoBrowseSessionOptions Options(int quality = 72, int buffer = 2, int maxW = 2560) => new()
        {
            Anchor = anchor,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"), KeepAliveInBackground = true },
            JpegQuality = quality,
            FrameBuffer = buffer,
            MaxFrameWidth = maxW,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CoBrowseSession.StartAsync(Options(quality: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CoBrowseSession.StartAsync(Options(buffer: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CoBrowseSession.StartAsync(Options(maxW: 0)));
    }
}
