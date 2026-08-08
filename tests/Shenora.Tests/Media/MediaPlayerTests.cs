using Shenora;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;
using Shenora.Core.Events;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Media;

/// <summary>
/// <see cref="MediaPlayer"/> — the .NET-owned lifecycle over a page element. Unlike the native player,
/// ALL of this is portable logic, so these exercise the real thing rather than pinning a contract a shell
/// must satisfy.
/// </summary>
public class MediaPlayerTests
{
    // ── the probe → plan → URL chain, which is why this class exists (D58) ────────────────────────

    [Fact]
    public async Task A_probed_source_reaches_ResolveUri_with_its_plan()
    {
        MediaPlaybackPlan? seen = null;
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions
        {
            Probe = (_, _) => Task.FromResult<MediaProbeResult?>(new MediaProbeResult
            {
                Container = ".mkv",
                Streams = [new MediaStreamInfo(MediaStreamKind.Audio, "ac3")],
            }),
            Policy = new MediaPlaybackPolicy
            {
                Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" },
                Codecs = new Dictionary<MediaStreamKind, IReadOnlySet<string>>
                {
                    [MediaStreamKind.Audio] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac" },
                },
            },
            ResolveUri = (source, plan) => { seen = plan; return $"app://convert?src={source}"; },
        });

