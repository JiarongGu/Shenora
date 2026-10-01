using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;
using Shenora.Core.Sessions;
using System.Globalization;
using System.Text.Json;
using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The co-browse input protocol's pure builders — the wire shapes are kept IDENTICAL to the
/// source for mechanical adoption, so these pin them (clamps, modifier bitmask, VK map,
/// invariant-culture formatting). The live screencast/dispatch loop is the sample-e2e's subject.
/// </summary>
public class StreamingSessionTests
{
    [Fact]
    public void Metrics_json_clamps_and_defaults_the_dpr()
    {
        using var doc = JsonDocument.Parse(StreamingSession.BuildMetricsOverrideJson(5000, 100, null));
        var root = doc.RootElement;

        Assert.Equal(1560, root.GetProperty("width").GetInt32());   // clamped to the source bounds
        Assert.Equal(240, root.GetProperty("height").GetInt32());
        Assert.Equal(1.5, root.GetProperty("deviceScaleFactor").GetDouble()); // the crisp default
        Assert.False(root.GetProperty("mobile").GetBoolean());
        Assert.Equal(1560, root.GetProperty("screenWidth").GetInt32()); // screen mirrors the viewport
    }

    [Fact]
    public void Metrics_json_is_invariant_culture()
    {
        // "1,50" on a comma-decimal locale is broken JSON — the source fixed this live.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = StreamingSession.BuildMetricsOverrideJson(800, 600, 1.5);

            // The invariant is that the number is written with a DOT and parses back to the value it
            // was given — not that it renders in one particular way. This used to assert
            // `Contains("\"deviceScaleFactor\":1.50")`, which pinned the exact digit padding of the
            // format string (P5.5 H7): switching "0.00" to "0.##" or to a plain double would have
            // failed a culture test for a reason that has nothing to do with culture.
            Assert.DoesNotContain("1,5", json, StringComparison.Ordinal); // the comma-decimal break
            using var doc = JsonDocument.Parse(json);                     // parseable regardless of locale
            Assert.Equal(1.5, doc.RootElement.GetProperty("deviceScaleFactor").GetDouble());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(SessionPointerAction.Down, false, "mousePressed", 1, "left")]
    [InlineData(SessionPointerAction.Up, false, "mouseReleased", 0, "left")]
    [InlineData(SessionPointerAction.Move, false, "mouseMoved", 0, "none")]   // a free move: no button, or Chromium reads it as held
    [InlineData(SessionPointerAction.Move, true, "mouseMoved", 1, "left")]    // a DRAG move — the held button carries through (else drags can't work)
    public void Mouse_json_maps_events_and_scales_fractions_to_css_px(SessionPointerAction action, bool buttonHeld, string cdpType, int buttons,
        string button)
    {
        using var doc = JsonDocument.Parse(StreamingSession.BuildMouseEventJson(action, 0.5, 0.25, 1280, 860, buttonHeld));
        var root = doc.RootElement;

        Assert.Equal(cdpType, root.GetProperty("type").GetString());
        Assert.Equal(buttons, root.GetProperty("buttons").GetInt32());
        Assert.Equal(640, root.GetProperty("x").GetDouble());  // 0.5 × 1280
        Assert.Equal(215, root.GetProperty("y").GetDouble());  // 0.25 × 860
        Assert.Equal(button, root.GetProperty("button").GetString());
    }

    [Fact]
    public void Wheel_json_carries_the_delta()
    {
        using var doc = JsonDocument.Parse(StreamingSession.BuildWheelEventJson(0.1, 0.9, -120, 1000, 750));
        var root = doc.RootElement;

        Assert.Equal("mouseWheel", root.GetProperty("type").GetString());
        Assert.Equal(100, root.GetProperty("x").GetDouble());
        Assert.Equal(675, root.GetProperty("y").GetDouble());
        Assert.Equal(0, root.GetProperty("deltaX").GetDouble());
        Assert.Equal(-120, root.GetProperty("deltaY").GetDouble());
    }

    [Fact]
    public void Key_jsons_are_a_down_up_pair_with_the_modifier_bitmask_and_vk()
    {
        var pair = StreamingSession.BuildKeyEventJsons("a", alt: false, ctrl: true, meta: false, shift: true);

        Assert.Equal(2, pair.Length);
        using var down = JsonDocument.Parse(pair[0]);
        using var up = JsonDocument.Parse(pair[1]);
        Assert.Equal("keyDown", down.RootElement.GetProperty("type").GetString());
        Assert.Equal("keyUp", up.RootElement.GetProperty("type").GetString());
        Assert.Equal(2 | 8, down.RootElement.GetProperty("modifiers").GetInt32()); // ctrl=2 | shift=8
        Assert.Equal('A', down.RootElement.GetProperty("windowsVirtualKeyCode").GetInt32()); // Ctrl+A works
        Assert.Equal("KeyA", down.RootElement.GetProperty("code").GetString());
        Assert.Equal("a", down.RootElement.GetProperty("key").GetString()); // DOM key stays as sent
    }

    /// <summary>On a macOS host a Command shortcut names its editing command on the keyDown alone; elsewhere, and for any
    /// other combination, no command is named.</summary>
    [Theory]
    [InlineData("a", false, true, "selectAll")]
    [InlineData("z", false, true, "undo")]
    [InlineData("Z", true, true, "redo")]
    [InlineData("a", false, false, null)]   // not a macOS host: Ctrl or Cmd edit there with no command
    [InlineData("c", false, true, null)]    // the clipboard's shortcuts are not named
    public void A_macOS_hosts_command_shortcut_names_its_editing_command(string key, bool shift, bool macHost, string? command)
    {
        var pair = StreamingSession.BuildKeyEventJsons(key, alt: false, ctrl: false, meta: true, shift: shift, macHost: macHost);
        using var down = JsonDocument.Parse(pair[0]);
        using var up = JsonDocument.Parse(pair[1]);
        if (command is null) Assert.False(down.RootElement.TryGetProperty("commands", out _));
        else Assert.Equal(command, down.RootElement.GetProperty("commands")[0].GetString());
        Assert.False(up.RootElement.TryGetProperty("commands", out _));
        Assert.Null(StreamingSession.MacEditingCommand("a", alt: false, ctrl: true, meta: true, shift: false));   // Ctrl+Cmd+A is not select-all
    }

