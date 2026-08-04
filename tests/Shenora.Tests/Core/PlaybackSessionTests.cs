using Shenora.Core;

namespace Shenora.Tests.Core;

/// <summary>
/// The portable half of the system-transport contract. Only the shape is testable here — what a real OS
/// does with it is the desktop sample's <c>PlaybackSessionProbe</c> (which reads the published item back out
/// of Windows' own session registry) and the device runs on the mobile shells.
/// <para>
/// These are worth having anyway, because two of the defaults below are load-bearing and a change to either
/// is silent: a <see cref="PlaybackProgress.Rate"/> that defaults to 0 would freeze every timeline, and a
/// <see cref="PlaybackCommands"/> whose flag values overlap would light the wrong buttons.
/// </para>
/// </summary>
public class PlaybackSessionTests
{
    [Fact]
    public void Rate_defaults_to_normal_speed()
    {
        // 1.0, not 0.0. The OS extrapolates the displayed time from this, so a zero default would make
        // every caller that omits it report a permanently-frozen position — and it would look like the
        // platform ignoring us rather than a wrong default.
        Assert.Equal(1.0, new PlaybackProgress { State = PlaybackState.Playing }.Rate);
    }

    [Fact]
    public void Command_flags_are_distinct_powers_of_two()
    {
        var values = Enum.GetValues<PlaybackCommands>().Where(v => v != PlaybackCommands.None).ToArray();

        Assert.NotEmpty(values);
        foreach (var v in values)
        {
            var n = (int)v;
            // A flags enum whose members are not distinct bits silently aliases: asking for Next would
            // light Previous, and no test of the mapping code would catch it.
            Assert.True((n & (n - 1)) == 0, $"{v} = {n} is not a single bit");
        }
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Fact]
    public void Every_single_command_can_be_expressed_as_a_supported_flag()
    {
        // The two enums are separate on purpose (a set to offer vs one to receive), and that split only
        // works if they stay in step. A command with no matching flag could be delivered but never
        // offered, which is exactly the asymmetry a reader would assume cannot happen.
        var flagNames = Enum.GetNames<PlaybackCommands>().Where(n => n != nameof(PlaybackCommands.None)).ToHashSet();
        var missing = Enum.GetNames<PlaybackCommand>().Where(n => !flagNames.Contains(n)).ToArray();

        Assert.True(missing.Length == 0,
            $"PlaybackCommand has values with no PlaybackCommands flag: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Position_is_only_meaningful_for_Seek()
    {
        // Not enforced by the type — it is documented — so this pins the intended shape: everything else
        // carries no position, and a handler reading Position for a Play would get null rather than 0.
        var seek = new PlaybackCommandRequest { Command = PlaybackCommand.Seek, Position = TimeSpan.FromSeconds(30) };
        var play = new PlaybackCommandRequest { Command = PlaybackCommand.Play };

        Assert.Equal(TimeSpan.FromSeconds(30), seek.Position);
        Assert.Null(play.Position);
    }

    [Fact]
    public void Info_is_all_optional_so_an_untagged_file_can_still_be_published()
    {
        // A host that could not report a file with no tags would be useless for exactly the files most
        // likely to need a transport surface.
        var info = new PlaybackInfo();

        Assert.Null(info.Title);
        Assert.Null(info.Subtitle);
        Assert.Null(info.GroupName);
        Assert.Null(info.Duration);
        Assert.True(info.Artwork.IsEmpty);
    }

    [Fact]
    public void Buffering_is_distinct_from_Playing_and_Paused()
    {
        // Collapsing Buffering into Playing makes the OS extrapolate a position that is not advancing;
        // collapsing it into Paused tells the user they can resume. It has to be its own state.
        Assert.NotEqual(PlaybackState.Playing, PlaybackState.Buffering);
        Assert.NotEqual(PlaybackState.Paused, PlaybackState.Buffering);
        Assert.NotEqual(PlaybackState.Stopped, PlaybackState.Buffering);
    }

    [Fact]
    public void Skip_by_interval_exists_on_BOTH_enums_and_is_distinct_from_Next_and_Seek()
    {
        // Added on adopter feedback (2026-08-04): the first adopter had ±15 s working and gave it up to
        // adopt the kit. For long-form audio Next is the wrong granularity and Seek is a scrubber, so
        // neither substitutes — which is exactly why these are their own commands rather than aliases.
        Assert.NotEqual(PlaybackCommands.Next, PlaybackCommands.SkipForward);
        Assert.NotEqual(PlaybackCommands.Seek, PlaybackCommands.SkipForward);
        Assert.NotEqual(PlaybackCommands.Previous, PlaybackCommands.SkipBackward);
        Assert.NotEqual(PlaybackCommand.Next, PlaybackCommand.SkipForward);
        Assert.NotEqual(PlaybackCommand.Seek, PlaybackCommand.SkipForward);
    }

    [Fact]
    public void A_skip_request_carries_an_interval_and_a_seek_carries_a_position()
    {
        // The two are separate properties on purpose: a skip is relative and a seek is absolute, and one
        // field serving both would make a handler guess which meaning it received.
        var skip = new PlaybackCommandRequest
        {
            Command = PlaybackCommand.SkipForward,
            Interval = TimeSpan.FromSeconds(15),
        };
        var seek = new PlaybackCommandRequest
        {
            Command = PlaybackCommand.Seek,
            Position = TimeSpan.FromMinutes(3),
        };

        Assert.Equal(TimeSpan.FromSeconds(15), skip.Interval);
        Assert.Null(skip.Position);
        Assert.Equal(TimeSpan.FromMinutes(3), seek.Position);
        Assert.Null(seek.Interval);
    }
}
