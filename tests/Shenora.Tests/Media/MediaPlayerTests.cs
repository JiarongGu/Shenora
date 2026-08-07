using Shenora.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// <see cref="MediaPlayer"/> — the .NET-owned lifecycle over a page element. Unlike the native player,
/// ALL of this is portable logic, so these tests exercise the real thing rather than pinning a contract a
/// shell must satisfy.
/// </summary>
public class MediaPlayerTests
{
    // ── the probe → plan → URL chain, which is why this class exists ──────────────────────────────

    [Fact]
    public async Task A_probed_source_reaches_ResolveUri_with_its_plan()
    {
        MediaPlaybackPlan? seen = null;
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions
        {
            Probe = (_, _) => Task.FromResult<MediaProbeResult?>(new MediaProbeResult
            {
                Container = ".mkv",
                Streams = [new MediaStreamInfo(MediaStreamKind.Audio, "ac3")],
            }),
            Policy = new MediaPlaybackPolicy
            {
                Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" },
                AudioCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac" },
            },
            ResolveUri = (source, plan) => { seen = plan; return $"app://convert?src={source}"; },
        });

        var open = player.OpenAsync(new MediaSource { Uri = "C:/media/film.mkv" });
        await target.ReadyAsync();
        await open;

        Assert.NotNull(seen);
        // .mkv is not a playable container and ac3 is not a playable codec, so the planner must not have
        // said Direct — which is the decision this class exists to make FOR the app.
        Assert.NotEqual(MediaPlaybackAction.Direct, seen!.Action);
        Assert.Equal("app://convert?src=C:/media/film.mkv", target.LoadedUri);
    }

    [Fact]
    public async Task With_no_probe_configured_the_plan_is_null_and_the_source_plays_directly()
    {
        var sawPlan = true;
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions
        {
            ResolveUri = (source, plan) => { sawPlan = plan is not null; return source; },
        });

        var open = player.OpenAsync(new MediaSource { Uri = "app://files/song.m4a" });
        await target.ReadyAsync();
        await open;

        Assert.False(sawPlan);
        Assert.Equal("app://files/song.m4a", target.LoadedUri);
    }

    /// <summary>
    /// ⚠ A probe that throws must NOT be fatal — the surface may well play the file anyway, and failing
    /// here would make the player stricter than the element it is driving.
    /// </summary>
    [Fact]
    public async Task A_throwing_probe_still_plays_the_source()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions
        {
            Probe = (_, _) => throw new InvalidOperationException("unreadable"),
            ResolveUri = (source, _) => source,
        });

        var open = player.OpenAsync(new MediaSource { Uri = "app://files/clip.mp4" });
        await target.ReadyAsync();
        await open;

        Assert.Equal("app://files/clip.mp4", target.LoadedUri);
    }

    /// <summary>
    /// An empty URL is how <c>ResolveUri</c> says "this cannot be played here" — the planner's
    /// <see cref="MediaPlaybackAction.Unsupported"/> reaching its natural conclusion.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_source_fails_without_loading_anything()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (_, _) => "" });

        await Assert.ThrowsAsync<MediaPlayerException>(() => player.OpenAsync(new MediaSource { Uri = "x.avi" }));

        Assert.Null(target.LoadedUri);
        Assert.Equal(MediaPlayerState.Failed, player.Status.State);
    }

    // ── the surface is the clock ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Position_and_duration_come_from_the_surface()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.ReadyAsync();
        await open;

        target.Report(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(42),
            Duration = TimeSpan.FromMinutes(4),
        });

        Assert.Equal(TimeSpan.FromSeconds(42), player.Status.Position);
        Assert.Equal(TimeSpan.FromMinutes(4), player.Status.Duration);
    }

    /// <summary>
    /// ⚠ The RATE is the player's, not the surface's. A target that does not carry one reports the default,
    /// and taking it verbatim would silently reset a configured 1.5x on the first report.
    /// </summary>
    [Fact]
    public async Task A_surface_report_does_not_reset_the_configured_rate()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.ReadyAsync();
        await open;

        player.Rate = 1.5;
        target.Report(new MediaPlayerStatus { State = MediaPlayerState.Playing });   // Rate defaults to 1.0

        Assert.Equal(1.5, player.Status.Rate);
        Assert.Equal(1.5, target.Rate);
    }

    [Fact]
    public async Task Opening_waits_for_the_surface_rather_than_assuming()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.LoadedAsync();

        Assert.False(open.IsCompleted);
        Assert.Equal(MediaPlayerState.Opening, player.Status.State);

        target.Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });
        await open;

        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
    }

    [Fact]
    public async Task A_surface_that_reports_failure_fails_the_open()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.LoadedAsync();
        target.Report(new MediaPlayerStatus { State = MediaPlayerState.Failed, Error = "decode error" });

        var thrown = await Assert.ThrowsAsync<MediaPlayerException>(() => open);
        Assert.Equal("decode error", thrown.Message);
    }

    // ── lifecycle ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transport_before_a_source_is_open_is_refused()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        await Assert.ThrowsAsync<MediaPlayerException>(() => player.PlayAsync());
        await Assert.ThrowsAsync<MediaPlayerException>(() => player.PauseAsync());
        await Assert.ThrowsAsync<MediaPlayerException>(() => player.SeekAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Closing_unloads_the_surface_and_empties_the_player()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.ReadyAsync();
        await open;

        await player.CloseAsync();

        Assert.True(target.Unloaded);
        Assert.Equal(MediaPlayerState.Empty, player.Status.State);
    }

    /// <summary>
    /// A user switching tracks quickly opens a second source while the first is still loading. The first
    /// waiter is cancelled rather than left hanging forever.
    /// </summary>
    [Fact]
    public async Task A_second_open_supersedes_the_first()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var first = player.OpenAsync(new MediaSource { Uri = "first.m4a" });
        await target.LoadedAsync();

        var second = player.OpenAsync(new MediaSource { Uri = "second.m4a" });
        await target.ReadyAsync();
        await second;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("second.m4a", target.LoadedUri);
    }

    [Fact]
    public async Task Seeking_backwards_past_zero_is_clamped()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.ReadyAsync();
        await open;

        await player.SeekAsync(TimeSpan.FromSeconds(-30));

        Assert.Equal(TimeSpan.Zero, target.SoughtTo);
    }

    [Fact]
    public void A_non_positive_rate_is_refused()
    {
        using var player = new MediaPlayer(new FakeTarget(), new MediaPlayerOptions { ResolveUri = (s, _) => s });

        Assert.Throws<ArgumentOutOfRangeException>(() => player.Rate = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => player.Rate = -1);
    }

    /// <summary>
    /// The whole point of both players implementing <see cref="IMediaPlayer"/>: an app holds one field and
    /// the Now Playing bridge works for either.
    /// </summary>
    [Fact]
    public async Task It_composes_with_ReportTo_like_any_other_player()
    {
        var target = new FakeTarget();
        using var player = new MediaPlayer(target, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        var session = new RecordingSession();
        using var _ = player.ReportTo(session);

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await target.ReadyAsync();
        await open;
        target.Report(new MediaPlayerStatus { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(9) });

        Assert.Contains(session.Reports, r => r.State == Shenora.Core.PlaybackState.Playing && r.Position == TimeSpan.FromSeconds(9));
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A page element, minus the page.</summary>
    private sealed class FakeTarget : IMediaRenderTarget
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LoadedUri { get; private set; }
        public TimeSpan SoughtTo { get; private set; } = TimeSpan.MinValue;
        public double Rate { get; private set; } = 1.0;
        public bool Unloaded { get; private set; }

        public event Action<MediaPlayerStatus>? Reported;

        public Task LoadAsync(string uri, TimeSpan startAt, CancellationToken cancellationToken = default)
        {
            LoadedUri = uri;
            Unloaded = false;
            _loaded.TrySetResult();
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            SoughtTo = position;
            return Task.CompletedTask;
        }

        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
        {
            Rate = rate;
            return Task.CompletedTask;
        }

        public Task UnloadAsync(CancellationToken cancellationToken = default)
        {
            Unloaded = true;
            LoadedUri = null;
            return Task.CompletedTask;
        }

        public void Report(MediaPlayerStatus status) => Reported?.Invoke(status);

        /// <summary>Wait until the player has handed this target a URL.</summary>
        public Task LoadedAsync() => _loaded.Task;

        /// <summary>Wait for the load, then answer it the way a healthy element would.</summary>
        public async Task ReadyAsync()
        {
            await _loaded.Task.ConfigureAwait(false);
            Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });
        }
    }

    private sealed class RecordingSession : Shenora.Core.IPlaybackSession
    {
        public List<Shenora.Core.PlaybackProgress> Reports { get; } = [];
        public Shenora.Core.PlaybackCommands Supported { get; set; }
        public TimeSpan SkipInterval { get; set; } = TimeSpan.FromSeconds(15);
        public event Action<Shenora.Core.PlaybackCommandRequest>? CommandReceived;

        public void Publish(Shenora.Core.PlaybackInfo info) { }
        public void Report(Shenora.Core.PlaybackProgress progress) => Reports.Add(progress);
        public void Clear() { }
        internal void Unused() => CommandReceived?.Invoke(new Shenora.Core.PlaybackCommandRequest { Command = Shenora.Core.PlaybackCommand.Play });
    }
}
