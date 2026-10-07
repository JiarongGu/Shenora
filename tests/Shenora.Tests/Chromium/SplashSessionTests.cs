using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The splash's lifecycle against a fake surface and a fake clock: when it shows, what lifts it, what never does, and
/// that a failing app callback or a missing display never reaches the app.
/// </summary>
public class SplashSessionTests
{
    private static readonly ChromiumWindowGeometry.Plan Where = new(400, 300, 0, 0, false);
    private static readonly SplashOverlayLayout Framed = new(false, 32, new SplashTitleBarOptions(), null, null);
    private static readonly Rectangle[] Desk = [new(0, 0, 1920, 1040)];
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSplashSurface _surface = new();
    private readonly FakeSplashSurface _card = new();

    // The card first, then the overlay: the order the session asks for them.
    private SplashSession CardSession(ChromiumSplashOptions options)
    {
        var queue = new Queue<FakeSplashSurface>([_card, _surface]);
        return new(options, title: "App", Color.Black, new ServiceCollection().BuildServiceProvider(), bus: null,
            () => queue.TryDequeue(out var s) ? s : null, _time, log: null, systemDark: null);
    }

    private SplashSession Session(ChromiumSplashOptions options, FakeSplashSurface? surface = null, Color? windowBackground = null, bool? dark = null,
        bool noDisplay = false) =>
        new(options, title: "App", windowBackground ?? Color.Black, new ServiceCollection().BuildServiceProvider(), bus: null,
            noDisplay ? () => null : () => surface ?? _surface, _time, log: null, systemDark: dark);

    private static void Open(SplashSession session)
    {
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
    }

    [Fact]
    public void The_window_drawing_uncovers_its_frame_and_after_the_lift_nothing_is_asked()
    {
        using var session = Session(new ChromiumSplashOptions { FadeOut = TimeSpan.Zero });
        Open(session);
        session.WindowShown();

        session.WindowPainted();
        Assert.Equal(["over", "reveal", "uncover"], _surface.Calls);

        session.Close();
        session.WindowPainted();
        Assert.Equal(1, _surface.Calls.Count(c => c == "uncover"));
    }

    [Fact]
    public void The_card_on_screen_is_announced_once()
    {
        using var session = CardSession(new ChromiumSplashOptions { Card = new SplashCardOptions() });
        var shown = 0;
        session.CardShown += () => shown++;

        session.Start(new(1000, 700, 100, 50, false), Desk);
        session.WindowOpened(7, Framed);
        session.WindowShown();

        Assert.Equal(1, shown);
    }

    [Fact]
    public void No_card_no_announcement()
    {
        using var session = Session(new ChromiumSplashOptions());
        var shown = 0;
        session.CardShown += () => shown++;
        Open(session);
        session.WindowShown();
        Assert.Equal(0, shown);
    }

    [Fact]
    public void A_card_shows_at_start_and_goes_once_the_window_is_shown()
    {
        using var session = CardSession(new ChromiumSplashOptions { Card = new SplashCardOptions() });
        session.Start(new(1000, 700, 100, 50, false), Desk);
        Assert.Equal(["card"], _card.Calls);
        Assert.Equal(new Rectangle(360, 250, 480, 300), _card.Card);
        Assert.True(session.IsShowing);

        session.WindowOpened(7, Framed);
        Assert.False(_card.Disposed);              // still up while CEF shows the window
        Assert.Equal(7, _surface.AttachedTo);
        session.WindowShown();
        Assert.True(_card.Disposed);
        Assert.False(_surface.Disposed);
        Assert.True(session.IsShowing);
    }

    [Fact]
    public void The_window_s_splash_is_revealed_once_the_window_is_on_screen_before_the_card_goes()
    {
        var log = new List<string>();
        _card.Log = log;
        _surface.Log = log;
        using var session = CardSession(new ChromiumSplashOptions { Card = new SplashCardOptions() });
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
        Assert.DoesNotContain("over:reveal", log);   // not floating alone while the window is still to show
        session.WindowShown();
        Assert.Equal(["card:card", "over:over", "over:reveal", "card:dispose"], log);
    }

