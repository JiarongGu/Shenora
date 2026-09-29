using Shenora.Chromium;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// CEF started as the app is composed (D87): only for the app run from its layout, taken over by the runner on the
/// composing thread, and a start that failed reported when the app runs. Each case drives its own instance with a
/// stand-in for CEF's start; the process's own stays untouched, since this test host is not an app's layout.
/// </summary>
public class ChromiumEarlyStartTests
{
    private static readonly object App = new();

    [Fact]
    public void Composing_outside_the_layout_starts_nothing_and_leaves_the_start_to_the_runner()
    {
        Assert.False(ChromiumEarlyStart.LaunchedFromLayout);
        var options = new ChromiumHostOptions();
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Early start test" }).UseChromium(options);

        Assert.False(ChromiumEarlyStart.Process.HasStarted);
        Assert.False(ChromiumEarlyStart.Process.TakeOver(options, () => Assert.Fail("nothing started")));
    }

    [Fact]
    public void The_runner_starts_the_app_at_once_when_the_context_already_exists()
    {
        var early = new ChromiumEarlyStart();
        var options = new ChromiumHostOptions();
        early.Start(options, contextInitialized => { contextInitialized(); return App; });

        var started = 0;
        Assert.True(early.TakeOver(options, () => started++));
        Assert.Equal(1, started);
    }

    [Fact]
    public void The_app_starts_when_the_context_comes_later()
    {
        var early = new ChromiumEarlyStart();
        var options = new ChromiumHostOptions();
        Action? context = null;
        early.Start(options, contextInitialized => { context = contextInitialized; return App; });

        var started = 0;
        Assert.True(early.TakeOver(options, () => started++));
        Assert.Equal(0, started);
        context!();
        Assert.Equal(1, started);
    }

    [Fact]
    public void A_start_that_failed_is_thrown_by_the_runner_not_by_the_composition()
    {
        var early = new ChromiumEarlyStart();
        var options = new ChromiumHostOptions();
        var failure = new InvalidOperationException("Chromium did not start");
        early.Start(options, _ => throw failure);   // returns: the composition goes on

        Assert.True(early.HasStarted);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => early.TakeOver(options, () => { })));
    }

    [Fact]
    public void Cef_starts_once_so_a_second_app_is_refused()
    {
        var early = new ChromiumEarlyStart();
        var options = new ChromiumHostOptions();
        var starts = 0;
        early.Start(options, contextInitialized => { starts++; contextInitialized(); return App; });
        early.Start(new ChromiumHostOptions(), _ => { starts++; return App; });

        Assert.Equal(1, starts);
        Assert.Contains("other options", Assert.Throws<InvalidOperationException>(() => early.TakeOver(new ChromiumHostOptions(), () => { })).Message);
    }

    [Fact]
    public void The_app_must_run_on_the_thread_that_composed_it()
    {
        var early = new ChromiumEarlyStart();
        var options = new ChromiumHostOptions();
        early.Start(options, contextInitialized => { contextInitialized(); return App; });

        Exception? thrown = null;
        var other = new Thread(() => thrown = Record.Exception(() => early.TakeOver(options, () => { })));
        other.Start();
        other.Join();
        Assert.Contains("thread that composed it", Assert.IsType<InvalidOperationException>(thrown).Message);
    }
}
