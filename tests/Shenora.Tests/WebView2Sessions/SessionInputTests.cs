using System.Text.Json;
using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The typed input seam that replaced <c>DispatchInputAsync(string json)</c> (P5.5 H9.1 / D21), plus
/// the legacy parser that makes the migration mechanical.
/// <para>
/// The parser is the part that genuinely needs tests: it IS the compatibility contract for a client
/// that already speaks the old protocol, so every arm has to map to the same thing the old switch did.
/// The old code read fields with <c>GetProperty(...).GetDouble()</c>, which THREW on a missing or
/// wrong-typed field and relied on the caller swallowing it — an entire input silently dropped. The
/// parser reports false instead, so a caller can log it.
/// </para>
/// </summary>
public class SessionInputTests
{
    private static SessionInput Parse(string json)
    {
        Assert.True(SessionInput.TryParseLegacyJson(json, out var input), $"failed to parse: {json}");
        return input!;
    }

    [Fact]
    public void Legacy_viewport_maps_to_the_typed_viewport_input()
    {
        var input = Assert.IsType<SessionViewportInput>(
            Parse("""{"type":"viewport","width":1024,"height":768,"dpr":2}"""));

        Assert.Equal(1024, input.Width);
        Assert.Equal(768, input.Height);
        Assert.Equal(2, input.DeviceScaleFactor);
    }

    [Fact]
    public void Legacy_viewport_without_dpr_leaves_it_to_the_session_default()
    {
        // null, NOT 0 — the session clamps a supplied dpr into 1..2, so a 0 here would silently
        // become 1 and quietly de-crisp every frame.
        var input = Assert.IsType<SessionViewportInput>(Parse("""{"type":"viewport","width":800,"height":600}"""));

        Assert.Null(input.DeviceScaleFactor);
    }

    [Theory]
    [InlineData("pressed", SessionPointerAction.Down)]
    [InlineData("released", SessionPointerAction.Up)]
    [InlineData("moved", SessionPointerAction.Move)]
    // The old switch's default arm was mouseMoved, so anything unrecognised — including an absent
    // "event" — must still be a MOVE, or a client sending a variant spelling loses cursor tracking.
    [InlineData("wiggled", SessionPointerAction.Move)]
    public void Legacy_mouse_events_map_to_pointer_actions(string legacyEvent, SessionPointerAction expected)
    {
        var input = Assert.IsType<SessionPointerInput>(
            Parse($$"""{"type":"mouse","event":"{{legacyEvent}}","fx":0.25,"fy":0.75}"""));

        Assert.Equal(expected, input.Action);
        Assert.Equal(0.25, input.X);
        Assert.Equal(0.75, input.Y);
    }

    [Fact]
    public void Legacy_mouse_without_an_event_field_is_a_move()
    {
        Assert.Equal(SessionPointerAction.Move,
            Assert.IsType<SessionPointerInput>(Parse("""{"type":"mouse","fx":0.1,"fy":0.2}""")).Action);
    }

    [Fact]
    public void Legacy_wheel_and_text_map_across()
    {
        var wheel = Assert.IsType<SessionWheelInput>(Parse("""{"type":"wheel","fx":0.5,"fy":0.5,"dy":-120}"""));
        Assert.Equal(-120, wheel.DeltaY);

        Assert.Equal("hello", Assert.IsType<SessionTextInput>(Parse("""{"type":"text","text":"hello"}""")).Text);
    }

    [Fact]
    public void Legacy_key_carries_every_modifier_and_absent_flags_are_false()
    {
        var input = Assert.IsType<SessionKeyInput>(
            Parse("""{"type":"key","key":"a","ctrl":true,"shift":true}"""));

        Assert.Equal("a", input.Key);
        Assert.True(input.Ctrl);
        Assert.True(input.Shift);
        Assert.False(input.Alt);
        Assert.False(input.Meta);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]                                  // valid JSON, not an object
    [InlineData("null")]                                // valid JSON, not an object
    [InlineData("123")]
    [InlineData("""{"width":10}""")]                    // no "type"
    [InlineData("""{"type":"telepathy"}""")]            // unknown type
    [InlineData("""{"type":"key"}""")]                  // a key event with no key is not actionable
    [InlineData("""{"type":"key","key":""}""")]
    public void Unparseable_or_unknown_messages_report_false_rather_than_throwing(string? json)
    {
        // The old dispatcher threw on these and relied on the marshaller swallowing it, which meant a
        // malformed input was indistinguishable from a delivered one. Returning false is what lets a
        // caller notice.
        Assert.False(SessionInput.TryParseLegacyJson(json, out var input));
        Assert.Null(input);
    }

    [Fact]
    public void Missing_numeric_fields_default_to_zero_rather_than_dropping_the_input()
    {
        // The old code called GetProperty("fx").GetDouble(), which threw — losing the whole event.
        // A partial coordinate is recoverable; a vanished press is not (it strands a held button).
        var input = Assert.IsType<SessionPointerInput>(Parse("""{"type":"mouse","event":"pressed"}"""));

        Assert.Equal(SessionPointerAction.Down, input.Action);
        Assert.Equal(0, input.X);
        Assert.Equal(0, input.Y);
    }

    // ── Frame geometry (H9.3) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Frame_viewport_comes_from_the_frames_own_metadata()
    {
        using var doc = JsonDocument.Parse(
            """{"data":"x","sessionId":1,"metadata":{"deviceWidth":1024,"deviceHeight":768}}""");

        Assert.Equal((1024, 768), StreamingSession.ReadFrameViewport(doc.RootElement, 1280, 860));
    }

    [Theory]
    [InlineData("""{"data":"x"}""")]                                          // no metadata at all
    [InlineData("""{"data":"x","metadata":null}""")]
    [InlineData("""{"data":"x","metadata":{}}""")]                            // metadata without dimensions
    [InlineData("""{"data":"x","metadata":{"deviceWidth":"wide"}}""")]        // wrong type
    [InlineData("""{"data":"x","metadata":{"deviceWidth":0,"deviceHeight":0}}""")] // nonsense dimensions
    public void Frame_viewport_falls_back_to_the_emulated_viewport(string frameJson)
    {
        // A frame with plausible geometry beats a dropped frame: the fallback is exactly what the page
        // was told to emulate, so it is right whenever the metadata is merely absent.
        using var doc = JsonDocument.Parse(frameJson);

        Assert.Equal((1280, 860), StreamingSession.ReadFrameViewport(doc.RootElement, 1280, 860));
    }

    [Fact]
    public void Frame_viewport_falls_back_per_dimension()
    {
        using var doc = JsonDocument.Parse("""{"data":"x","metadata":{"deviceWidth":1024}}""");

        Assert.Equal((1024, 860), StreamingSession.ReadFrameViewport(doc.RootElement, 1280, 860));
    }
}
