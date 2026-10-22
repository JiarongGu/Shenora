using Microsoft.Extensions.Time.Testing;
using Shenora.Core.Events;
using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// Moving the playhead between the page's player and the platform's.
///
/// <para>
/// 🔴 <b>Every test here asserts the native player was USED, not merely present.</b> That is D63's rule and
/// it is the reason this file exists in the shape it does: a handoff that quietly does nothing is
/// indistinguishable from one that works, because the page keeps playing either way until the app is
/// actually backgrounded — which no unit test can do.
/// </para>
/// <para>
/// ⚠ What these CANNOT prove is that playback survives backgrounding; that needs a device and is measured
/// in the sample (`.claude/knowledge/mobile-shells.md`). What they DO prove is the decision logic that four
/// separate hardware defects came from — the ordering, the single owner, the missing source, and the
/// end-of-media case.
/// </para>
/// </summary>
public class BackgroundPlaybackTransferTests
{
    /// <summary>A player that records what was asked of it, in order.</summary>
    private sealed class FakePlayer : IMediaPlayer
    {
        public List<string> Calls { get; } = [];
        public MediaPlayerStatus Status { get; set; } = new() { State = MediaPlayerState.Empty };
        public event Action<MediaPlayerStatus>? StateChanged;
        public double Rate { get; set; } = 1.0;

        /// <summary>Runs inside <see cref="OpenAsync"/> — what lands while a native player opens.</summary>
        public Action? OnOpen { get; set; }

        /// <summary>A report reaching this player, as the page's would.</summary>
        public void Raise(MediaPlayerStatus status) => StateChanged?.Invoke(status);

        public bool Watched => StateChanged is not null;

