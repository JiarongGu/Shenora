using Shenora.Windows;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The desktop transport session's one piece of real logic: turning a reported position plus a remembered
/// duration into the timeline SMTC will accept.
/// <para>
/// Only this part is unit-testable — constructing a <see cref="WindowsPlaybackSession"/> spins a real
/// <c>MediaPlayer</c> and talks cross-process to a system service, so what the OS ACTUALLY does with the
/// timeline is the desktop sample's <c>PlaybackSessionProbe</c>, which reads it back out of Windows' own
/// session registry. These tests exist because the defect they cover shipped in exactly the gap between
/// those two: the call never threw, and nothing asked what the OS ended up believing.
/// </para>
/// </summary>
public class WindowsPlaybackSessionTests
{
    [Fact]
    public void A_known_duration_becomes_the_timeline_end()
    {
        // The whole defect (first adopter, 2026-08-05): PlaybackInfo.Duration was accepted and dropped, so
        // EndTime stayed 00:00:00 for a 240 s track and the flyout had no total to draw a scrubber against.
        var (position, end) = WindowsPlaybackSession.TimelineFor(
            TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(240));

        Assert.Equal(TimeSpan.FromSeconds(42), position);
        Assert.Equal(TimeSpan.FromSeconds(240), end);
    }

    [Fact]
    public void An_unknown_duration_still_leaves_the_end_at_zero()
    {
        // The PREVIOUS behaviour, pinned rather than discarded. A live stream has no end, and telling the OS
        // an item ends at 0 renders a permanently-full scrubber — so "unknown" must stay unknown. This is
        // the direction a fix like this breaks silently, which is why it is a test and not a comment.
        var (position, end) = WindowsPlaybackSession.TimelineFor(TimeSpan.FromSeconds(42), null);

        Assert.Equal(TimeSpan.FromSeconds(42), position);
        Assert.Equal(TimeSpan.Zero, end);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_duration_means_unknown_not_an_item_of_that_length(int seconds)
    {
        // Zero is what an uninitialised TimeSpan looks like, so treating it as a real length would turn
        // "the app has not measured this yet" into "this item is over" — the full-scrubber symptom again,
        // reached from a different direction.
        var (_, end) = WindowsPlaybackSession.TimelineFor(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(seconds));

        Assert.Equal(TimeSpan.Zero, end);
    }

    [Fact]
    public void A_position_past_the_end_is_clamped_rather_than_costing_the_duration()
    {
        // SMTC wants StartTime ≤ Position ≤ MaxSeekTime ≤ EndTime and rejects the whole timeline otherwise,
        // which would lose the duration as well as the position. Reporting a position a tick past the end is
        // ordinary at the moment a track finishes, so the incoherent field must not take the coherent one.
        var (position, end) = WindowsPlaybackSession.TimelineFor(
            TimeSpan.FromSeconds(241), TimeSpan.FromSeconds(240));

        Assert.Equal(TimeSpan.FromSeconds(240), position);
        Assert.Equal(TimeSpan.FromSeconds(240), end);
    }

    [Fact]
    public void A_negative_position_is_floored_at_zero_even_with_no_duration()
    {
        // Same reasoning as the clamp, on the other side, and it applies with no duration too — StartTime is
        // zero regardless, so a negative position is out of order on its own.
        var (position, end) = WindowsPlaybackSession.TimelineFor(TimeSpan.FromSeconds(-1), null);

        Assert.Equal(TimeSpan.Zero, position);
        Assert.Equal(TimeSpan.Zero, end);
    }
}
