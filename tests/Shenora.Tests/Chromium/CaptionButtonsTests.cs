using System.Text.Json;
using Shenora.Chromium.Host;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The page-drawn caption buttons' logic without a window: the Win32 subclass only feeds it messages, so what a
/// hover, a press and a release DO is decided here.
/// </summary>
public class CaptionButtonsTests
{
    private static readonly CaptionButtonRect Min = new(CaptionButtonKind.Minimize, 100, 0, 46, 32);
    private static readonly CaptionButtonRect Max = new(CaptionButtonKind.Maximize, 146, 0, 46, 32);
    private static readonly CaptionButtonRect Close = new(CaptionButtonKind.Close, 192, 0, 46, 32);

    private static (CaptionButtons Buttons, List<CaptionButtonState> States, List<CaptionButtonKind> Clicks) Create()
    {
        var states = new List<CaptionButtonState>();
        var clicks = new List<CaptionButtonKind>();
        var buttons = new CaptionButtons(states.Add, clicks.Add);
        buttons.Set([Min, Max, Close]);
        return (buttons, states, clicks);
    }

    [Fact]
    public void A_point_finds_its_button_and_the_right_edge_is_exclusive()
    {
        var (buttons, _, _) = Create();

        Assert.Equal(CaptionButtonKind.Maximize, buttons.At(146, 0));
        Assert.Equal(CaptionButtonKind.Close, buttons.At(192, 31));
        Assert.Null(buttons.At(238, 10));   // one past Close's right edge
        Assert.Null(buttons.At(150, 32));   // one past the bottom
        Assert.Null(buttons.At(10, 10));
    }

    [Fact]
    public void A_press_and_release_on_the_same_button_clicks_it_once()
    {
        var (buttons, states, clicks) = Create();

        buttons.Hover(CaptionButtonKind.Maximize);
        buttons.Press(CaptionButtonKind.Maximize);
        buttons.Release(CaptionButtonKind.Maximize);

        Assert.Equal([CaptionButtonKind.Maximize], clicks);
        Assert.Equal(
            [new CaptionButtonState(CaptionButtonKind.Maximize, null), new CaptionButtonState(CaptionButtonKind.Maximize, CaptionButtonKind.Maximize),
             new CaptionButtonState(CaptionButtonKind.Maximize, null)],
            states);
    }

    [Fact]
    public void A_release_on_another_button_than_the_press_clicks_nothing()
    {
        var (buttons, _, clicks) = Create();

        buttons.Press(CaptionButtonKind.Minimize);
        buttons.Release(CaptionButtonKind.Close);

        Assert.Empty(clicks);
    }

    // A native caption button captures the mouse for its press, so only it can show, and only under the pointer.
    [Fact]
    public void While_a_press_is_held_only_its_button_shows_and_only_while_the_pointer_is_on_it()
    {
        var (buttons, states, _) = Create();

        buttons.Press(CaptionButtonKind.Minimize);
        buttons.Hover(CaptionButtonKind.Close);   // another button does not light
        buttons.Hover(null);
        buttons.Hover(CaptionButtonKind.Minimize);   // back on: pressed again

        Assert.Equal(
            [new CaptionButtonState(CaptionButtonKind.Minimize, CaptionButtonKind.Minimize), new CaptionButtonState(null, null),
             new CaptionButtonState(CaptionButtonKind.Minimize, CaptionButtonKind.Minimize)],
            states);
    }

    [Fact]
    public void A_release_off_every_button_ends_the_press_and_clicks_nothing()
    {
        var (buttons, states, clicks) = Create();

        buttons.Press(CaptionButtonKind.Maximize);
        buttons.Release(null);   // on the drag bar
        buttons.Hover(CaptionButtonKind.Close);

        Assert.Empty(clicks);
        Assert.False(buttons.IsPressed);
        Assert.Equal(new CaptionButtonState(CaptionButtonKind.Close, null), states[^1]);
    }