        public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            Calls.Add($"open:{source.Uri}");
            StateChanged?.Invoke(Status);
            OnOpen?.Invoke();
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) { Calls.Add("play"); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { Calls.Add("pause"); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            Calls.Add($"seek:{position.TotalSeconds:F2}");
            return Task.CompletedTask;
        }
        public Task CloseAsync(CancellationToken cancellationToken = default) { Calls.Add("close"); return Task.CompletedTask; }
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default) { Calls.Add($"rate:{rate}"); return Task.CompletedTask; }
        public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) { Calls.Add($"volume:{volume}"); return Task.CompletedTask; }
    }

    private static (FakePlayer Page, FakePlayer Native, BackgroundPlaybackTransfer Handoff) Build(
        MediaPlayerStatus page, string? source = "/library/film.mkv")
    {
        var pagePlayer = new FakePlayer { Status = page };
        var nativePlayer = new FakePlayer();
        var handoff = new BackgroundPlaybackTransfer(pagePlayer, nativePlayer,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => source });
        return (pagePlayer, nativePlayer, handoff);
    }

    [Fact]
    public async Task The_native_player_is_OPENED_SOUGHT_and_PLAYED_at_the_pages_position()
    {
        var (_, native, handoff) = Build(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(34.98),
        });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.TookOver, result.Outcome);
        // D63: the seam is proven USED, and in the right ORDER — seeking after play would start the film at
        // its opening for as long as it took the seek to land.
        Assert.Equal(["open:/library/film.mkv", "seek:34.98", "play"], native.Calls);
    }

    /// <summary>
    /// 🔴 A film the USER paused stays paused when they leave the app. It was handed over and played on
    /// natively, because the platform's own pause of the hidden page reached the host as <c>Paused</c> and
    /// looked the same; <c>useMediaPlayer</c> now drops that pause, so <c>Paused</c> here is the user's.
    /// Measured on Android: a paused film was <c>TookOver</c> before this, and the page goes hidden before
    /// the platform pauses it.
    /// </summary>
    [Fact]
    public async Task A_page_the_user_PAUSED_is_left_paused()
    {
        var (_, native, handoff) = Build(new MediaPlayerStatus
        {
            State = MediaPlayerState.Paused,
            Position = TimeSpan.FromSeconds(12),
        });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Nothing, result.Outcome);
        Assert.Empty(native.Calls);
    }

    /// <summary>A page caught BUFFERING was playing and stalled: its film goes on in the background.</summary>
    [Fact]
    public async Task A_BUFFERING_page_hands_over()
    {
        var (_, native, handoff) = Build(new MediaPlayerStatus
        {
            State = MediaPlayerState.Buffering,
            Position = TimeSpan.FromSeconds(7),
        });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.TookOver, result.Outcome);
        Assert.Equal(["open:/library/film.mkv", "seek:7.00", "play"], native.Calls);
    }

    // ── the position: the page player's estimate, or the page's own report when it lands during the open ──

    /// <summary>
    /// 🔴 The defect, end to end with the kit's own page player: playing since 3 s, and no report for 20 s
    /// because the page reports transitions only. Its hidden-time report lost the race with
    /// <c>Window.Stopped</c> in 3 runs of 5 on Android, and the film restarted natively near where it began.
    /// </summary>
    [Fact]
    public async Task Steady_playback_with_no_report_since_it_began_is_handed_over_where_it_has_got_to()
    {
        var clock = new FakeTimeProvider();
        using var page = new MediaPlayer(new EventBus(), new MediaPlayerOptions(), clock);
        page.Report(new MediaPlayerStatus { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(3) });
        clock.Advance(TimeSpan.FromSeconds(20));
        var native = new FakePlayer();
        var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv" });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.TookOver, result.Outcome);
        Assert.Equal(["open:/library/film.mkv", "seek:23.00", "play"], native.Calls);
    }

    /// <summary>
    /// The page's hidden-time report landing while the native player opens is the page's own reading, and wins
    /// over the estimate taken at <c>Window.Stopped</c> — which runs on past the platform's pause of the hidden
    /// page. Nothing waits for it.
    /// </summary>
    [Fact]
    public async Task A_page_report_that_lands_while_the_native_player_opens_is_the_position_handed_over()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(6.6),
        });
        native.OnOpen = () => page.Raise(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(6.2),
        });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(TimeSpan.FromSeconds(6.2), result.Position);
        Assert.Equal(["open:/library/film.mkv", "seek:6.20", "play"], native.Calls);
        Assert.False(page.Watched);
    }

    /// <summary>The decision was made at <c>Window.Stopped</c>: a report that is not playing — the platform's
    /// pause of the hidden page is one — neither unmakes it nor moves the position.</summary>
    [Fact]
    public async Task A_report_during_the_open_that_is_not_playing_leaves_the_estimate()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(6.6),
        });
        native.OnOpen = () => page.Raise(new MediaPlayerStatus
        {
            State = MediaPlayerState.Paused,
            Position = TimeSpan.FromSeconds(6.3),
        });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.TookOver, result.Outcome);
        Assert.Equal(["open:/library/film.mkv", "seek:6.60", "play"], native.Calls);
    }

    /// <summary>Neither a finished film nor one still opening is playing, so neither is handed over.</summary>
    [Theory]
    [InlineData(MediaPlayerState.Ended)]
    [InlineData(MediaPlayerState.Opening)]
    public async Task A_page_that_is_not_playing_moves_nothing(MediaPlayerState state)
    {
        var (_, native, handoff) = Build(new MediaPlayerStatus { State = state, Position = TimeSpan.FromSeconds(60) });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Nothing, result.Outcome);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public async Task Nothing_playing_moves_nothing_and_is_not_a_failure()
    {
        var (_, native, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Empty });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Nothing, result.Outcome);
        Assert.Empty(native.Calls);
    }

    /// <summary>An app that cannot map its own source gets a NAMED outcome, not a silent no-op.</summary>
    [Fact]
    public async Task An_unresolved_source_is_reported_rather_than_opened_as_null()
    {
        var (_, native, handoff) = Build(
            new MediaPlayerStatus { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(5) },
            source: null);

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Unresolved, result.Outcome);
        Assert.Empty(native.Calls);
    }

    /// <summary>A throwing app resolver must not become the kit's exception.</summary>
    [Fact]
    public async Task A_resolver_that_throws_is_treated_as_no_source()
    {
        var handoff = new BackgroundPlaybackTransfer(
            new FakePlayer { Status = new MediaPlayerStatus { State = MediaPlayerState.Playing } },
            new FakePlayer(),
            new BackgroundPlaybackOptions { ResolveNativeSource = () => throw new InvalidOperationException("app bug") });

        var result = await handoff.ToBackgroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Unresolved, result.Outcome);
    }

    [Fact]
    public async Task Coming_back_mid_clip_seeks_the_page_and_plays_it()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Empty });
        native.Status = new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(53.31),
            Duration = TimeSpan.FromSeconds(60),
        };

        var result = await handoff.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Resumed, result.Outcome);
        Assert.Equal(["close"], native.Calls);
        Assert.Equal(["seek:53.31", "play"], page.Calls);
    }

    /// <summary>
    /// A native player the handoff STARTED and something then paused while the app was away — the lock screen's
    /// controls, wired by the app — hands back a page at its position and paused: playing on would undo that pause
    /// the moment the user returns.
    /// </summary>
    [Fact]
    public async Task Coming_back_to_a_native_player_PAUSED_while_away_leaves_the_page_paused()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(10) });
        await handoff.ToBackgroundAsync();
        native.Status = new MediaPlayerStatus
        {
            State = MediaPlayerState.Paused,
            Position = TimeSpan.FromSeconds(21.5),
            Duration = TimeSpan.FromSeconds(60),
        };

        var result = await handoff.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Paused, result.Outcome);
        Assert.Equal(["seek:21.50"], page.Calls);
    }

    /// <summary>
    /// A native player reads Paused between opening and playing. Back before the handoff has played it, that is not
    /// anyone's pause: the page resumes, as it always did.
    /// </summary>
    [Fact]
    public async Task Coming_back_before_the_handoff_played_the_native_player_resumes_the_page()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Empty });
        native.Status = new MediaPlayerStatus { State = MediaPlayerState.Paused, Position = TimeSpan.FromSeconds(3) };

        var result = await handoff.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Resumed, result.Outcome);
        Assert.Equal(["seek:3.00", "play"], page.Calls);
    }

    /// <summary>Paused AT its end is finished, not paused: the page is parked there rather than sought to it.</summary>
    [Fact]
    public async Task A_native_player_paused_at_its_end_counts_as_finished()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(10) });
        await handoff.ToBackgroundAsync();
        native.Status = new MediaPlayerStatus
        {
            State = MediaPlayerState.Paused,
            Position = TimeSpan.FromSeconds(60),
            Duration = TimeSpan.FromSeconds(60),
        };

        var result = await handoff.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Finished, result.Outcome);
        Assert.Equal(["pause"], page.Calls);
    }

    /// <summary>
    /// 🔴 THE ONE THAT TURNS A FEATURE INTO A BUG REPORT. Handing back the END position seeks the element to
    /// its duration, which REWINDS it, and the follow-up play() runs the opening titles — measured on the iOS
    /// simulator as `resumed t=0.00` after a 60 s clip finished in the background. So a finished playback
    /// hands back a finished PAGE.
    /// </summary>
    [Fact]
    public async Task Playback_that_FINISHED_while_away_parks_the_page_instead_of_restarting_it()
    {
        var (page, native, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Empty });
        native.Status = new MediaPlayerStatus
        {
            State = MediaPlayerState.Ended,
            Position = TimeSpan.FromSeconds(60),
            Duration = TimeSpan.FromSeconds(60),
        };

        var result = await handoff.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Finished, result.Outcome);
        Assert.Equal(["pause"], page.Calls);
        Assert.DoesNotContain("play", page.Calls);
    }

    /// <summary>
    /// ⚠ And the case a state check ALONE misses: a player that stopped a few milliseconds short of its own
    /// duration without reporting Ended. Restarting the film there is the same defect wearing a different hat.
    /// </summary>
    [Fact]
    public async Task A_player_that_stops_just_short_of_its_duration_counts_as_finished()
    {
        var (page, _, handoff) = Build(new MediaPlayerStatus { State = MediaPlayerState.Empty });
        var native = new FakePlayer
        {
            Status = new MediaPlayerStatus
            {
                State = MediaPlayerState.Playing,
                Position = TimeSpan.FromSeconds(59.8),
                Duration = TimeSpan.FromSeconds(60),
            },
        };
        var handoffShort = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv" });

        var result = await handoffShort.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Finished, result.Outcome);
        Assert.DoesNotContain("play", page.Calls);
    }

    /// <summary>A native player that will not close must not cost the page its playhead.</summary>
    [Fact]
    public async Task A_native_close_that_throws_still_hands_the_playhead_back()
    {
        var page = new FakePlayer();
        var native = new ThrowingClosePlayer
        {
            Status = new MediaPlayerStatus
            {
                State = MediaPlayerState.Playing,
                Position = TimeSpan.FromSeconds(20),
                Duration = TimeSpan.FromSeconds(60),
            },
        };
        var handoff = new BackgroundPlaybackTransfer(page, native,
            new BackgroundPlaybackOptions { ResolveNativeSource = () => "/library/film.mkv" });

        var result = await handoff.ToForegroundAsync();

        Assert.Equal(BackgroundPlaybackOutcome.Resumed, result.Outcome);
        Assert.Equal(["seek:20.00", "play"], page.Calls);
    }

    private sealed class ThrowingClosePlayer : FakePlayerBase
    {
        public override Task CloseAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the platform player refused to close");
    }

    /// <summary>The overridable half of <see cref="FakePlayer"/>, for the one test that needs a throw.</summary>
    private class FakePlayerBase : IMediaPlayer
    {
        public List<string> Calls { get; } = [];
        public MediaPlayerStatus Status { get; set; } = new() { State = MediaPlayerState.Empty };
        public event Action<MediaPlayerStatus>? StateChanged;
        public double Rate { get; set; } = 1.0;

        public virtual Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            Calls.Add($"open:{source.Uri}");
            StateChanged?.Invoke(Status);
            return Task.CompletedTask;
        }

        public virtual Task PlayAsync(CancellationToken cancellationToken = default) { Calls.Add("play"); return Task.CompletedTask; }
        public virtual Task PauseAsync(CancellationToken cancellationToken = default) { Calls.Add("pause"); return Task.CompletedTask; }
        public virtual Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Calls.Add($"seek:{position.TotalSeconds:F2}"); return Task.CompletedTask; }
        public virtual Task CloseAsync(CancellationToken cancellationToken = default) { Calls.Add("close"); return Task.CompletedTask; }
        public virtual Task SetRateAsync(double rate, CancellationToken cancellationToken = default) { Calls.Add($"rate:{rate}"); return Task.CompletedTask; }
        public virtual Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default) { Calls.Add($"volume:{volume}"); return Task.CompletedTask; }
    }
}
