using System.Text.Json;
using Shenora;
using Shenora.Ipc;
using Shenora.Media;

namespace Shenora.Tests.Ipc;

/// <summary>
/// <see cref="MediaPlayerFacade"/> — the joint between the two halves the kit already shipped.
/// <para>
/// 🔴 <b>Every test here would have passed vacuously before the facade existed, because the failure was
/// SILENCE.</b> The page posted <c>PLAYER_REPORT</c>, nothing answered, and
/// <see cref="IMediaPlayer.OpenAsync"/> waited forever — no exception, no log, an element visibly
/// playing. So the assertion that matters is not "the route returns OK", it is
/// <see cref="A_page_report_COMPLETES_an_open_that_is_waiting_for_it"/>: the await has to finish.
/// </para>
/// </summary>
public class MediaPlayerFacadeTests
{
    /// <summary>
    /// 🔴 <b>THE ONE THAT MATTERS.</b> `OpenAsync` completes on the first non-`Opening` report and on
    /// nothing else, so this is the whole loop: host emits LOAD → page reports → the await returns.
    /// ⚠ If the facade stops being registered, or the module names drift apart, this hangs rather than
    /// failing — which is why it is written with a timeout instead of a bare await.
    /// </summary>
    [Fact]
    public async Task A_page_report_COMPLETES_an_open_that_is_waiting_for_it()
    {
        var bus = new RecordingBus();
        var options = new MediaPlayerOptions();
        using var player = new MediaPlayer(bus, options);

        var open = player.OpenAsync(new MediaSource { Uri = "C:/media/song.m4a" });
        await bus.EmittedAsync(MediaPlayerEvents.Load);

        var response = await DispatchAsync(player, options,
            new { state = "Paused", position = 12.5, duration = 300.0 });

        Assert.True(response.Success, response.Error?.Code);
        await open.WaitAsync(TimeSpan.FromSeconds(5));   // the await that used to never return

        Assert.Equal(MediaPlayerState.Paused, player.Status.State);
    }

    /// <summary>
    /// ⚠ The page speaks SECONDS as plain numbers, because that is what a media element exposes; the host
    /// speaks <see cref="TimeSpan"/>. Deserializing one into the other does not work, so this conversion
    /// is the mapping every adopter had to write — and the reason the kit shipping it deletes real work.
    /// </summary>
    [Fact]
    public async Task Seconds_on_the_wire_become_a_TimeSpan_on_the_host()
    {
        var options = new MediaPlayerOptions();
        using var player = new MediaPlayer(new RecordingBus(), options);

        await DispatchAsync(player, options, new { state = "Playing", position = 90.5, duration = 245.25 });

        Assert.Equal(TimeSpan.FromSeconds(90.5), player.Status.Position);
        Assert.Equal(TimeSpan.FromSeconds(245.25), player.Status.Duration);
    }

    /// <summary>
    /// A live stream has no duration, and the wire carries <c>null</c> for it.
    /// <para>
    /// ⚠ <b>An element reports <c>Infinity</c> there, and this test originally sent that — which was
    /// wrong twice over and worth recording.</b> `useMediaPlayer` already filters it page-side
    /// (<c>Number.isFinite(element.duration) ? … : null</c>), AND `System.Text.Json` cannot encode a
    /// non-finite double at all, so the value can never reach this route. The facade's finite-check stays
    /// as defence for a page that is not ours; the TEST has to use the value the wire really carries, or
    /// it pins a path nothing travels.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_live_stream_reports_a_null_duration_rather_than_a_zero_one()
    {
        var options = new MediaPlayerOptions();
        using var player = new MediaPlayer(new RecordingBus(), options);

        var response = await DispatchAsync(player, options,
            new { state = "Playing", position = 3.0, duration = (double?)null });

        Assert.True(response.Success, response.Error?.Code);
        Assert.Null(player.Status.Duration);
        Assert.Equal(TimeSpan.FromSeconds(3), player.Status.Position);
    }

    /// <summary>
    /// ⚠ An unknown state becomes <see cref="MediaPlayerState.Failed"/>, never an exception. The page is a
    /// separate codebase on its own release cadence, so a state this host does not know means the halves
    /// disagree — and a player waiting in `OpenAsync` must be told SOMETHING, or the disagreement presents
    /// as the hang this facade exists to remove.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_state_FAILS_the_open_rather_than_hanging_it()
    {
        var bus = new RecordingBus();
        var options = new MediaPlayerOptions();
        using var player = new MediaPlayer(bus, options);

        var open = player.OpenAsync(new MediaSource { Uri = "C:/media/song.m4a" });
        await bus.EmittedAsync(MediaPlayerEvents.Load);

        await DispatchAsync(player, options, new { state = "SomethingFromANewerPage", position = 0.0 });

        await Assert.ThrowsAsync<MediaPlayerException>(() => open.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>The module name comes from the options both halves read, so they cannot drift apart.</summary>
    [Fact]
    public void The_facade_answers_on_the_players_own_module()
    {
        var options = new MediaPlayerOptions();
        using var player = new MediaPlayer(new RecordingBus(), options);

        Assert.Equal("SHENORA.MEDIA", options.Module);
        Assert.Equal(options.Module, new MediaPlayerFacade(player, options).ModuleName);
    }

    private static async Task<IpcResponse> DispatchAsync(IMediaPlayer player, MediaPlayerOptions options, object payload)
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new MediaPlayerFacade(player, options));
        return await dispatcher.DispatchAsync(new IpcRequest
        {
            Id = "r1",
            Module = options.Module,
            Type = MediaPlayerFacade.ReportType,
            Payload = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
        }, CancellationToken.None);
    }

    /// <summary>
    /// A bus that lets a test wait for the host's LOAD instead of racing it. Deliberately a local copy
    /// rather than a shared helper: <c>MediaPlayerTests</c> has its own, and the two want different things
    /// from it — that one records every send for assertions, this one only needs a gate.
    /// </summary>
    private sealed class RecordingBus : IEventBus
    {
        private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EmittedAsync(string type) => type == MediaPlayerEvents.Load ? _loaded.Task : Task.CompletedTask;

        public void Emit(string module, string type, object? payload = null, string? scope = null)
        {
            if (type == MediaPlayerEvents.Load) _loaded.TrySetResult();
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
}