        var open = player.OpenAsync(new MediaSource { Uri = "C:/media/film.mkv" });
        await bus.LoadedAsync();
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });
        await open;

        Assert.NotNull(seen);
        // .mkv is not a playable container and ac3 is not a playable codec, so the planner must not have
        // said Direct — which is the decision this class exists to make FOR the app.
        Assert.NotEqual(MediaPlaybackAction.Direct, seen!.Action);
        Assert.Equal("app://convert?src=C:/media/film.mkv", bus.LoadedUri);
    }

    /// <summary>
    /// 🔴 The failure this bound exists for: nothing ever reports, because the app's `PLAYER_REPORT` route
    /// is missing. Before `OpenTimeout` that awaited FOREVER — no exception, no log line, and an element
    /// visibly playing, so the symptom read as "my C# await hangs while the video works". The test asserts
    /// the MESSAGE, not just that it threw: a timeout that says "timed out" would leave the adopter exactly
    /// where they started, and naming the route is the entire value.
    /// </summary>
    [Fact]
    public async Task An_open_that_nothing_reports_on_FAILS_naming_the_missing_route()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions
        {
            OpenTimeout = TimeSpan.FromMilliseconds(150),
        });

        var error = await Assert.ThrowsAsync<MediaPlayerException>(
            () => player.OpenAsync(new MediaSource { Uri = "C:/media/film.mp4" }));

        Assert.Contains("PLAYER_REPORT", error.Message);
        Assert.Contains("SHENORA.MEDIA", error.Message);       // the module to route it on
        Assert.Contains("OpenTimeout", error.Message);          // and the knob, if it is genuinely slow
        Assert.Equal(MediaPlayerState.Failed, player.Status.State);
    }

    /// <summary>
    /// The bound must not fire on the ordinary path — a report arriving before it expires wins. Otherwise
    /// the fix would trade a silent hang for a spurious failure, which is worse: one is a wiring mistake
    /// the adopter can find, the other is the kit lying about their working code.
    /// </summary>
    [Fact]
    public async Task A_source_that_reports_in_time_is_unaffected_by_the_bound()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions
        {
            OpenTimeout = TimeSpan.FromSeconds(30),
        });

        var open = player.OpenAsync(new MediaSource { Uri = "C:/media/film.mp4" });
        await bus.LoadedAsync();
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });

        await open;   // no throw
        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
    }

    /// <summary>
    /// The caller's own cancellation is NOT a timeout and must not be dressed up as one: it is the app's
    /// decision and needs no explanation. Checked because both paths land in the same `catch`.
    /// </summary>
    [Fact]
    public async Task A_caller_cancelling_still_surfaces_as_cancellation_not_as_the_timeout_message()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions
        {
            OpenTimeout = TimeSpan.FromSeconds(30),
        });
        using var cts = new CancellationTokenSource();

        var open = player.OpenAsync(new MediaSource { Uri = "C:/media/film.mp4" }, cts.Token);
        await bus.LoadedAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
    }

    [Fact]
    public async Task With_no_probe_configured_the_plan_is_null_and_the_source_plays_directly()
    {
        var sawPlan = true;
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions
        {
            ResolveUri = (source, plan) => { sawPlan = plan is not null; return source; },
        });

        await OpenAndSettle(player, bus, "app://files/song.m4a");

        Assert.False(sawPlan);
        Assert.Equal("app://files/song.m4a", bus.LoadedUri);
    }

    /// <summary>
    /// ⚠ A probe that throws must NOT be fatal — the element may well play the file anyway, and failing
    /// here would make the player stricter than the thing it is driving.
    /// </summary>
    [Fact]
    public async Task A_throwing_probe_still_plays_the_source()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions
        {
            Probe = (_, _) => throw new InvalidOperationException("unreadable"),
            ResolveUri = (source, _) => source,
        });

        await OpenAndSettle(player, bus, "app://files/clip.mp4");

        Assert.Equal("app://files/clip.mp4", bus.LoadedUri);
    }

    /// <summary>An empty URL is how <c>ResolveUri</c> says "this cannot be played here".</summary>
    [Fact]
    public async Task An_unresolvable_source_fails_without_telling_the_page_to_load_anything()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (_, _) => "" });

        await Assert.ThrowsAsync<MediaPlayerException>(() => player.OpenAsync(new MediaSource { Uri = "x.avi" }));

        Assert.DoesNotContain(bus.Sent, e => e.Type == MediaPlayerEvents.Load);
        Assert.Equal(MediaPlayerState.Failed, player.Status.State);
    }

    // ── the page is the clock ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Position_and_duration_come_from_the_page()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        await OpenAndSettle(player, bus, "a.m4a");

        player.Report(new MediaPlayerStatus
        {
            State = MediaPlayerState.Playing,
            Position = TimeSpan.FromSeconds(42),
            Duration = TimeSpan.FromMinutes(4),
        });

        Assert.Equal(TimeSpan.FromSeconds(42), player.Status.Position);
        Assert.Equal(TimeSpan.FromMinutes(4), player.Status.Duration);
    }

    /// <summary>
    /// ⚠ The RATE is the player's, not the page's. A driver that does not carry one reports the default,
    /// and taking it verbatim would silently reset a configured 1.5x on the first report.
    /// </summary>
    [Fact]
    public async Task A_page_report_does_not_reset_the_configured_rate()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        await OpenAndSettle(player, bus, "a.m4a");

        player.Rate = 1.5;
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Playing });   // Rate defaults to 1.0

        Assert.Equal(1.5, player.Status.Rate);
        Assert.Contains(bus.Sent, e => e.Type == MediaPlayerEvents.Rate);
    }

    [Fact]
    public async Task Opening_waits_for_the_page_rather_than_assuming()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await bus.LoadedAsync();

        Assert.False(open.IsCompleted);
        Assert.Equal(MediaPlayerState.Opening, player.Status.State);

        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });
        await open;

        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
    }

    [Fact]
    public async Task A_page_that_reports_failure_fails_the_open()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var open = player.OpenAsync(new MediaSource { Uri = "a.m4a" });
        await bus.LoadedAsync();
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Failed, Error = "decode error" });

        var thrown = await Assert.ThrowsAsync<MediaPlayerException>(() => open);
        Assert.Equal("decode error", thrown.Message);
    }

    // ── lifecycle ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Transport_before_a_source_is_open_is_refused()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        await Assert.ThrowsAsync<MediaPlayerException>(() => player.PlayAsync());
        await Assert.ThrowsAsync<MediaPlayerException>(() => player.PauseAsync());
        await Assert.ThrowsAsync<MediaPlayerException>(() => player.SeekAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Transport_reaches_the_page_as_events()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        await OpenAndSettle(player, bus, "a.m4a");

        await player.PlayAsync();
        await player.PauseAsync();
        await player.SeekAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(bus.Sent, e => e.Type == MediaPlayerEvents.Play);
        Assert.Contains(bus.Sent, e => e.Type == MediaPlayerEvents.Pause);
        Assert.Contains(bus.Sent, e => e.Type == MediaPlayerEvents.Seek);
        Assert.All(bus.Sent, e => Assert.Equal("SHENORA.MEDIA", e.Module));
    }

    [Fact]
    public async Task Closing_unloads_the_page_element_and_empties_the_player()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        await OpenAndSettle(player, bus, "a.m4a");

        await player.CloseAsync();

        Assert.Contains(bus.Sent, e => e.Type == MediaPlayerEvents.Unload);
        Assert.Equal(MediaPlayerState.Empty, player.Status.State);
    }

    /// <summary>
    /// A user switching tracks quickly opens a second source while the first is still loading. The first
    /// waiter is cancelled — and, crucially, its cleanup must NOT unload the successor's source.
    /// </summary>
    [Fact]
    public async Task A_second_open_supersedes_the_first_without_unloading_it()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });

        var first = player.OpenAsync(new MediaSource { Uri = "first.m4a" });
        await bus.LoadedAsync();

        var second = player.OpenAsync(new MediaSource { Uri = "second.m4a" });
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });
        await second;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("second.m4a", bus.LoadedUri);
        Assert.DoesNotContain(bus.Sent, e => e.Type == MediaPlayerEvents.Unload);
    }

    [Fact]
    public async Task Seeking_backwards_past_zero_is_clamped()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        await OpenAndSettle(player, bus, "a.m4a");

        await player.SeekAsync(TimeSpan.FromSeconds(-30));

        var seek = Assert.Single(bus.Sent, e => e.Type == MediaPlayerEvents.Seek);
        Assert.Equal(0d, Read(seek.Payload, "position"));
    }

    [Fact]
    public void A_non_positive_rate_is_refused()
    {
        using var player = new MediaPlayer(new RecordingBus(), new MediaPlayerOptions { ResolveUri = (s, _) => s });

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
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions { ResolveUri = (s, _) => s });
        var session = new RecordingSession();
        using var _ = player.ReportTo(session);

        await OpenAndSettle(player, bus, "a.m4a");
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Playing, Position = TimeSpan.FromSeconds(9) });

        Assert.Contains(session.Reports, r => r.State == PlaybackState.Playing && r.Position == TimeSpan.FromSeconds(9));
    }

    // ── the URL convention: policy → plan → converter, across a string ───────────────────────────

    /// <summary>
    /// 🔴 <b>The planner's verdict survives the URL.</b> The plan is computed in the player and the route
    /// only ever sees a string, so anything not encoded here is re-derived from the file by a converter
    /// that never saw the policy — and two places deciding the same thing is how they come to disagree.
    /// </summary>
    [Theory]
    [InlineData(MediaPlaybackAction.Remux)]
    [InlineData(MediaPlaybackAction.Transcode)]
    public void The_planners_verdict_survives_the_round_trip(MediaPlaybackAction action)
    {
        var uri = MediaPlayerRoute.Build("C:/media/film.mkv", action);

        Assert.Equal("C:/media/film.mkv", MediaPlayerRoute.SourceOf(uri));
        Assert.Equal(action, MediaPlayerRoute.ActionOf(uri));
    }

    /// <summary>
    /// ⚠ A source containing the action marker must not truncate — the action is APPENDED, so the split is
    /// on the LAST occurrence. This is the case that makes the encoding unambiguous rather than merely
    /// usually right.
    /// </summary>
    [Fact]
    public void A_source_containing_the_marker_still_round_trips()
    {
        const string awkward = "C:/media/what&do=next.mkv";

        var uri = MediaPlayerRoute.Build(awkward, MediaPlaybackAction.Transcode);

        Assert.Equal(awkward, MediaPlayerRoute.SourceOf(uri));
        Assert.Equal(MediaPlaybackAction.Transcode, MediaPlayerRoute.ActionOf(uri));
    }

    /// <summary>A URL this route did not build is not its business — the pipeline falls through.</summary>
    [Fact]
    public void A_foreign_url_is_declined()
    {
        Assert.Null(MediaPlayerRoute.SourceOf("/assets/logo.png"));
    }

    /// <summary>
    /// An unreadable or absent action falls back to <see cref="MediaPlaybackAction.Remux"/> — the cheaper
    /// repair. A request reaching a conversion route at all means the container is wrong.
    /// </summary>
    [Fact]
    public void A_missing_action_falls_back_to_the_cheaper_repair()
    {
        Assert.Equal(MediaPlaybackAction.Remux, MediaPlayerRoute.ActionOf("/__shenora/media?src=a.mkv"));
        Assert.Equal(MediaPlaybackAction.Remux, MediaPlayerRoute.ActionOf("/__shenora/media?src=a.mkv&do=nonsense"));
    }

    // ── UseMediaPlayer: the zero-config call is the one that has to be right ─────────────────────

    /// <summary>
    /// 🔴 The owner's shape: *"a single {app}.useMediaPlayer then the system should work."* With no
    /// configuration the player must pass a source straight through — no probe, no plan, no rewriting.
    /// </summary>
    [Fact]
    public async Task With_no_configuration_a_source_is_passed_straight_through()
    {
        var bus = new RecordingBus();
        using var player = new MediaPlayer(bus, new MediaPlayerOptions());

        await OpenAndSettle(player, bus, "app://files/song.m4a");

        Assert.Equal("app://files/song.m4a", bus.LoadedUri);
    }

    /// <summary>
    /// ⚠ <see cref="MediaPlayerOptions.AllowedRoots"/> is the containment boundary, so its DEFAULT must be
    /// "nothing" rather than anything convenient. Empty also means no conversion route is wired, which is
    /// what makes the zero-argument call safe to hand anybody.
    /// </summary>
    [Fact]
    public void Conversion_is_off_until_roots_are_named()
    {
        var options = new MediaPlayerOptions();

        Assert.Empty(options.AllowedRoots);
        Assert.Null(options.ResolveUri);
        Assert.Null(options.CacheRoot);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task OpenAndSettle(MediaPlayer player, RecordingBus bus, string uri)
    {
        var open = player.OpenAsync(new MediaSource { Uri = uri });
        await bus.LoadedAsync();
        player.Report(new MediaPlayerStatus { State = MediaPlayerState.Paused });
        await open;
    }

    /// <summary>Read a property off an anonymous payload — the wire shape is untyped by design.</summary>
    private static double Read(object? payload, string name)
    {
        var value = payload?.GetType().GetProperty(name)?.GetValue(payload);
        return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The page, minus the page: captures what the player told it to do.</summary>
    private sealed class RecordingBus : IEventBus
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<(string Module, string Type, object? Payload)> Sent { get; } = [];
        public string? LoadedUri { get; private set; }

        public Task LoadedAsync() => _loaded.Task;

        public void Emit(string module, string type, object? payload = null, string? scope = null)
        {
            Sent.Add((module, type, payload));
            if (type == MediaPlayerEvents.Load)
            {
                LoadedUri = payload?.GetType().GetProperty("uri")?.GetValue(payload) as string;
                _loaded.TrySetResult();
            }
            else if (type == MediaPlayerEvents.Unload)
            {
                LoadedUri = null;
            }
        }

        public void Emit(EventMessage message) => Emit(message.Module, message.Type, message.Payload, message.Scope);
        public Task EmitAsync(EventMessage message) { Emit(message); return Task.CompletedTask; }
        public Task EmitAsync(string module, string type, object? payload = null, string? scope = null)
        {
            Emit(module, type, payload, scope);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string module, string type, string scope, Func<EventMessage, Task> handler) => new Noop();
        public IDisposable Subscribe(string module, string type, Func<EventMessage, Task> handler) => new Noop();
        public IDisposable SubscribeToModule(string module, string scope, Func<EventMessage, Task> handler) => new Noop();
        public IDisposable SubscribeToModule(string module, Func<EventMessage, Task> handler) => new Noop();
        public IDisposable SubscribeToAll(Func<EventMessage, Task> handler) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class RecordingSession : IPlaybackSession
    {
        public List<PlaybackProgress> Reports { get; } = [];
        public PlaybackCommands Supported { get; set; }
        public TimeSpan SkipInterval { get; set; } = TimeSpan.FromSeconds(15);
        public event Action<PlaybackCommandRequest>? CommandReceived;

        public void Publish(PlaybackInfo info) { }
        public void Report(PlaybackProgress progress) => Reports.Add(progress);
        public void Clear() { }
        internal void Unused() => CommandReceived?.Invoke(new PlaybackCommandRequest { Command = PlaybackCommand.Play });
    }
}
