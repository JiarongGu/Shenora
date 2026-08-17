using System.Text.Json;

using Shenora.Core.Ipc;
using Shenora.Modules.Media;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The OTHER direction: the page driving the HOST's player over IPC.
///
/// <para>
/// 🔴 <b>The same verbs as <see cref="MediaPlayerEvents"/>, and the CHANNEL is the direction.</b> An EVENT
/// named <c>PLAYER_PLAY</c> is the host telling the page's element to play; a REQUEST named
/// <c>PLAYER_PLAY</c> is the page telling the host's native player to play. Reusing the constants is what
/// stops the two halves drifting into `PLAYER_PLAY` and `PLAY_PLAYER` — so these tests assert against
/// `MediaPlayerEvents`, never against a string literal.
/// </para>
///
/// <para>
/// ⚠ Before this, an app that wanted its React UI to drive <c>WindowsMediaPlayer</c> / <c>AndroidMediaPlayer</c> /
/// <c>IosMediaPlayer</c> wrote its own module to do it — the wiring D64 exists to delete.
/// </para>
/// </summary>
public class MediaPlayerDriveTests
{
    [Fact]
    public async Task Play_pause_and_close_reach_the_host_player()
    {
        var player = new FakePlayer();

        Assert.True((await DispatchAsync(player, MediaPlayerEvents.Play)).Success);
        Assert.True((await DispatchAsync(player, MediaPlayerEvents.Pause)).Success);
        Assert.True((await DispatchAsync(player, MediaPlayerEvents.Unload)).Success);

        Assert.Equal(["Play", "Pause", "Close"], player.Calls);
    }

    [Fact]
    public async Task Load_opens_the_uri_the_page_named()
    {
        var player = new FakePlayer();

        var response = await DispatchAsync(player, MediaPlayerEvents.Load, new { uri = "C:/media/song.m4a" });

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal("C:/media/song.m4a", player.Opened?.Uri);
    }

    /// <summary>
    /// ⚠ SECONDS on the wire, `TimeSpan` in the contract — the same conversion `PLAYER_REPORT` carries in
    /// the other direction, and the one every adopter wrote by hand and got wrong.
    /// </summary>
    [Fact]
    public async Task Seek_converts_the_pages_SECONDS_into_a_TimeSpan()
    {
        var player = new FakePlayer();

        await DispatchAsync(player, MediaPlayerEvents.Seek, new { position = 90.5 });

        Assert.Equal(TimeSpan.FromSeconds(90.5), player.Sought);
    }

    [Fact]
    public async Task Rate_is_applied_and_reported_back()
    {
        var player = new FakePlayer();

        var response = await DispatchAsync(player, MediaPlayerEvents.Rate, new { rate = 1.5 });

        Assert.Equal(1.5, player.Status.Rate);
        Assert.Equal(1.5, Field(response, "rate").GetDouble());
    }

    /// <summary>
    /// A command answers with the resulting status, so a page never has to follow one with a query — and
    /// the answer is in SECONDS, like everything else the page reads.
    /// </summary>
    [Fact]
    public async Task A_command_answers_with_the_status_in_seconds()
    {
        var player = new FakePlayer { Status = Status(MediaPlayerState.Playing, 12.5, 300) };

        var response = await DispatchAsync(player, MediaPlayerEvents.Play);

        Assert.Equal("Playing", Field(response, "state").GetString());
        Assert.Equal(12.5, Field(response, "position").GetDouble());
        Assert.Equal(300, Field(response, "duration").GetDouble());
    }

    [Fact]
    public async Task Status_can_be_asked_for_on_its_own()
    {
        var player = new FakePlayer { Status = Status(MediaPlayerState.Paused, 3, 60) };

        var response = await DispatchAsync(player, MediaPlayerModule.StatusType);

        Assert.True(response.Success, response.Error?.Code);
        Assert.Equal("Paused", Field(response, "state").GetString());
        Assert.Empty(player.Calls);                        // asking is not driving
    }

    /// <summary>
    /// 🔴 <b>THE ASYMMETRY THAT MATTERS.</b> A <c>PLAYER_REPORT</c> with no registered player is IGNORED —
    /// the page is describing its own element and nobody needed to hear it. A drive command with no player
    /// must FAIL, because the page is waiting for the thing to happen: answering "fine" while doing
    /// nothing leaves it waiting forever with no error to act on, which is the exact silent-hang failure
    /// this module was created to fix, in the other direction.
    /// </summary>
    [Fact]
    public async Task A_drive_command_with_NO_player_fails_loudly_while_a_report_stays_quiet()
    {
        var drive = await DispatchAsync(player: null, MediaPlayerEvents.Play);
        Assert.False(drive.Success);
        Assert.Equal("MEDIA_PLAYER_UNAVAILABLE", drive.Error?.Code);

        var report = await DispatchAsync(player: null, MediaPlayerModule.ReportType,
            new { state = "Playing", position = 1.0 });
        Assert.True(report.Success);
    }

    private static MediaPlayerStatus Status(MediaPlayerState state, double position, double duration) => new()
    {
        State = state,
        Position = TimeSpan.FromSeconds(position),
        Duration = TimeSpan.FromSeconds(duration),
    };

    private static JsonElement Field(IpcResponse response, string name) =>
        JsonSerializer.SerializeToElement(response.Data, IpcJson.Options).GetProperty(name);

    private static async Task<IpcResponse> DispatchAsync(IMediaPlayer? player, string type, object? payload = null)
    {
        var options = new MediaPlayerOptions();
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new MediaPlayerModule(player, options));
        return await dispatcher.DispatchAsync(new IpcRequest
        {
            Id = "r1",
            Module = options.Access.Module,
            Type = type,
            Payload = payload is null
                ? default
                : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
        }, CancellationToken.None);
    }

    /// <summary>Records what it was asked to do. Deliberately not a mock: the assertion is the ORDER.</summary>
    private sealed class FakePlayer : IMediaPlayer
    {
        public List<string> Calls { get; } = [];
        public MediaSource? Opened { get; private set; }
        public TimeSpan? Sought { get; private set; }
        public MediaPlayerStatus Status { get; set; } = new() { State = MediaPlayerState.Empty };

        private double _rate = 1.0;

        /// <remarks>
        /// ⚠ Setting it updates <see cref="Status"/> too, because that is what a real player does — the
        /// contract says <c>Status.Rate</c> reports what was ASKED FOR. A fake that stored the value
        /// somewhere the status could not see would make the round trip look broken when it is not.
        /// </remarks>
        public Task SetRateAsync(double rate, CancellationToken cancellationToken = default)
        {
            _rate = rate;
            Status = Status with { Rate = rate };
            return Task.CompletedTask;
        }

        public event Action<MediaPlayerStatus>? StateChanged;

        public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default)
        {
            Calls.Add("Open");
            Opened = source;
            StateChanged?.Invoke(Status);                 // a real transition, so the event is not dead code
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) => Record("Play");
        public Task PauseAsync(CancellationToken cancellationToken = default) => Record("Pause");
        public Task CloseAsync(CancellationToken cancellationToken = default) => Record("Close");

        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        {
            Sought = position;
            return Record("Seek");
        }

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }
}
