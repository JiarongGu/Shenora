using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>A press on the splash's drawn title strip (Linux): a drag once the pointer moves past the slop, a double-click
/// to maximize, and nothing for a plain click.</summary>
public class StripGestureTests
{
    [Fact]
    public void A_press_that_moves_past_the_slop_starts_one_drag_from_where_it_was_pressed()
    {
        var gesture = new StripGesture();
        Assert.Equal(StripGesture.Act.None, gesture.Down(100, 10, atMs: 0));
        Assert.Equal(StripGesture.Act.None, gesture.Move(102, 11));          // within the slop
        Assert.Equal(StripGesture.Act.Drag, gesture.Move(110, 12));
        Assert.Equal((100, 10), gesture.DragFrom);
        Assert.Equal(StripGesture.Act.None, gesture.Move(150, 40));          // one drag, not one per move
        gesture.Up();
    }

    [Fact]
    public void Two_quick_presses_in_one_place_are_a_double_click()
    {
        var gesture = new StripGesture();
        gesture.Down(100, 10, atMs: 1000);
        gesture.Up();
        Assert.Equal(StripGesture.Act.ToggleMaximize, gesture.Down(101, 11, atMs: 1300));
        gesture.Up();
        // A third press starts afresh: it is not a second double-click.
        Assert.Equal(StripGesture.Act.None, gesture.Down(101, 11, atMs: 1500));
    }

    [Theory]
    [InlineData(1000 + StripGesture.DoubleClickMs + 1, 100)]   // too slow
    [InlineData(1100, 120)]                                    // too far
    public void A_slow_or_distant_second_press_is_not_a_double_click(long atMs, int x)
    {
        var gesture = new StripGesture();
        gesture.Down(100, 10, atMs: 1000);
        gesture.Up();
        Assert.Equal(StripGesture.Act.None, gesture.Down(x, 10, atMs));
    }

    [Fact]
    public void A_press_that_dragged_does_not_count_toward_a_double_click()
    {
        var gesture = new StripGesture();
        gesture.Down(100, 10, atMs: 1000);
        gesture.Move(200, 10);
        gesture.Up();
        Assert.Equal(StripGesture.Act.None, gesture.Down(200, 10, atMs: 1100));
    }

    [Theory]
    [InlineData(uint.MaxValue - 10, 5)]   // X's 32-bit millisecond clock wrapped between the presses (every 49.7 days)
    [InlineData(1000, 500)]               // a clock that went back
    public void A_second_press_that_reads_as_earlier_is_not_a_double_click(long first, long second)
    {
        var gesture = new StripGesture();
        gesture.Down(100, 10, first);
        gesture.Up();
        Assert.Equal(StripGesture.Act.None, gesture.Down(100, 10, second));
    }

    [Fact]
    public void A_move_with_nothing_pressed_does_nothing() =>
        Assert.Equal(StripGesture.Act.None, new StripGesture().Move(300, 20));
}
