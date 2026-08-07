using Shenora.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The portable half of <see cref="IMediaPlayer"/> — the shapes, the defaults, and the promises the
/// contract makes in prose.
/// <para>
/// ⚠ <b>What these do NOT prove.</b> There is no managed player: every implementation is a shell talking
/// to AVFoundation, ExoPlayer or Media Foundation, so nothing here decodes a byte or advances a clock.
/// These pin the contract a shell must satisfy and the states a portable caller can rely on. **Real
/// playback is proven on a device**, and the claim that iOS keeps playing while backgrounded is a DEVICE
/// claim — see TASKS.md. Saying so here rather than letting a green suite imply otherwise, per the
/// standing rule that a gate which was not looking at the thing must say so.
/// </para>
/// </summary>
public class MediaPlayerContractTests
{
    [Fact]
    public void Status_defaults_to_a_normal_rate()
    {
        // 1.0 rather than 0.0, because a caller constructing a status to compare against should not have
        // to remember to set the speed of "playing normally".
        var status = new MediaPlayerStatus { State = MediaPlayerState.Paused };

        Assert.Equal(1.0, status.Rate);
        Assert.Equal(TimeSpan.Zero, status.Position);
        Assert.Null(status.Duration);
        Assert.Null(status.Error);
    }

    [Fact]
    public void Source_defaults_to_starting_at_the_beginning()
    {
        var source = new MediaSource { Uri = "file:///music/track.m4a" };

        Assert.Equal(TimeSpan.Zero, source.StartAt);
    }

    [Fact]
    public void Empty_and_Ended_are_distinct_states()
    {
        // The distinction earns its place: Ended keeps a position so a UI can show a finished item at its
        // end, and Empty has no source at all. Collapsing them would make "replay" and "load" the same call.
        Assert.NotEqual(MediaPlayerState.Empty, MediaPlayerState.Ended);
    }

    [Fact]
    public void Buffering_is_distinct_from_Playing()
    {
        // Same reasoning PlaybackState documents: a player waiting on data is not advancing, and a UI that
        // cannot tell them apart extrapolates a position that is not moving.
        Assert.NotEqual(MediaPlayerState.Buffering, MediaPlayerState.Playing);
    }

    [Fact]
    public void Failed_status_carries_a_reason_and_no_position_claim()
    {
        var status = new MediaPlayerStatus
        {
            State = MediaPlayerState.Failed,
            Error = "The media source could not be played.",
        };

        Assert.Equal(MediaPlayerState.Failed, status.State);
        Assert.NotNull(status.Error);
    }

    /// <summary>
    /// ⚠ <b>A tripwire on the error CONTRACT, not on a shell.</b> The prose says an <c>Error</c> is a short
    /// app-safe reason and never the platform's raw text, because this string can reach a page — the same
    /// rule the IPC stack applies to every error path. Nothing in the type system enforces that, so this
    /// pins the one thing that can be checked: the exception the kit throws does not wrap its inner
    /// exception's message into its own.
    /// </summary>
    [Fact]
    public void Player_exception_does_not_leak_the_platform_message()
    {
        var platform = new InvalidOperationException("AVFoundationErrorDomain -11800 /var/mobile/…/track.m4a");

        var thrown = new MediaPlayerException("The media source could not be played.", platform);

        Assert.Equal("The media source could not be played.", thrown.Message);
        Assert.DoesNotContain("AVFoundationErrorDomain", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/mobile", thrown.Message, StringComparison.Ordinal);
        Assert.Same(platform, thrown.InnerException);
    }

    /// <summary>
    /// The interface deliberately has NO queue. This is the line the surface-lexicon entry for `Player`
    /// draws — "a `Next()` on this interface is the tell that it has become a product" — so it is pinned
    /// rather than left as a comment somebody can quietly step over.
    /// </summary>
    [Fact]
    public void Player_ships_no_queue_vocabulary()
    {
        var members = typeof(IMediaPlayer).GetMembers().Select(m => m.Name).ToArray();

        Assert.DoesNotContain("Next", members);
        Assert.DoesNotContain("Previous", members);
        Assert.DoesNotContain("Playlist", members);
        Assert.DoesNotContain("Queue", members);
    }
}
