using System.Collections.Concurrent;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's unhandled exceptions reaching the app (<see cref="ChromiumHostOptions.OnUnhandledException"/>),
/// the UI-thread channel against a fake UI thread. The process's own channels (AppDomain, unobserved tasks) are wired
/// once per process by the runner, and are not taken in-suite, as WinFormsBootstrap's are not.
/// </summary>
public class ChromiumUnhandledExceptionsTests
{
    private sealed class FakeUi
    {
        public readonly Queue<Action> Posted = new();
        public bool OnUi;

        public bool Post(Action work) { Posted.Enqueue(work); return true; }

        public void Drain()
        {
            OnUi = true;
            while (Posted.TryDequeue(out var work)) work();
            OnUi = false;
        }
    }

    // The options are the process's (static), and other tests' dispatchers may report too: assert on OUR exception.
    private static void With(Action<UnhandledExceptionReport> onException, Action body)
    {
        ChromiumUnhandledExceptions.Use(new ChromiumHostOptions { OnUnhandledException = onException });
        try { body(); }
        finally { ChromiumUnhandledExceptions.Use(null); }
    }

    [Fact]
    public void Posted_work_that_throws_reaches_the_app_and_the_loop_goes_on()
    {
        var reports = new ConcurrentQueue<UnhandledExceptionReport>();
        var thrown = new InvalidOperationException("posted work");
        With(reports.Enqueue, () =>
        {
            var ui = new FakeUi();
            var dispatcher = new CefUiDispatcher(ui.Post, () => ui.OnUi);
            dispatcher.MarkReady();
            var after = false;

            Assert.True(dispatcher.Post(() => throw thrown));
            Assert.True(dispatcher.Post(() => after = true));
            ui.Drain();

            Assert.True(after);
        });
        var report = Assert.Single(reports, r => ReferenceEquals(r.Exception, thrown));
        Assert.Equal(UnhandledExceptionSource.UiThread, report.Source);
        Assert.False(report.IsTerminating);
    }

    [Fact]
    public void An_async_void_continuation_that_throws_on_the_UI_thread_reaches_the_app()
    {
        // An async void method's exception is re-thrown on the context it captured: the shell's, which posts it.
        var reports = new ConcurrentQueue<UnhandledExceptionReport>();
        var thrown = new InvalidOperationException("async void");
        With(reports.Enqueue, () =>
        {
            var ui = new FakeUi();
            var dispatcher = new CefUiDispatcher(ui.Post, () => ui.OnUi);
            dispatcher.MarkReady();
            new CefUiContext(dispatcher).Post(_ => throw thrown, null);
            ui.Drain();
        });
        Assert.Single(reports, r => ReferenceEquals(r.Exception, thrown) && r.Source == UnhandledExceptionSource.UiThread);
    }

    [Fact]
    public void A_throwing_handler_is_swallowed()
    {
        var thrown = new InvalidOperationException("posted work");
        With(_ => throw new Exception("the app's own handler"), () =>
        {
            var ui = new FakeUi();
            var dispatcher = new CefUiDispatcher(ui.Post, () => ui.OnUi);
            dispatcher.MarkReady();
            dispatcher.Post(() => throw thrown);
            ui.Drain();   // no exception escapes the "UI thread"
        });
    }

    [Fact]
    public void Without_a_handler_nothing_is_reported()
    {
        ChromiumUnhandledExceptions.Use(null);
        ChromiumUnhandledExceptions.Report(new Exception("x"), UnhandledExceptionSource.AppDomain, isTerminating: true);
    }
}
