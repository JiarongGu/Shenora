using System.Text.Json;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The back-gesture coordinator and its two routes.
/// <para>
/// 🔴 <b>What matters here is which way it fails.</b> Every uncertain case must fall through to the
/// platform, because a press the kit SWALLOWS is a back button that does nothing — and that is a broken
/// app an adopter cannot debug from the outside, whereas a press that reaches the platform is at worst
/// the behaviour they had before the kit was involved.
/// </para>
/// </summary>
public class BackNavigationTests
{
    /// <summary>Collects what the coordinator published, so a test can read the token it minted.</summary>
    private sealed class Recording
    {
        private readonly List<EventMessage> _seen = [];
        public IReadOnlyList<EventMessage> Seen => _seen;

        public EventBus Bus { get; }

        public Recording()
        {
            Bus = new EventBus();
            Bus.SubscribeToAll(message =>
            {
                lock (_seen) _seen.Add(message);
                return Task.CompletedTask;
            });
        }

        /// <summary>The token of the Nth press published, waiting briefly for the fire-and-forget emit.</summary>
        public async Task<string> TokenAsync(int index = 0)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                lock (_seen)
                {
                    if (_seen.Count > index)
                    {
                        var payload = Assert.IsType<BackNavigationEvent>(_seen[index].Payload);
                        return payload.Token;
                    }
                }
                await Task.Delay(10);
            }
            Assert.Fail($"no press #{index} was published");
            return string.Empty;
        }
    }

    private static BackNavigation Coordinator(Recording recording, int timeoutMs = 30_000) =>
        new(recording.Bus, new BackNavigationOptions { Timeout = TimeSpan.FromMilliseconds(timeoutMs) });

    [Fact]
    public async Task A_press_falls_through_when_NO_page_is_intercepting_and_publishes_nothing()
    {
        // 🔴 The fast path, and the reason interception is opt-in: an app that never asked for this must
        // not pay a round trip — or worse, a timeout — on every back press.
        var recording = new Recording();
        using var back = Coordinator(recording);

        Assert.False(await back.PressAsync());
        Assert.Empty(recording.Seen);
    }

    [Fact]
    public async Task A_press_the_page_HANDLES_does_not_reach_the_platform()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);
        back.SetIntercepting(true);

        var press = back.PressAsync();
        Assert.True(back.Resolve(await recording.TokenAsync(), handled: true));

        Assert.True(await press);
    }

    [Fact]
    public async Task A_press_the_page_DECLINES_falls_through()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);
        back.SetIntercepting(true);

        var press = back.PressAsync();
        back.Resolve(await recording.TokenAsync(), handled: false);

        Assert.False(await press);
    }

    [Fact]
    public async Task A_page_that_never_answers_lets_the_press_through_rather_than_swallowing_it()
    {
        // 🔴 The failure this timeout exists for: a crashed bundle, or a listener that threw while
        // registering, leaves a page that asked to intercept and then never answers. Without the
        // timeout the back button silently does nothing for the rest of the session.
        var recording = new Recording();
        using var back = Coordinator(recording, timeoutMs: 50);
        back.SetIntercepting(true);

        Assert.False(await back.PressAsync());
    }

    [Fact]
    public async Task An_answer_that_arrives_AFTER_the_timeout_is_refused_rather_than_applied_to_the_next_press()
    {
        // 🔴 This is what the token is for. Without one, a slow answer to press #1 would arrive while
        // press #2 was waiting and silently consume it — so the page would appear to handle a press it
        // never saw, and the two would drift one apart for ever.
        var recording = new Recording();
        using var back = Coordinator(recording, timeoutMs: 50);
        back.SetIntercepting(true);

        Assert.False(await back.PressAsync());
        var stale = await recording.TokenAsync();

        Assert.False(back.Resolve(stale, handled: true));
    }

    [Fact]
    public async Task Two_presses_get_DIFFERENT_tokens_and_are_answered_independently()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);
        back.SetIntercepting(true);

        var first = back.PressAsync();
        var firstToken = await recording.TokenAsync(0);
        var second = back.PressAsync();
        var secondToken = await recording.TokenAsync(1);

        Assert.NotEqual(firstToken, secondToken);

        // Answered out of order, which a user mashing back produces naturally.
        back.Resolve(secondToken, handled: false);
        back.Resolve(firstToken, handled: true);

        Assert.True(await first);
        Assert.False(await second);
    }

    [Fact]
    public async Task Releasing_interception_ANSWERS_the_press_already_waiting_instead_of_stranding_it()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);
        back.SetIntercepting(true);

        var press = back.PressAsync();
        await recording.TokenAsync();
        back.SetIntercepting(false);

        Assert.False(await press);
    }

    [Fact]
    public async Task Disposing_releases_a_waiting_press_to_the_platform()
    {
        // The page is gone; the platform default is the only honest answer left.
        var recording = new Recording();
        var back = Coordinator(recording);
        back.SetIntercepting(true);

        var press = back.PressAsync();
        await recording.TokenAsync();
        back.Dispose();

        Assert.False(await press);
    }

    // ── the routes ────────────────────────────────────────────────────────────────────────────────

    private static async Task<IpcResponse> DispatchAsync(BackNavigation back, string type, object? payload)
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new BackNavigationModule(back));
        return await dispatcher.DispatchAsync(new IpcRequest
        {
            Id = "r1",
            Module = BackNavigation.Module,
            Type = type,
            Payload = payload is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
        }, CancellationToken.None);
    }

    [Fact]
    public async Task The_INTERCEPT_route_is_what_turns_it_on()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);
        Assert.False(back.Intercepting);

        Assert.True((await DispatchAsync(back, BackNavigation.InterceptType, new { enabled = true })).Success);
        Assert.True(back.Intercepting);

        Assert.True((await DispatchAsync(back, BackNavigation.InterceptType, new { enabled = false })).Success);
        Assert.False(back.Intercepting);
    }

    [Fact]
    public async Task The_RESOLVE_route_answers_a_waiting_press()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);
        back.SetIntercepting(true);

        var press = back.PressAsync();
        var token = await recording.TokenAsync();
        var response = await DispatchAsync(back, BackNavigation.ResolveType, new { token, handled = true });

        Assert.True(response.Success);
        var json = JsonSerializer.SerializeToElement(response.Data, IpcJson.Options);
        Assert.True(json.GetProperty("accepted").GetBoolean());
        Assert.True(await press);
    }

    [Fact]
    public async Task The_RESOLVE_route_REPORTS_an_answer_nobody_was_waiting_for()
    {
        // ⚠ Not an error — the platform already took the press. But a page seeing this repeatedly is a
        // page whose back handling never runs, and this response is the only place that is visible
        // without a device attached.
        var recording = new Recording();
        using var back = Coordinator(recording);

        var response = await DispatchAsync(back, BackNavigation.ResolveType,
            new { token = "b999", handled = true });

        Assert.True(response.Success);
        var json = JsonSerializer.SerializeToElement(response.Data, IpcJson.Options);
        Assert.False(json.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_route_is_refused()
    {
        var recording = new Recording();
        using var back = Coordinator(recording);

        var response = await DispatchAsync(back, "GO_BACK_PLEASE", null);

        Assert.False(response.Success);
    }
}
