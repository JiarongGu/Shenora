using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Events;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The splash's component model: a setup that runs once, state whose change asks for a render, the events a component
/// hooks, and the class form, which is the same setup behind constructor injection.
/// </summary>
public class SplashComponentTests
{
    private sealed class Sink : ISplashSessionSink
    {
        public int Invalidations;
        public int Closes;
        public void Invalidate() => Interlocked.Increment(ref Invalidations);
        public void Close() => Interlocked.Increment(ref Closes);
        public SplashSurface Surface { get; set; } = SplashSurface.Card;
    }

    private static SplashContext Context(Sink sink, IEventBus? bus = null, IServiceProvider? services = null, bool? dark = null) =>
        new(services ?? new ServiceCollection().BuildServiceProvider(), dark, bus, sink, log: null);

    [Fact]
    public void The_context_reports_the_surface_its_session_is_drawing()
    {
        var sink = new Sink();
        var context = Context(sink);
        Assert.Equal(SplashSurface.Card, context.Surface);
        sink.Surface = SplashSurface.Window;
        Assert.Equal(SplashSurface.Window, context.Surface);
    }

    [Fact]
    public void A_state_change_asks_for_a_render_and_an_equal_set_does_not()
    {
        var sink = new Sink();
        var state = Context(sink).State("a");
        state.Value = "a";
        Assert.Equal(0, sink.Invalidations);
        state.Value = "b";
        Assert.Equal(1, sink.Invalidations);
        Assert.Equal("b", state.Value);
    }

    [Fact]
    public void State_set_after_the_lift_changes_nothing()
    {
        var sink = new Sink();
        var context = Context(sink);
        var state = context.State(0);
        context.Lifted();
        state.Value = 5;
        Assert.Equal(0, sink.Invalidations);
    }

    [Fact]
    public void State_set_from_many_threads_ends_on_one_of_their_values()
    {
        var sink = new Sink();
        var state = Context(sink).State(0);
        Parallel.For(1, 1001, i => state.Value = i);
        Assert.InRange(state.Value, 1, 1000);
        Assert.InRange(sink.Invalidations, 1, 1000);
    }

    [Fact]
    public async Task A_subscription_reaches_the_handler_until_the_lift_and_not_after()
    {
        var bus = new EventBus();
        var context = Context(new Sink(), bus);
        var seen = new List<object?>();
        context.Subscribe("BOOT", "STEP", m => seen.Add(m.Payload));

        await bus.EmitAsync("BOOT", "STEP", 1);
        context.Lifted();
        await bus.EmitAsync("BOOT", "STEP", 2);

        Assert.Equal([1], seen);
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_reach_the_emitter()
    {
        var bus = new EventBus();
        Context(new Sink(), bus).Subscribe("BOOT", "STEP", _ => throw new InvalidOperationException("app bug"));
        await bus.EmitAsync("BOOT", "STEP");
    }

    [Fact]
    public void Without_a_bus_a_subscription_is_a_no_op() =>
        Context(new Sink()).Subscribe("BOOT", "STEP", _ => throw new InvalidOperationException("never"));

    [Fact]
    public void A_throwing_callback_does_not_stop_the_others()
    {
        var context = Context(new Sink());
        var opened = 0;
        var ready = 0;
        context.OnWindowOpened(() => throw new InvalidOperationException());
        context.OnWindowOpened(() => opened++);
        context.OnPageReady(() => throw new InvalidOperationException());
        context.OnPageReady(() => ready++);

        context.RaiseWindowOpened();
        context.RaisePageReady();

        Assert.Equal(1, opened);
        Assert.Equal(1, ready);
    }

    [Fact]
    public void Close_reaches_the_session()
    {
        var sink = new Sink();
        Context(sink).Close();
        Assert.Equal(1, sink.Closes);
    }

    [Fact]
    public void Boot_work_is_taken_once_and_a_later_registration_is_refused()
    {
        var context = Context(new Sink());
        context.OnShown(_ => Task.CompletedTask);
        context.OnShown(_ => Task.CompletedTask);
        Assert.Equal(2, context.TakeShownWork().Count);
        Assert.Throws<InvalidOperationException>(() => context.OnShown(_ => Task.CompletedTask));
    }

    [Fact]
    public void The_context_carries_the_system_theme() => Assert.True(Context(new Sink(), dark: true).SystemDark);

    [Fact]
    public void Of_builds_the_class_with_its_constructor_services()
    {
        var services = new ServiceCollection().AddSingleton(new Marker("injected")).BuildServiceProvider();
        var render = Splash.Of<ClassSplash>()(Context(new Sink(), services: services));
        Assert.Equal("injected", Assert.IsType<SplashText>(render()).Text);
    }

    [Fact]
    public void The_preset_is_an_image_a_title_and_an_indeterminate_bar()
    {
        var tree = Assert.IsType<SplashStack>(Splash.Preset("logo.png", "App")(Context(new Sink()))());
        Assert.Equal("logo.png", Assert.IsType<SplashImage>(tree.Children[0]).Path);
        Assert.Equal("App", Assert.IsType<SplashText>(tree.Children[1]).Text);
        Assert.Null(Assert.IsType<SplashProgress>(tree.Children[2]).Value);

        var bare = Assert.IsType<SplashStack>(Splash.Preset()(Context(new Sink()))());
        Assert.IsType<SplashProgress>(Assert.Single(bare.Children));
    }

    private sealed record Marker(string Name);

    private sealed class ClassSplash(Marker marker) : ISplashComponent
    {
        public Func<SplashElement> Setup(SplashContext context) => () => new SplashText(marker.Name);
    }
}