    [Fact]
    public void An_unknown_key_omits_the_vk_and_code_so_cdp_infers_from_key()
    {
        using var doc = JsonDocument.Parse(
            StreamingSession.BuildKeyEventJsons("F13", alt: false, ctrl: false, meta: false, shift: false)[0]);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("windowsVirtualKeyCode", out _));
        Assert.False(root.TryGetProperty("code", out _));
        Assert.Equal(0, root.GetProperty("modifiers").GetInt32());
    }

    [Theory]
    [InlineData("Enter", 13, "Enter")]
    [InlineData("Backspace", 8, "Backspace")]
    [InlineData("ArrowLeft", 37, "ArrowLeft")]
    [InlineData(" ", 32, "Space")]
    [InlineData("z", 'Z', "KeyZ")]   // lowercase letters normalize to the uppercase VK
    [InlineData("Q", 'Q', "KeyQ")]
    [InlineData("7", '7', "Digit7")]
    public void The_vk_map_covers_navigation_editing_letters_and_digits(string key, int vk, string code)
    {
        Assert.Equal((vk, code), StreamingSession.KeyInfo(key));
    }

    [Fact]
    public async Task Start_validates_the_options_before_touching_the_ui()
    {
        using var anchor = new Form { ShowInTaskbar = false };
        StreamingSessionOptions Options(int quality = 72, int buffer = 2, int maxW = 2560) => new()
        {
            Host = new WebView2SessionHost(anchor),
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "unused"), KeepAliveInBackground = true },
            FrameQuality = quality,
            FrameBuffer = buffer,
            MaxFrameWidth = maxW,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StreamingSession.StartAsync(Options(quality: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StreamingSession.StartAsync(Options(buffer: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => StreamingSession.StartAsync(Options(maxW: 0)));
    }

    /// <summary>A renderer that dies while the session starts ends the start, which owns that teardown: setup awaited
    /// DevTools calls a dead renderer never answers, so StartAsync waited for ever.</summary>
    [Fact]
    public async Task A_renderer_that_dies_during_start_ends_the_start_and_closes_the_browser()
    {
        using var ui = new TestUiThread();
        var host = new DyingHost(ui);

        var start = StreamingSession.StartAsync(new StreamingSessionOptions
        {
            Host = host,
            Browser = new SessionBrowserOptions { ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "dying") },
        });

        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(start, finished);
        await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.True(host.Browser!.IsClosed, "the start left the dead session's browser open");
    }

    /// <summary>Hands out a browser whose DevTools never answer, and reports its renderer gone a moment later.</summary>
    private sealed class DyingHost(TestUiThread ui) : ISessionHost
    {
        public FakeSessionBrowser? Browser;
        public IUiDispatcher Ui => ui;
        public ISessionBrowserContext CreateContext() => throw new NotSupportedException();
        public Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ISessionBrowser> CreateAsync(SessionBrowserDefinition definition, CancellationToken cancellationToken)
        {
            Browser = new FakeSessionBrowser { DevTools = _ => new TaskCompletionSource<string>().Task };
            _ = Task.Delay(200).ContinueWith(_ => ui.Post(() =>
                definition.OnGone?.Invoke(new SessionProcessReport("RenderProcessExited", "crashed", 1, Terminal: true))));
            return Task.FromResult<ISessionBrowser>(Browser);
        }
    }

    [Fact]
    public async Task A_cancelled_start_completes_even_though_nothing_pumps_the_anchor()
    {
        // 🔴 THE HANG. Every cancellation check lives INSIDE the BeginInvoke body, so all of them are
        // unreachable when nothing pumps — and `BeginInvoke` succeeds whenever the handle exists,
        // including after `Application.Run` has returned. `StartAsync(options, ct)` then never returned
        // even with `ct` already cancelled.
        //
        // 🔴 CANCELLED **AFTER** THE CALL, and that is the whole point of the ordering. Registering on an
        // ALREADY-cancelled token fires the callback synchronously, so a pre-cancelled token cannot tell
        // a live registration apart from one that is disposed the instant the method returns — which is
        // what `using var` does on a NON-async method. The first version of this fix had exactly that
        // shape, and a pre-cancelled test passed against it. Sabotage-verified both ways at this
        // ordering: it fails with the registration removed AND with the method made non-async.
        using var anchor = new Form { ShowInTaskbar = false };
        _ = anchor.Handle;                     // BeginInvoke needs a created handle
        using var cts = new CancellationTokenSource();

        var start = StreamingSession.StartAsync(new StreamingSessionOptions
        {
            Host = new WebView2SessionHost(anchor),
            Browser = new SessionBrowserOptions
            {
                ProfileDirectory = Path.Combine(AppContext.BaseDirectory, "session-tests", "cancelled"),
                KeepAliveInBackground = true,
            },
        }, cts.Token);

        Assert.False(start.IsCompleted, "the post cannot have run — nothing is pumping this anchor");
        await cts.CancelAsync();

        // Nothing calls Application.DoEvents here ON PURPOSE — the posted body must never run. A short
        // bound rather than an unbounded await, so a regression FAILS instead of wedging the suite.
        var finished = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(start, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }
}
