using Shenora.Modules.Media;

using Shenora;
namespace Shenora.Tests.Media;

/// <summary>
/// 🔴 <b>The four invariants <see cref="MediaPlayerBase"/> exists to hold — none of which had a test until
/// 2026-08-14, when a coverage run reported the whole type at 0 of 340 lines.</b>
///
/// <para>
/// That gap mattered more than the number: this is a PORTABLE state machine with the platform left
/// abstract, so it is none of the things <c>docs/REVIEW-GUIDE.md</c> §6 documents as untestable by
/// construction — no live browser, no STA pump, no real provider. It needs a ~40-line fake, which is what
/// this file is. Its own class doc says each invariant was learned separately by two shipping
/// implementations and that each is "invisible when wrong", so a refactor that broke one would ship
/// silently and present as a UI glitch nobody could trace back here.
/// </para>
/// <para>
/// Every test below asserts the ANSWER (the observable state a caller sees), never that a platform hook
/// was merely called — the lesson from the wildcard-claim test that encoded a bug as a pass.
/// </para>
/// </summary>
public class MediaPlayerBaseTests
{
    /// <summary>A platform that records what it was told and does exactly nothing else.</summary>
    private sealed class FakePlayer(Action<string>? log = null) : MediaPlayerBase(log is null ? null : AppCallback.Logger(log))
    {
        public readonly List<string> Calls = [];
        public TimeSpan Position;
        public TimeSpan? DurationValue = TimeSpan.FromSeconds(60);
        public TaskCompletionSource? SeekGate;

        protected override TimeSpan PositionCore => Position;
        protected override TimeSpan? DurationCore => DurationValue;
        protected override void OpenCore(MediaSource source, Uri uri) => Calls.Add($"open:{uri}");
        protected override void ApplyStartAt(TimeSpan position) => Calls.Add($"startAt:{position}");
        protected override void PlayCore(double rate) => Calls.Add($"play:{rate}");
        protected override void PauseCore() => Calls.Add("pause");
        protected override Task SeekCore(TimeSpan position)
        {
            Calls.Add($"seek:{position}");
            return SeekGate?.Task ?? Task.CompletedTask;
        }
        protected override void ApplyRateCore(double rate) => Calls.Add($"rate:{rate}");
        protected override void TeardownCore() => Calls.Add("teardown");

        // The platform callbacks are protected; a test drives them the way a shell would.
        public void Opened() => OnOpened();
        public void Ended() => OnEnded();
        public void Failed(string reason) => OnFailed(reason);
        public void PlatformState(MediaPlayerState state) => OnPlatformState(state);
    }

    private static MediaSource Source(string uri = "https://example.test/clip.mp4", TimeSpan startAt = default) =>
        new() { Uri = uri, StartAt = startAt };

    private static async Task<FakePlayer> OpenedPlayerAsync()
    {
        var player = new FakePlayer();
        var opening = player.OpenAsync(Source());
        player.Opened();
        await opening;
        return player;
    }

    // ── What OpenAsync accepts as a source URI ────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 A <c>file:</c> URL is what a .NET caller produces with <c>new Uri(path).AbsoluteUri</c>, and it
    /// used to be REFUSED as "not a file path or an absolute URL" while being both. Found by the first
    /// adopter (Yaorin, 0.11.0), one failed open each. The rooted-path branch already yields a
    /// <c>file:</c> URI, so everything downstream was handling one anyway.
    /// </summary>
    [Fact]
    public async Task A_file_URL_opens_and_arrives_as_the_same_uri_a_rooted_path_would()
    {
        var rooted = OperatingSystem.IsWindows() ? @"C:\media\clip.wma" : "/media/clip.wma";

        var viaPath = new FakePlayer();
        var openingPath = viaPath.OpenAsync(Source(rooted));
        viaPath.Opened();
        await openingPath;

        var viaUrl = new FakePlayer();
        var openingUrl = viaUrl.OpenAsync(Source(new Uri(rooted).AbsoluteUri));
        viaUrl.Opened();
        await openingUrl;

        // Both spellings reach OpenCore, and reach it as the SAME uri — otherwise a platform that
        // branches on IsFile/LocalPath would behave differently for one of them.
        Assert.Equal(viaPath.Calls, viaUrl.Calls);
    }

