using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Media;

/// <summary>
/// The session's commands reaching a player (<see cref="MediaPlayerExtensions.ObeyCommandsFrom"/>), and the
/// background handoff keeping the session on whichever player holds the playhead
/// (<see cref="BackgroundPlaybackOptions.Session"/>). Measured on an iPhone without the second: Control Center showed
/// stale state, and its play resumed the page's muted video in the background while the native player played.
/// </summary>
public class PlaybackSessionPairingTests
{
    [Theory]
    [InlineData(PlaybackCommand.Play, "play")]
    [InlineData(PlaybackCommand.Pause, "pause")]
    [InlineData(PlaybackCommand.Stop, "pause")]
    public async Task A_command_reaches_the_player(PlaybackCommand command, string expected)
    {
        var player = new FakePlayer { Status = Playing(10) };
        var session = new FakeSession();
        using var _ = player.ObeyCommandsFrom(session);

        await session.Send(new PlaybackCommandRequest { Command = command });

        Assert.Equal([expected], player.Calls);
    }

    [Theory]
    [InlineData(MediaPlayerState.Playing, "pause")]
    [InlineData(MediaPlayerState.Buffering, "pause")]
    [InlineData(MediaPlayerState.Paused, "play")]
    public async Task The_toggle_does_whichever_is_not_current(MediaPlayerState state, string expected)
    {
        var player = new FakePlayer { Status = new MediaPlayerStatus { State = state } };
        var session = new FakeSession();
        using var _ = player.ObeyCommandsFrom(session);

        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.TogglePlayPause });

        Assert.Equal([expected], player.Calls);
    }

    [Fact]
    public async Task Seeks_and_skips_move_the_position_and_a_skip_back_stops_at_zero()
    {
        var player = new FakePlayer { Status = Playing(10) };
        var session = new FakeSession { SkipInterval = TimeSpan.FromSeconds(15) };
        using var _ = player.ObeyCommandsFrom(session);

        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Seek, Position = TimeSpan.FromSeconds(42) });
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.SkipForward });
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.SkipForward, Interval = TimeSpan.FromSeconds(5) });
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.SkipBackward });

        Assert.Equal(["seek:42.00", "seek:25.00", "seek:15.00", "seek:0.00"], player.Calls);
    }

    /// <summary>Play on a finished item starts it again; measured on an iPhone without it, the player went to
    /// Playing at its end and played nothing.</summary>
    [Theory]
    [InlineData(PlaybackCommand.Play)]
    [InlineData(PlaybackCommand.TogglePlayPause)]
    public async Task Play_on_a_finished_item_starts_it_again(PlaybackCommand command)
    {
        var player = new FakePlayer { Status = new MediaPlayerStatus { State = MediaPlayerState.Ended, Position = TimeSpan.FromSeconds(60) } };
        var session = new FakeSession();
        using var _ = player.ObeyCommandsFrom(session);

        await session.Send(new PlaybackCommandRequest { Command = command });

        Assert.Equal(["seek:0.00", "play"], player.Calls);
    }

    /// <summary>A queue's commands are the app's: a player has no next item to go to.</summary>
    [Fact]
    public async Task Next_and_previous_are_left_to_the_app()
    {
        var player = new FakePlayer { Status = Playing(10) };
        var session = new FakeSession();
        using var _ = player.ObeyCommandsFrom(session);

        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Next });
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Previous });

        Assert.Empty(player.Calls);
    }

    [Fact]
    public async Task Disposing_the_pairing_stops_the_commands()
    {
        var player = new FakePlayer { Status = Playing(10) };
        var session = new FakeSession();
        player.ObeyCommandsFrom(session).Dispose();

        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Pause });

        Assert.Empty(player.Calls);
        Assert.False(session.Watched);
    }

    /// <summary>Commands arrive on the platform's thread: a refusal is the player's problem, logged, never thrown.</summary>
    [Fact]
    public async Task A_player_that_refuses_a_command_is_logged()
    {
        var player = new FakePlayer { Status = Playing(10), Refuse = true };
        var session = new FakeSession();
        var log = new Recorder();
        using var _ = player.ObeyCommandsFrom(session, log);

        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Play });

        Assert.Equal(["play"], player.Calls);
        var (level, text) = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("refused the Play command", text);
    }

    /// <summary>A transition between the first report's read and its landing reached the session first; the newer
    /// state is said again rather than left behind the older one.</summary>
    [Fact]
    public void The_first_report_is_said_again_when_the_player_moved_under_it()
    {
        var player = new FakePlayer { Status = Playing(30), Then = new MediaPlayerStatus { State = MediaPlayerState.Paused, Position = TimeSpan.FromSeconds(30.5) } };
        var session = new FakeSession();

        MediaPlayerExtensions.ReportNow(player, session);

        Assert.Equal([PlaybackState.Playing, PlaybackState.Paused], session.Reports.Select(r => r.State));
    }

    // ── the background handoff keeping the session on the player with the playhead ───────────────

    [Fact]
    public async Task The_session_follows_the_playhead_to_the_native_player_and_back()
    {
        var page = new FakePlayer { Status = Playing(30) };
        var native = new FakePlayer { Status = Playing(31) };
        var session = new FakeSession();
        using var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv", Session = session });

        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Pause });
        Assert.Equal(["pause"], page.Calls);

        await handoff.ToBackgroundAsync();
        page.Calls.Clear();
        native.Calls.Clear();
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Pause });
        Assert.Empty(page.Calls);
        Assert.Equal(["pause"], native.Calls);
        // The session hears the native player at once, not at its next transition.
        Assert.Equal(TimeSpan.FromSeconds(31), Assert.Single(session.Reports).Position);

        await handoff.ToForegroundAsync();
        page.Calls.Clear();
        native.Calls.Clear();
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Play });
        Assert.Equal(["play"], page.Calls);
        Assert.Empty(native.Calls);
    }

    /// <summary>A return while the native player is STARTING: the handback closes it and the page carries on, so the
    /// handoff finishing afterwards must not move the session onto the player just closed.</summary>
    [Fact]
    public async Task A_return_while_the_native_player_starts_keeps_the_session_on_the_page()
    {
        var page = new FakePlayer { Status = Playing(30) };
        var native = new FakePlayer { Status = Playing(30), PlayGate = new TaskCompletionSource() };
        var session = new FakeSession();
        using var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv", Session = session });

        var leaving = handoff.ToBackgroundAsync();
        await native.PlayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await handoff.ToForegroundAsync();
        native.PlayGate.SetResult();
        var left = await leaving;

        Assert.Equal(BackgroundPlaybackOutcome.Nothing, left.Outcome);
        page.Calls.Clear();
        native.Calls.Clear();
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Pause });
        Assert.Equal(["pause"], page.Calls);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task A_return_while_the_native_player_opens_keeps_the_session_on_the_page()
    {
        var page = new FakePlayer { Status = Playing(30) };
        var native = new FakePlayer { Status = Playing(30), OpenGate = new TaskCompletionSource() };
        var session = new FakeSession();
        using var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv", Session = session });

        var leaving = handoff.ToBackgroundAsync();
        await handoff.ToForegroundAsync();
        Assert.Equal(BackgroundPlaybackOutcome.Nothing, (await leaving).Outcome);

        page.Calls.Clear();
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Pause });
        Assert.Equal(["pause"], page.Calls);
        Assert.False(native.Watched);
    }

    /// <summary>A transfer replaced — a page loading again — must let go, or two transfers obey every command; and a
    /// handoff after that must not take the session up again.</summary>
    [Fact]
    public async Task A_disposed_transfer_lets_go_of_the_session()
    {
        var page = new FakePlayer { Status = Playing(30) };
        var native = new FakePlayer { Status = Playing(30) };
        var session = new FakeSession();
        var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv", Session = session });

        handoff.Dispose();
        await handoff.ToBackgroundAsync();
        await session.Send(new PlaybackCommandRequest { Command = PlaybackCommand.Pause });

        Assert.DoesNotContain("pause", page.Calls);
        Assert.DoesNotContain("pause", native.Calls);
        Assert.False(session.Watched);
    }

    [Fact]
    public async Task A_transfer_disposed_during_a_handoff_lets_go_of_the_native_player()
    {
        var native = new FakePlayer { Status = Playing(30) };
        var session = new FakeSession();
        var handoff = new BackgroundPlaybackTransfer(new FakePlayer { Status = Playing(30) }, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv", Session = session });

        await handoff.ToBackgroundAsync();
        handoff.Dispose();

        Assert.False(session.Watched);
        Assert.False(native.Watched);
    }

    [Fact]
    public async Task Without_a_session_nothing_is_paired()
    {
        var page = new FakePlayer { Status = Playing(30) };
        var native = new FakePlayer { Status = Playing(30) };
        using var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv" });

        Assert.Equal(BackgroundPlaybackOutcome.TookOver, (await handoff.ToBackgroundAsync()).Outcome);

        Assert.False(page.Watched);
        Assert.False(native.Watched);
    }

    private static MediaPlayerStatus Playing(double seconds) =>
        new() { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(seconds) };

    private sealed class FakeSession : IPlaybackSession
    {
        public List<PlaybackProgress> Reports { get; } = [];
        public PlaybackCommands Supported { get; set; }
        public TimeSpan SkipInterval { get; set; } = TimeSpan.FromSeconds(15);
        public event Action<PlaybackCommandRequest>? CommandReceived;
        public bool Watched => CommandReceived is not null;

        public void Publish(PlaybackInfo info) { }
        public void Report(PlaybackProgress progress) => Reports.Add(progress);
        public void Clear() { }

        /// <summary>Raise a command, then let the player's asynchronous handling finish.</summary>
        public async Task Send(PlaybackCommandRequest request)
        {
            CommandReceived?.Invoke(request);
            await Task.Delay(20);
        }
    }

    private sealed class FakePlayer : IMediaPlayer
    {
        private MediaPlayerStatus _status = new() { State = MediaPlayerState.Empty };

        public List<string> Calls { get; } = [];
        public bool Refuse { get; init; }
        public event Action<MediaPlayerStatus>? StateChanged;
        public bool Watched => StateChanged is not null;

        /// <summary>The status after the next read — a transition landing between a read and what follows it.</summary>
        public MediaPlayerStatus? Then { get; set; }

        public MediaPlayerStatus Status
        {
            get
            {
                var status = _status;
                if (Then is { } next) { _status = next; Then = null; }
                return status;
            }
            set => _status = value;
        }

        /// <summary>Holds the open until released, honouring the caller's cancellation.</summary>
        public TaskCompletionSource? OpenGate { get; init; }

        /// <summary>Holds a play until released WITHOUT honouring cancellation, as a synchronous platform start does.</summary>
        public TaskCompletionSource? PlayGate { get; init; }
        public TaskCompletionSource PlayEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task Do(string call)
        {
            Calls.Add(call);
            return Refuse ? Task.FromException(new MediaPlayerException("refused")) : Task.CompletedTask;
        }

        public async Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            StateChanged?.Invoke(Status);
            await Do($"open:{source.Uri}");
            if (OpenGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        }

        public async Task PlayAsync(CancellationToken cancellationToken = default)
        {
            await Do("play");
            PlayEntered.TrySetResult();
            if (PlayGate is { } gate) await gate.Task;
        }

        public Task PauseAsync(CancellationToken cancellationToken = default) => Do("pause");
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Do($"seek:{position.TotalSeconds:F2}");
        public Task CloseAsync(CancellationToken cancellationToken = default) => Do("close");
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) => Do($"rate:{rate}");
    }

    private sealed class Recorder : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> formatter) => Lines.Add((level, formatter(state, error)));
    }
}
