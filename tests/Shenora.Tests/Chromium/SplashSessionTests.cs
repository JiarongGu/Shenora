using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shenora.Chromium;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The splash's lifecycle against a fake surface and a fake clock: when it shows, what lifts it, what never does, and
/// that a failing app callback or a missing display never reaches the app.
/// </summary>
public class SplashSessionTests
{
    private static readonly ChromiumWindowGeometry.Plan Where = new(400, 300, 0, 0, false);
    private readonly FakeTimeProvider _time = new();
    private readonly FakeSplashSurface _surface = new();

    private SplashSession Session(ChromiumSplashOptions options, FakeSplashSurface? surface = null, Color? windowBackground = null, bool? dark = null,
        bool noDisplay = false) =>
        new(options, title: "App", windowBackground ?? Color.Black, new ServiceCollection().BuildServiceProvider(), bus: null,
            noDisplay ? () => null : () => surface ?? _surface, _time, log: null, systemDark: dark);

    private static SplashComponent Text(Func<SplashContext, Func<string>> setup) => context =>
    {
        var text = setup(context);
        return () => new SplashText(text());
    };

    [Fact]
    public void It_shows_on_start_attaches_when_the_window_opens_and_lifts_at_the_handshake()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where);
        Assert.True(_surface.Shown);
        Assert.True(session.IsShowing);

        session.WindowOpened(42);
        Assert.Equal(42, _surface.AttachedTo);
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
        session.Start(Where);
        session.WindowOpened(1);
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
        session.Start(Where);
        session.WindowOpened(1);
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
        session.Start(Where);
        session.WindowOpened(1);
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
        session.Start(Where);
        session.WindowOpened(1);
        session.PageReady();
        await _surface.FadedTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_timeout_stands_in_for_the_page_and_counts_from_the_window()
    {
        using var session = Session(new ChromiumSplashOptions { HoldUntilClosed = true, Timeout = TimeSpan.FromSeconds(15) });
        session.Start(Where);
        _time.Advance(TimeSpan.FromSeconds(20));   // no window yet: no clock running
        Assert.False(_surface.Faded);

        session.WindowOpened(1);
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
        session.Start(Where);
        session.WindowOpened(1);
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
        session.Start(Where);
        session.WindowOpened(7);
        Assert.Equal(7, surface.AttachedTo);   // attached before the timer, which CEF's show does not wait for
        Assert.True(opened);
    }

    [Fact]
    public void A_timeout_longer_than_a_timer_can_hold_is_taken_as_none()
    {
        var surface = new FakeSplashSurface();
        using var session = new SplashSession(new ChromiumSplashOptions { HoldUntilClosed = true, Timeout = TimeSpan.MaxValue }, "App", Color.Black,
            new ServiceCollection().BuildServiceProvider(), null, () => surface, TimeProvider.System, null, null);
        session.Start(Where);
        session.WindowOpened(7);   // TimeProvider.System refuses a due time past ~49 days
        Assert.Equal(7, surface.AttachedTo);
        Assert.True(session.IsShowing);
    }

    [Fact]
    public void Closed_during_its_own_setup_it_makes_no_window_at_all()
    {
        var made = 0;
        using var session = new SplashSession(new ChromiumSplashOptions { Component = c => { c.Close(); return () => new SplashStack(); } }, "App",
            Color.Black, new ServiceCollection().BuildServiceProvider(), null, () => { made++; return _surface; }, _time, null, null);
        session.Start(Where);
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
        session.Start(Where);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.WindowOpened(1);
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
            session.Start(Where);
            context!.Close();
            Assert.True(_surface.Faded);
        }

        var other = new FakeSplashSurface();
        using var second = Session(new ChromiumSplashOptions(), other);
        second.Start(Where);
        second.Close();
        Assert.True(other.Faded);
    }

    [Fact]
    public void Abort_destroys_it_without_a_fade()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where);
        session.Abort();
        Assert.False(_surface.Faded);
        Assert.True(_surface.Disposed);
    }

    [Fact]
    public void A_zero_fade_is_a_hard_cut()
    {
        using var session = Session(new ChromiumSplashOptions { FadeOut = TimeSpan.Zero });
        session.Start(Where);
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
        session.Start(Where);
        Assert.Equal("b", _surface.LastText);
    }

    [Fact]
    public void A_state_change_presents_a_new_frame()
    {
        SplashState<string>? state = null;
        using var session = Session(new ChromiumSplashOptions { Component = Text(context => { state = context.State("a"); return () => state.Value; }) });
        session.Start(Where);
        state!.Value = "b";
        Assert.Equal("b", _surface.LastText);
        Assert.Equal(2, _surface.Frames);
    }

    [Fact]
    public void State_set_after_the_lift_draws_nothing()
    {
        SplashState<string>? state = null;
        using var session = Session(new ChromiumSplashOptions { Component = Text(context => { state = context.State("a"); return () => state.Value; }) });
        session.Start(Where);
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
        session.Start(Where);
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
        session.Start(Where);
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
        session.Start(Where);
        Assert.False(session.IsShowing);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_surface_that_fails_to_show_costs_the_splash_and_not_the_app()
    {
        var failing = new FakeSplashSurface { FailShow = true };
        using var session = Session(new ChromiumSplashOptions(), failing);
        session.Start(Where);
        Assert.False(session.IsShowing);
        Assert.True(failing.Disposed);
        session.WindowOpened(1);
        session.PageReady();
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
        session.Start(Where);
        session.WindowOpened(1);
        session.PageReady();
        Assert.Equal(["window", "page"], events);
    }

    [Fact]
    public void The_owner_moving_moves_the_splash()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where);
        session.WindowOpened(1);
        session.OwnerMoved();
        Assert.Equal(1, _surface.Follows);
    }

    [Fact]
    public void The_background_is_the_splash_s_then_the_window_s_then_the_system_theme_s()
    {
        Color First(FakeSplashSurface s) => ((SplashFill)s.LastFrame!.Ops[0]).Color;

        var own = new FakeSplashSurface();
        using (var s = Session(new ChromiumSplashOptions { Background = Color.Red }, own)) s.Start(Where);
        Assert.Equal(Color.Red, First(own));

        var window = new FakeSplashSurface();
        using (var s = Session(new ChromiumSplashOptions(), window, windowBackground: Color.Blue)) s.Start(Where);
        Assert.Equal(Color.Blue, First(window));

        var light = new FakeSplashSurface();
        using (var s = new SplashSession(new ChromiumSplashOptions(), "App", null, new ServiceCollection().BuildServiceProvider(), null, () => light, _time, null, systemDark: false))
            s.Start(Where);
        Assert.Equal(Color.FromArgb(0xF3, 0xF3, 0xF3), First(light));

        var unknown = new FakeSplashSurface();
        using (var s = new SplashSession(new ChromiumSplashOptions(), "App", null, new ServiceCollection().BuildServiceProvider(), null, () => unknown, _time, null, systemDark: null))
            s.Start(Where);
        Assert.Equal(Color.FromArgb(0x1F, 0x1F, 0x1F), First(unknown));
    }

    [Fact]
    public void Without_a_component_it_shows_the_preset_with_the_window_title()
    {
        using var session = Session(new ChromiumSplashOptions());
        session.Start(Where);
        Assert.Equal("App", _surface.LastText);
    }

    [Fact]
    public void An_indeterminate_bar_moves_with_the_clock()
    {
        using var session = Session(new ChromiumSplashOptions { Component = _ => () => new SplashProgress() });
        session.Start(Where);
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
        public nint AttachedTo { get; private set; }
        public int Follows { get; private set; }
        public bool Faded { get; private set; }
        public TimeSpan FadeDuration { get; private set; }
        public bool Disposed { get; private set; }
        public int Frames { get; private set; }
        public SplashFrame? LastFrame { get; private set; }
        public string? LastText => LastFrame?.Ops.OfType<SplashTextRun>().LastOrDefault()?.Text;
        public Task FadedTask => _faded.Task;

        public void Show(ChromiumWindowGeometry.Plan placement, SplashRender render)
        {
            if (FailShow) throw new InvalidOperationException("no window");
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

        public void Attach(nint mainWindow) => AttachedTo = mainWindow;
        public void FollowOwner() => Follows++;

        public void FadeOut(TimeSpan duration, Action done)
        {
            Faded = true;
            FadeDuration = duration;
            done();
            _faded.TrySetResult();
        }

        public void Dispose() => Disposed = true;
    }
}