    [Fact]
    public async Task A_RELATIVE_uri_is_refused_and_the_message_says_which_thing_is_wrong()
    {
        var player = new FakePlayer();

        var error = await Assert.ThrowsAsync<MediaPlayerException>(() => player.OpenAsync(Source("clip.wma")));

        // The old message named a file path and an absolute URL — the two things a rejected `file:` URL
        // already was. Only a relative string can reach this now, so it says so, and names the fix.
        Assert.Contains("relative", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clip.wma", error.Message, StringComparison.Ordinal);
    }

    // ── Invariant 1 — a TERMINAL state is never overwritten by a platform transition ───────────────

    /// <summary>
    /// Every platform drives its session to "paused" right after it ends, so a mapping that trusts the
    /// platform erases <c>Ended</c> microseconds after raising it — a UI sees "finished" flicker to
    /// "paused at the end".
    /// </summary>
    [Fact]
    public async Task A_platform_pause_after_the_source_ENDED_does_not_erase_Ended()
    {
        var player = await OpenedPlayerAsync();
        player.Ended();
        Assert.Equal(MediaPlayerState.Ended, player.Status.State);

        player.PlatformState(MediaPlayerState.Paused);

        Assert.Equal(MediaPlayerState.Ended, player.Status.State);
    }

    /// <summary>The same guard, from the other terminal state — a failed open must not read as healthy.</summary>
    [Fact]
    public async Task A_platform_pause_after_a_FAILURE_does_not_erase_Failed()
    {
        var player = new FakePlayer();
        var opening = player.OpenAsync(Source());
        player.Failed("could not decode");
        await Assert.ThrowsAsync<MediaPlayerException>(() => opening);

        player.PlatformState(MediaPlayerState.Paused);

        Assert.Equal(MediaPlayerState.Failed, player.Status.State);
        Assert.Equal("could not decode", player.Status.Error);
    }

    // ── Invariant 2 — a rate set while PAUSED is remembered, not applied ──────────────────────────

    /// <summary>
    /// On AVFoundation rate and transport are the SAME control, so pushing a remembered 1.5× would
    /// silently start a paused player. The rate must be handed to the platform WITH the start instead.
    /// </summary>
    [Fact]
    public async Task A_rate_set_while_paused_is_remembered_and_applied_only_when_play_starts()
    {
        var player = await OpenedPlayerAsync();
        player.Calls.Clear();

        await player.SetRateAsync(1.5);

        // Remembered, and NOT pushed — pushing it is what would start playback.
        Assert.Equal(1.5, player.Status.Rate);
        Assert.DoesNotContain(player.Calls, c => c.StartsWith("rate:", StringComparison.Ordinal));
        Assert.Equal(MediaPlayerState.Paused, player.Status.State);

        await player.PlayAsync();

        // It starts AT the remembered rate rather than at 1.0 and visibly stepping up.
        Assert.Contains("play:1.5", player.Calls);
    }

    /// <summary>The other half: while PLAYING, a rate change is pushed straight through.</summary>
    [Fact]
    public async Task A_rate_set_while_PLAYING_is_pushed_to_the_platform()
    {
        var player = await OpenedPlayerAsync();
        await player.PlayAsync();
        player.Calls.Clear();

        await player.SetRateAsync(2.0);

        Assert.Contains("rate:2", player.Calls);
    }

    // ── Invariant 3 — a CANCELLED open leaves the player Empty, not half-loaded ───────────────────

    /// <summary>
    /// So a caller that retries does not inherit the previous attempt's source. Asserted on the state a
    /// caller can see, not on whether teardown happened to be called.
    /// </summary>
    [Fact]
    public async Task A_cancelled_open_leaves_the_player_EMPTY()
    {
        var player = new FakePlayer();
        using var cts = new CancellationTokenSource();

        var opening = player.OpenAsync(Source(), cts.Token);   // never signalled Opened
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(MediaPlayerState.Empty, player.Status.State);
        // Half-loaded would mean a position or duration survived; Empty reports neither.
        Assert.Equal(TimeSpan.Zero, player.Status.Position);
        Assert.Null(player.Status.Duration);
    }

    // ── Invariant 4 — an ABANDONED open completes exceptionally rather than hanging ───────────────

    /// <summary>
    /// 🔴 Re-opening while an open is in flight used to leave its caller awaiting forever — no exception,
    /// no log line. The type's doc names this as the same shape as the defect that made
    /// <c>MediaPlayer.OpenAsync</c> wait for a report nothing sent.
    /// </summary>
    [Fact]
    public async Task Re_opening_while_an_open_is_IN_FLIGHT_faults_the_first_caller()
    {
        var player = new FakePlayer();

        var first = player.OpenAsync(Source("https://example.test/one.mp4"));
        var second = player.OpenAsync(Source("https://example.test/two.mp4"));

        // ⚠ BOUNDED, and that is the whole point of this test's shape. The failure being pinned here is
        // "the caller waits FOREVER", so a bare `await` would detect the regression by WEDGING THE SUITE —
        // measured 2026-08-14: sabotaging the fault left the run reporting "5 passed" and exiting, a green
        // that meant nothing (the same shape `local` records from the D72 work). `WaitAsync` turns the
        // hang into a fast, named TimeoutException instead.
        await Assert.ThrowsAsync<MediaPlayerException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));