    [Fact]
    public void A_press_dragged_off_and_back_clicks_on_its_release()
    {
        var (buttons, _, clicks) = Create();

        buttons.Press(CaptionButtonKind.Maximize);
        buttons.Hover(null);
        buttons.Hover(CaptionButtonKind.Maximize);
        buttons.Release(CaptionButtonKind.Maximize);

        Assert.Equal([CaptionButtonKind.Maximize], clicks);
    }

    [Fact]
    public void Leaving_the_non_client_area_clears_a_hover_but_not_a_held_press()
    {
        var (buttons, states, clicks) = Create();

        buttons.Hover(CaptionButtonKind.Close);
        buttons.Leave();
        Assert.Equal(new CaptionButtonState(null, null), states[^1]);

        // The capture decides where a held press is; a leave during it is noise.
        buttons.Press(CaptionButtonKind.Close);
        buttons.Leave();
        buttons.Release(CaptionButtonKind.Close);
        Assert.Equal([CaptionButtonKind.Close], clicks);
    }

    [Fact]
    public void A_lost_capture_cancels_the_press_so_its_release_clicks_nothing()
    {
        var (buttons, states, clicks) = Create();

        buttons.Press(CaptionButtonKind.Close);
        buttons.Cancel();
        buttons.Release(CaptionButtonKind.Close);

        Assert.Empty(clicks);
        Assert.Equal(new CaptionButtonState(null, null), states[1]);
    }

    [Fact]
    public void Clearing_the_regions_during_a_press_cancels_it()
    {
        var (buttons, _, clicks) = Create();

        buttons.Press(CaptionButtonKind.Close);
        buttons.Set([]);
        buttons.Release(CaptionButtonKind.Close);

        Assert.False(buttons.IsPressed);
        Assert.Empty(clicks);
    }

    [Fact]
    public void An_unchanged_state_is_not_sent_again()
    {
        var (buttons, states, _) = Create();

        buttons.Hover(CaptionButtonKind.Minimize);
        buttons.Hover(CaptionButtonKind.Minimize);
        buttons.Hover(null);
        buttons.Hover(null);

        Assert.Equal(2, states.Count);
    }

    [Fact]
    public void Clearing_the_regions_clears_a_live_hover_and_answers_no_button_anywhere()
    {
        var (buttons, states, _) = Create();
        buttons.Hover(CaptionButtonKind.Maximize);

        buttons.Set([]);

        Assert.True(buttons.IsEmpty);
        Assert.Null(buttons.At(150, 10));
        Assert.Equal(new CaptionButtonState(null, null), states[^1]);
    }

    [Fact]
    public void The_payload_is_scaled_to_client_pixels_and_odd_entries_are_skipped_not_fatal()
    {
        var payload = JsonDocument.Parse("""
            { "buttons": [
                { "kind": "minimize", "x": 50, "y": 0, "width": 23, "height": 16 },
                { "kind": "MAXIMIZE", "x": 73.25, "y": 0, "width": 23, "height": 16 },
                { "kind": "help", "x": 0, "y": 0, "width": 10, "height": 10 },
                { "kind": "close", "x": 96, "y": 0, "width": 0, "height": 16 },
                "not an object"
            ] }
            """).RootElement;

        var regions = CaptionButtons.Parse(payload, 2.0);

        Assert.Equal(
            [new CaptionButtonRect(CaptionButtonKind.Minimize, 100, 0, 46, 32), new CaptionButtonRect(CaptionButtonKind.Maximize, 146, 0, 46, 32)],
            regions);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "buttons": 3 }""")]
    public void A_payload_without_buttons_clears_them(string json) =>
        Assert.Empty(CaptionButtons.Parse(JsonDocument.Parse(json).RootElement, 1.0));

    [Fact]
    public void The_state_crosses_the_wire_as_the_clients_kinds_with_none_omitted()
    {
        Assert.Equal("""{"hot":"maximize","pressed":"close"}""", IpcJson.Serialize(new CaptionButtonState(CaptionButtonKind.Maximize, CaptionButtonKind.Close)));
        Assert.Equal("{}", IpcJson.Serialize(new CaptionButtonState(null, null)));
    }
}
