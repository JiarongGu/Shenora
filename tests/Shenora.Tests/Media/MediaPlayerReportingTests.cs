using Shenora.Core;
using Shenora.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// <see cref="MediaPlayerExtensions.ReportTo"/> — the reconciliation D54 asked for, and the one part of
/// the player story that is PURE and can therefore be proven here rather than on a device.
/// </summary>
public class MediaPlayerReportingTests
{
    [Theory]
    [InlineData(MediaPlayerState.Playing, PlaybackState.Playing)]
    [InlineData(MediaPlayerState.Paused, PlaybackState.Paused)]
    [InlineData(MediaPlayerState.Buffering, PlaybackState.Buffering)]
    [InlineData(MediaPlayerState.Opening, PlaybackState.Buffering)]
    [InlineData(MediaPlayerState.Ended, PlaybackState.Stopped)]
    [InlineData(MediaPlayerState.Failed, PlaybackState.Stopped)]
    public void Player_state_reaches_the_session_in_the_OS_vocabulary(MediaPlayerState from, PlaybackState expected)
    {
        var player = new FakePlayer();
        var session = new FakeSession();
        using var _ = player.ReportTo(session);

        player.Raise(new MediaPlayerStatus { State = from });

        Assert.Equal(expected, Assert.Single(session.Reports).State);
    }

    [Fact]
    public void Position_and_rate_are_carried_through()
    {
        var player = new FakePlayer();
        var session = new FakeSession();
        using var _ = player.ReportTo(session);

        player.Raise(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromMinutes(3),
            Rate = 1.5,
        });

        var report = Assert.Single(session.Reports);
        Assert.Equal(TimeSpan.FromMinutes(3), report.Position);
        Assert.Equal(1.5, report.Rate);
    }

    /// <summary>
    /// Empty means the source is gone, which is <see cref="IPlaybackSession.Clear"/> — not a report of
    /// <see cref="PlaybackState.Stopped"/>, which would leave the app on the lock screen with a resumable
    /// item that no longer exists.
    /// </summary>
    [Fact]
    public void Closing_the_player_takes_the_app_off_the_lock_screen()
    {
        var player = new FakePlayer();
        var session = new FakeSession();
        using var _ = player.ReportTo(session);

        player.Raise(new MediaPlayerStatus { State = MediaPlayerState.Empty });

        Assert.Equal(1, session.Cleared);
        Assert.Empty(session.Reports);
    }

    /// <summary>
    /// ⚠ The trap this pins: <see cref="IPlaybackSession.Publish"/> takes a WHOLE
    /// <see cref="PlaybackInfo"/>, so a bridge that published the duration it knows would blank the title
    /// and artwork the app had already set. Metadata stays the app's.
    /// </summary>
    [Fact]
    public void Reporting_never_publishes_metadata()
    {
        var player = new FakePlayer();
        var session = new FakeSession();
        using var _ = player.ReportTo(session);

        player.Raise(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Duration = TimeSpan.FromMinutes(50),
        });

        Assert.Empty(session.Published);
    }

    [Fact]
    public void Disposing_the_handle_stops_the_reporting()
    {
        var player = new FakePlayer();
        var session = new FakeSession();
        var handle = player.ReportTo(session);

        player.Raise(new MediaPlayerStatus { State = MediaPlayerState.Playing });
        handle.Dispose();
        player.Raise(new MediaPlayerStatus { State = MediaPlayerState.Paused });

        Assert.Equal(PlaybackState.Playing, Assert.Single(session.Reports).State);
    }

    /// <summary>
    /// Disposing twice must not detach a handler a LATER pairing attached — the failure mode of a naive
    /// unsubscriber when a caller disposes in both a <c>finally</c> and a <c>Dispose</c>.
    /// </summary>
    [Fact]
    public void Disposing_twice_does_not_disturb_a_later_pairing()
    {
        var player = new FakePlayer();
        var first = new FakeSession();
        var handle = player.ReportTo(first);
        handle.Dispose();

        var second = new FakeSession();
        using var _ = player.ReportTo(second);
        handle.Dispose();                       // the second dispose of the FIRST handle

        player.Raise(new MediaPlayerStatus { State = MediaPlayerState.Playing });

        Assert.Single(second.Reports);
        Assert.Empty(first.Reports);
    }

    private sealed class FakePlayer : IMediaPlayer
    {
        public MediaPlayerStatus Status { get; private set; } = new() { State = MediaPlayerState.Empty };
        public double Rate { get; set; } = 1.0;
        public event Action<MediaPlayerStatus>? StateChanged;

        public void Raise(MediaPlayerStatus status)
        {
            Status = status;
            StateChanged?.Invoke(status);
        }

        public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSession : IPlaybackSession
    {
        public List<PlaybackProgress> Reports { get; } = [];
        public List<PlaybackInfo> Published { get; } = [];
        public int Cleared { get; private set; }

        public PlaybackCommands Supported { get; set; }
        public TimeSpan SkipInterval { get; set; } = TimeSpan.FromSeconds(15);
        public event Action<PlaybackCommandRequest>? CommandReceived;

        public void Publish(PlaybackInfo info) => Published.Add(info);
        public void Report(PlaybackProgress progress) => Reports.Add(progress);
        public void Clear() => Cleared++;

        // Never raised — the fake is a sink. Referenced so the compiler does not warn it is unused.
        internal void Unused() => CommandReceived?.Invoke(new PlaybackCommandRequest { Command = PlaybackCommand.Play });
    }
}