    [Fact]
    public void Without_a_card_the_window_s_splash_is_revealed_when_the_window_shows()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
        session.WindowShown();
        Assert.Equal(["over", "reveal"], _surface.Calls);
    }

    [Fact]
    public void The_render_function_sees_Card_then_Window()
    {
        var seen = new List<SplashSurface>();
        using var session = CardSession(new ChromiumSplashOptions
        {
            Card = new SplashCardOptions(),
            Component = context => () => { seen.Add(context.Surface); return new SplashStack(); },
        });
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
        Assert.Equal([SplashSurface.Card, SplashSurface.Window], seen.Distinct());
        Assert.Equal(SplashSurface.Window, seen[^1]);
    }

    [Fact]
    public void The_card_keeps_drawing_Card_while_the_window_s_splash_draws_Window()
    {
        // Between the window opening and its being on screen both are up: a card frame then (its animation, a state
        // change) must still be the card's.
        using var session = CardSession(new ChromiumSplashOptions
        {
            Card = new SplashCardOptions(),
            Component = context => () => new SplashText(context.Surface.ToString()),
        });
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
        _card.Invalidate();
        Assert.Equal("Card", _card.LastText);
        Assert.Equal("Window", _surface.LastText);
    }

    [Fact]
    public void Aborting_during_the_card_disposes_it_and_shows_no_overlay()
    {
        using var session = CardSession(new ChromiumSplashOptions { Card = new SplashCardOptions() });
        session.Start(Where, Desk);
        session.Abort();
        Assert.True(_card.Disposed);
        session.WindowOpened(1, Framed);
        Assert.Empty(_surface.Calls);
    }

    [Fact]
    public void The_lift_takes_the_card_too_when_the_window_never_said_it_was_shown()
    {
        using var session = CardSession(new ChromiumSplashOptions { Card = new SplashCardOptions() });
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
        session.PageReady();
        Assert.True(_surface.Faded);
        Assert.True(_card.Disposed);
        Assert.False(_card.Faded);                 // the card goes at once; only the window's splash fades
    }

    [Fact]
    public void HasLifted_is_false_until_the_lift_and_true_at_once_after_a_failed_setup()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where, Desk);
        Assert.False(session.HasLifted);
        session.WindowOpened(1, Framed);
        session.PageReady();
        Assert.True(session.HasLifted);

        using var failed = Session(new ChromiumSplashOptions { Component = _ => throw new InvalidOperationException("app bug") }, new FakeSplashSurface());
        failed.Start(Where, Desk);
        Assert.True(failed.HasLifted);
    }

    [Fact]
    public void Lifted_is_raised_once()
    {
        var raised = 0;
        using var session = Session(new ChromiumSplashOptions());
        session.Lifted += () => raised++;
        Open(session);
        session.PageReady();
        session.Close();
        session.Abort();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Owner_moves_reach_the_window_s_splash_only()
    {
        using var session = CardSession(new ChromiumSplashOptions { Card = new SplashCardOptions() });
        session.Start(Where, Desk);
        session.OwnerMoved();                       // before the window: nothing to follow
        session.WindowOpened(1, Framed);
        for (var i = 0; i < 60; i++) session.OwnerMoved();
        Assert.Equal(0, _card.Follows);
        Assert.Equal(60, _surface.Follows);        // each one forwarded; the surface coalesces its own redraws
    }

    private static SplashComponent Text(Func<SplashContext, Func<string>> setup) => context =>
    {
        var text = setup(context);
        return () => new SplashText(text());
    };

    [Fact]
    public void With_no_card_it_shows_over_the_window_when_it_opens_and_lifts_at_the_handshake()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where, Desk);
        Assert.False(_surface.Shown);
        Assert.False(session.IsShowing);

        session.WindowOpened(42, Framed);
        Assert.Equal(["over"], _surface.Calls);
        Assert.Equal(42, _surface.AttachedTo);
        Assert.Equal(Framed, _surface.Layout);
        Assert.True(session.IsShowing);
        Assert.False(_surface.Faded);

        session.PageReady();
        Assert.True(_surface.Faded);
        Assert.Equal(TimeSpan.FromMilliseconds(150), _surface.FadeDuration);
        Assert.True(_surface.Disposed);
        Assert.False(session.IsShowing);
    }

    [Fact]
    public void A_held_splash_ignores_the_handshake_until_the_page_releases_it()
    {
        using var session = Session(new ChromiumSplashOptions { HoldUntilClosed = true });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.PageReady();
        Assert.False(_surface.Faded);
        session.Release();
        Assert.True(_surface.Faded);
    }

    [Fact]
    public async Task Boot_work_gates_the_lift_and_its_token_outlives_the_lift()
    {
        var gate = new TaskCompletionSource();
        var seen = new TaskCompletionSource<CancellationToken>();
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                context.OnShown(async ct => { seen.SetResult(ct); await gate.Task; });
                return () => new SplashStack();
            },
        });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.PageReady();
        var token = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(_surface.Faded);

        gate.SetResult();
        await _surface.FadedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(token.IsCancellationRequested);

        session.AppStopping();
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task All_boot_work_runs_at_once_and_the_last_to_finish_lifts_it()
    {
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var running = 0;
        var both = new TaskCompletionSource();
        Task Work(TaskCompletionSource done)
        {
            if (Interlocked.Increment(ref running) == 2) both.SetResult();
            return done.Task;
        }
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                context.OnShown(_ => Work(first));
                context.OnShown(_ => Work(second));
                return () => new SplashStack();
            },
        });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.PageReady();
        await both.Task.WaitAsync(TimeSpan.FromSeconds(5));

        first.SetResult();
        await Task.Delay(50);
        Assert.False(_surface.Faded);
        second.SetResult();
        await _surface.FadedTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Boot_work_that_throws_is_logged_and_released()
    {
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                context.OnShown(_ => throw new InvalidOperationException("app bug"));
                return () => new SplashStack();
            },
        });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.PageReady();
        await _surface.FadedTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_timeout_stands_in_for_the_page_and_counts_from_the_window()
    {
        using var session = Session(new ChromiumSplashOptions { HoldUntilClosed = true, Timeout = TimeSpan.FromSeconds(15) });
        session.Start(Where, []);
        _time.Advance(TimeSpan.FromSeconds(20));   // no window yet: no clock running
        Assert.False(_surface.Faded);

        session.WindowOpened(1, Framed);
        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.False(_surface.Faded);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_surface.Faded);
    }

    [Fact]
    public async Task The_timeout_never_lifts_it_over_unfinished_boot_work()
    {
        var never = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                context.OnShown(_ => { started.SetResult(); return never.Task; });
                return () => new SplashStack();
            },
        });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(_surface.Faded);
        Assert.True(session.IsShowing);
    }

    private sealed class ThrowingTimers : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new ArgumentOutOfRangeException(nameof(dueTime));
    }

    [Fact]
    public void A_timer_that_cannot_be_made_still_leaves_the_splash_attached()
    {
        var surface = new FakeSplashSurface();
        var opened = false;
        using var session = new SplashSession(new ChromiumSplashOptions { Component = c => { c.OnWindowOpened(() => opened = true); return () => new SplashStack(); } },
            "App", Color.Black, new ServiceCollection().BuildServiceProvider(), null, () => surface, new ThrowingTimers(), null, null);
        session.Start(Where, []);
        session.WindowOpened(7, Framed);
        Assert.Equal(7, surface.AttachedTo);   // attached before the timer, which CEF's show does not wait for
        Assert.True(opened);
    }

    [Fact]
    public void A_timeout_longer_than_a_timer_can_hold_is_taken_as_none()
    {
        var surface = new FakeSplashSurface();
        using var session = new SplashSession(new ChromiumSplashOptions { HoldUntilClosed = true, Timeout = TimeSpan.MaxValue }, "App", Color.Black,
            new ServiceCollection().BuildServiceProvider(), null, () => surface, TimeProvider.System, null, null);
        session.Start(Where, []);
        session.WindowOpened(7, Framed);   // TimeProvider.System refuses a due time past ~49 days
        Assert.Equal(7, surface.AttachedTo);
        Assert.True(session.IsShowing);
    }

    [Fact]
    public void Closed_during_its_own_setup_it_makes_no_window_at_all()
    {
        var made = 0;
        using var session = new SplashSession(new ChromiumSplashOptions { Component = c => { c.Close(); return () => new SplashStack(); } }, "App",
            Color.Black, new ServiceCollection().BuildServiceProvider(), null, () => { made++; return _surface; }, _time, null, null);
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        Assert.Equal(0, made);
        Assert.False(_surface.Shown);
    }

    [Fact]
    public async Task A_timeout_that_passes_while_boot_work_runs_says_so()
    {
        var lines = new List<string>();
        var log = AppCallback.Logger(line => { lock (lines) lines.Add(line); });
        var started = new TaskCompletionSource();
        var never = new TaskCompletionSource();
        using var session = new SplashSession(new ChromiumSplashOptions
            {
                Component = c => { c.OnShown(_ => { started.SetResult(); return never.Task; }); return () => new SplashStack(); },
            },
            "App", Color.Black, new ServiceCollection().BuildServiceProvider(), null, () => _surface, _time, log, null);
        session.Start(Where, []);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.WindowOpened(1, Framed);
        _time.Advance(TimeSpan.FromSeconds(16));
        lock (lines) Assert.Contains(lines, l => l.Contains("still waits on its OnShown work"));
        Assert.True(session.IsShowing);
    }

    [Fact]
    public void Close_lifts_it_at_once_from_the_component_or_from_the_app()
    {
        SplashContext? context = null;
        using (var session = Session(new ChromiumSplashOptions { Component = c => { context = c; return () => new SplashStack(); } }))
        {
            session.Start(Where, []);
            session.WindowOpened(1, Framed);
            context!.Close();
            Assert.True(_surface.Faded);
        }

        var other = new FakeSplashSurface();
        using var second = Session(new ChromiumSplashOptions(), other);
        second.Start(Where, []);
        second.WindowOpened(1, Framed);
        second.Close();
        Assert.True(other.Faded);
    }

    [Fact]
    public void Abort_destroys_it_without_a_fade()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.Abort();
        Assert.False(_surface.Faded);
        Assert.True(_surface.Disposed);
    }

    [Fact]
    public void A_zero_fade_is_a_hard_cut()
    {
        using var session = Session(new ChromiumSplashOptions { FadeOut = TimeSpan.Zero });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.Close();
        Assert.False(_surface.Faded);
        Assert.True(_surface.Disposed);
    }

    [Fact]
    public void State_set_during_setup_lands_in_the_first_frame()
    {
        using var session = Session(new ChromiumSplashOptions
        {
            Component = Text(context =>
            {
                var state = context.State("a");
                state.Value = "b";
                return () => state.Value;
            }),
        });
        Open(session);
        Assert.Equal("b", _surface.LastText);
    }

    [Fact]
    public void A_state_change_presents_a_new_frame()
    {
        SplashState<string>? state = null;
        using var session = Session(new ChromiumSplashOptions { Component = Text(context => { state = context.State("a"); return () => state.Value; }) });
        Open(session);
        state!.Value = "b";
        Assert.Equal("b", _surface.LastText);
        Assert.Equal(2, _surface.Frames);
    }

    [Fact]
    public void State_set_after_the_lift_draws_nothing()
    {
        SplashState<string>? state = null;
        using var session = Session(new ChromiumSplashOptions { Component = Text(context => { state = context.State("a"); return () => state.Value; }) });
        Open(session);
        session.Close();
        var frames = _surface.Frames;
        state!.Value = "b";
        Assert.Equal(frames, _surface.Frames);
    }

    [Fact]
    public void A_render_that_throws_keeps_the_last_good_frame()
    {
        SplashState<bool>? broken = null;
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                broken = context.State(false);
                return () => broken.Value ? throw new InvalidOperationException("app bug") : new SplashText("ok");
            },
        });
        Open(session);
        broken!.Value = true;
        Assert.Equal(2, _surface.Frames);
        Assert.Equal("ok", _surface.LastText);
    }

    [Fact]
    public async Task A_setup_that_throws_shows_nothing_and_still_runs_the_boot_work_it_registered()
    {
        var ran = new TaskCompletionSource();
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                context.OnShown(_ => { ran.SetResult(); return Task.CompletedTask; });
                throw new InvalidOperationException("app bug");
            },
        });
        Open(session);
        Assert.False(_surface.Shown);
        Assert.False(session.IsShowing);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_a_display_the_boot_work_still_runs()
    {
        var ran = new TaskCompletionSource();
        using var session = Session(new ChromiumSplashOptions
        {
            Component = context =>
            {
                context.OnShown(_ => { ran.SetResult(); return Task.CompletedTask; });
                return () => new SplashStack();
            },
        }, noDisplay: true);
        Open(session);
        Assert.False(session.IsShowing);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_surface_that_fails_to_show_costs_the_splash_and_not_the_app()
    {
        var failing = new FakeSplashSurface { FailShow = true };
        using var session = Session(new ChromiumSplashOptions(), failing);
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        Assert.False(session.IsShowing);
        Assert.True(failing.Disposed);
        session.PageReady();
    }

    [Fact]
    public void A_card_that_fails_to_show_still_lets_the_window_s_splash_show()
    {
        var queue = new Queue<FakeSplashSurface>([new FakeSplashSurface { FailShow = true }, _surface]);
        using var session = new SplashSession(new ChromiumSplashOptions { Card = new SplashCardOptions() }, "App", Color.Black,
            new ServiceCollection().BuildServiceProvider(), null, () => queue.TryDequeue(out var s) ? s : null, _time, null, null);
        session.Start(Where, Desk);
        session.WindowOpened(1, Framed);
        Assert.Equal(["over"], _surface.Calls);
        Assert.True(session.IsShowing);
    }

    [Fact]
    public void The_window_events_reach_the_component()
    {
        var events = new List<string>();
        using var session = Session(new ChromiumSplashOptions
        {
            HoldUntilClosed = true,
            Component = context =>
            {
                context.OnWindowOpened(() => events.Add("window"));
                context.OnPageReady(() => events.Add("page"));
                return () => new SplashStack();
            },
        });
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.PageReady();
        Assert.Equal(["window", "page"], events);
    }

    [Fact]
    public void The_owner_moving_moves_the_splash()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where, []);
        session.WindowOpened(1, Framed);
        session.OwnerMoved();
        Assert.Equal(1, _surface.Follows);
    }

    [Fact]
    public void The_background_is_the_splash_s_then_the_window_s_then_the_system_theme_s()
    {
        Color First(FakeSplashSurface s) => ((SplashFill)s.LastFrame!.Ops[0]).Color;

        var own = new FakeSplashSurface();
        using (var s = Session(new ChromiumSplashOptions { Background = Color.Red }, own)) Open(s);
        Assert.Equal(Color.Red, First(own));

        var window = new FakeSplashSurface();
        using (var s = Session(new ChromiumSplashOptions(), window, windowBackground: Color.Blue)) Open(s);
        Assert.Equal(Color.Blue, First(window));

        var light = new FakeSplashSurface();
        using (var s = new SplashSession(new ChromiumSplashOptions(), "App", null, new ServiceCollection().BuildServiceProvider(), null, () => light, _time, null, systemDark: false))
            Open(s);
        Assert.Equal(Color.FromArgb(0xF3, 0xF3, 0xF3), First(light));

        var unknown = new FakeSplashSurface();
        using (var s = new SplashSession(new ChromiumSplashOptions(), "App", null, new ServiceCollection().BuildServiceProvider(), null, () => unknown, _time, null, systemDark: null))
            Open(s);
        Assert.Equal(Color.FromArgb(0x1F, 0x1F, 0x1F), First(unknown));
    }

    [Fact]
    public void The_app_s_colour_scheme_decides_light_or_dark_before_the_system_s()
    {
        Color First(FakeSplashSurface s) => ((SplashFill)s.LastFrame!.Ops[0]).Color;
        bool? seenDark = null, seenSystemDark = null;
        SplashComponent component = c =>
        {
            seenDark = c.Dark;
            seenSystemDark = c.SystemDark;
            return () => new SplashStack();
        };

        // A dark system, an app held light: the splash is light, and still says what the system is.
        var light = new FakeSplashSurface();
        using (var s = new SplashSession(new ChromiumSplashOptions { Component = component }, "App", null,
            new ServiceCollection().BuildServiceProvider(), null, () => light, _time, null, systemDark: true, ColorScheme.Light))
            Open(s);
        Assert.Equal(Color.FromArgb(0xF3, 0xF3, 0xF3), First(light));
        Assert.Equal(false, seenDark);
        Assert.Equal(true, seenSystemDark);

        // Following the system, it is the system's.
        var system = new FakeSplashSurface();
        using (var s = new SplashSession(new ChromiumSplashOptions { Component = component }, "App", null,
            new ServiceCollection().BuildServiceProvider(), null, () => system, _time, null, systemDark: false, ColorScheme.System))
            Open(s);
        Assert.Equal(Color.FromArgb(0xF3, 0xF3, 0xF3), First(system));
        Assert.Equal(false, seenDark);
    }

    [Fact]
    public void Without_a_component_it_shows_the_preset_with_the_window_title()
    {
        using var session = Session(new ChromiumSplashOptions());
        Open(session);
        Assert.Equal("App", _surface.LastText);
    }

    [Fact]
    public void An_indeterminate_bar_moves_with_the_clock()
    {
        using var session = Session(new ChromiumSplashOptions { Component = _ => () => new SplashProgress() });
        Open(session);
        var before = _surface.LastFrame!.Ops.OfType<SplashFill>().Last().Bounds.X;
        Assert.True(_surface.LastFrame.Animated);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        _surface.Invalidate();
        Assert.NotEqual(before, _surface.LastFrame!.Ops.OfType<SplashFill>().Last().Bounds.X);
    }

    private sealed class Mono : ISplashTextMeasurer
    {
        public SizeF Measure(string text, SplashFont font, float maxWidth) => new(Math.Min(text.Length * 7f, maxWidth), font.Size * 1.25f);
    }

    /// <summary>Renders synchronously on every Show and Invalidate, and records what was asked of it.</summary>
    private sealed class FakeSplashSurface : ISplashSurface
    {
        private readonly TaskCompletionSource _faded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SplashRender? _render;

        public bool FailShow { get; init; }
        public bool Shown { get; private set; }
        public Rectangle? Card { get; private set; }
        public nint AttachedTo { get; private set; }
        public SplashOverlayLayout? Layout { get; private set; }
        public List<string> Calls { get; } = [];
        public int Follows { get; private set; }
        public bool Faded { get; private set; }
        public TimeSpan FadeDuration { get; private set; }
        public bool Disposed { get; private set; }
        public int Frames { get; private set; }
        public SplashFrame? LastFrame { get; private set; }
        public string? LastText => LastFrame?.Ops.OfType<SplashTextRun>().LastOrDefault()?.Text;
        public Task FadedTask => _faded.Task;

        public List<string>? Log { get; set; }   // shared with another fake, to see the order across both

        private void Record(string call)
        {
            Calls.Add(call);
            Log?.Add($"{(Card is null ? "over" : "card")}:{call}");
        }

        public void ShowCard(Rectangle dipRect, SplashRender render)
        {
            if (FailShow) throw new InvalidOperationException("no window");
            Card = dipRect;
            Record("card");
            Show(render);
        }

        public void ShowOver(nint mainWindow, SplashOverlayLayout layout, SplashRender render)
        {
            if (FailShow) throw new InvalidOperationException("no window");
            Record("over");
            AttachedTo = mainWindow;
            Layout = layout;
            Show(render);
        }

        public void Reveal(Action shown)
        {
            Record("reveal");
            shown();
        }

        private void Show(SplashRender render)
        {
            _render = render;
            Shown = true;
            Invalidate();
        }

        public void Invalidate()
        {
            if (_render is null || Disposed) return;
            LastFrame = _render(new Size(400, 300), 1, new Mono());
            Frames++;
        }

        public void FollowOwner() => Follows++;

        public void Uncover() => Record("uncover");

        public void FadeOut(TimeSpan duration, Action done)
        {
            Faded = true;
            FadeDuration = duration;
            done();
            _faded.TrySetResult();
        }

        public void Dispose()
        {
            if (!Disposed) Log?.Add($"{(Card is null ? "over" : "card")}:dispose");
            Disposed = true;
        }
    }
}