        player.Opened();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
    }

    /// <summary>Closing while an open is in flight settles it the same way.</summary>
    [Fact]
    public async Task Closing_while_an_open_is_IN_FLIGHT_faults_the_caller()
    {
        var player = new FakePlayer();
        var opening = player.OpenAsync(Source());

        await player.CloseAsync();

        // Bounded for the same reason as the re-open case above: the regression is a hang.
        await Assert.ThrowsAsync<MediaPlayerException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(MediaPlayerState.Empty, player.Status.State);
    }

    // ── The surrounding contract these four sit inside ────────────────────────────────────────────

    /// <summary>`StateChanged` is a TRANSITION, not a tick — a repeated platform state raises nothing.</summary>
    [Fact]
    public async Task A_platform_state_that_matches_what_we_believe_raises_nothing()
    {
        var player = await OpenedPlayerAsync();
        var raised = 0;
        player.StateChanged += _ => raised++;

        player.PlatformState(MediaPlayerState.Paused);   // already Paused

        Assert.Equal(0, raised);

        player.PlatformState(MediaPlayerState.Buffering);
        Assert.Equal(1, raised);
    }

    /// <summary>
    /// A source opened with <c>StartAt</c> is POSITIONED as part of opening rather than by a seek
    /// afterwards — the difference a caller sees is whether a resumed item starts at zero and jumps.
    /// </summary>
    [Fact]
    public async Task StartAt_is_applied_during_opening_not_as_a_later_seek()
    {
        var player = new FakePlayer();
        var opening = player.OpenAsync(Source(startAt: TimeSpan.FromSeconds(30)));
        player.Opened();
        await opening;

        Assert.Contains("startAt:00:00:30", player.Calls);
        Assert.DoesNotContain(player.Calls, c => c.StartsWith("seek:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Seeking out of <c>Ended</c> makes it resumable again — leaving it Ended makes a UI that seeks
    /// backwards from the end still show "finished".
    /// </summary>
    [Fact]
    public async Task Seeking_out_of_ENDED_makes_the_player_resumable()
    {
        var player = await OpenedPlayerAsync();
        player.Ended();

        await player.SeekAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
    }

    /// <summary>
    /// A failed SEEK is not a failed PLAYER: the source is still open at its old position and the caller
    /// can seek again. It is logged rather than thrown.
    /// </summary>
    [Fact]
    public async Task A_failing_seek_does_not_fail_the_player()
    {
        var logged = new List<string>();
        var player = new FakePlayer(logged.Add);
        var opening = player.OpenAsync(Source());
        player.Opened();
        await opening;

        var gate = new TaskCompletionSource();
        gate.SetException(new InvalidOperationException("the platform refused"));
        player.SeekGate = gate;

        await player.SeekAsync(TimeSpan.FromSeconds(5));   // must not throw

        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
        Assert.Contains(logged, l => l.Contains("SeekAsync failed", StringComparison.Ordinal));
        // ⚠ The platform's own text must not become the player's app-visible error.
        Assert.Null(player.Status.Error);
    }

    /// <summary>With no source open, the transport verbs refuse rather than guessing.</summary>
    [Fact]
    public async Task The_transport_verbs_refuse_when_no_source_is_open()
    {
        var player = new FakePlayer();

        await Assert.ThrowsAsync<MediaPlayerException>(() => player.PlayAsync());
        await Assert.ThrowsAsync<MediaPlayerException>(() => player.PauseAsync());
        await Assert.ThrowsAsync<MediaPlayerException>(() => player.SeekAsync(TimeSpan.Zero));
    }

    /// <summary>
    /// A released handle answers a position from whatever it last held, which would read as a position
    /// that survived <c>CloseAsync</c> — so the platform is not asked once the source is gone.
    /// </summary>
    [Fact]
    public async Task Position_and_duration_are_not_asked_of_the_platform_once_closed()
    {
        var player = await OpenedPlayerAsync();
        player.Position = TimeSpan.FromSeconds(42);
        Assert.Equal(TimeSpan.FromSeconds(42), player.Status.Position);

        await player.CloseAsync();

        Assert.Equal(TimeSpan.Zero, player.Status.Position);
        Assert.Null(player.Status.Duration);
    }

    /// <summary>A rate of zero or less is a caller bug, refused at the setter.</summary>
    [Fact]
    public void A_non_positive_rate_is_refused()
    {
        var player = new FakePlayer();
        // Throws SYNCHRONOUSLY rather than returning a faulted task: an out-of-range argument is a
        // caller bug, not a platform outcome, so it should surface without an await.
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = player.SetRateAsync(0); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = player.SetRateAsync(-1); });
    }

    /// <summary>A throwing StateChanged handler is caught — it runs inside a platform callback.</summary>
    [Fact]
    public async Task A_throwing_state_handler_does_not_escape_into_the_platform_callback()
    {
        var player = await OpenedPlayerAsync();
        player.StateChanged += _ => throw new InvalidOperationException("subscriber blew up");

        player.Ended();   // must not throw

        Assert.Equal(MediaPlayerState.Ended, player.Status.State);
    }
}
