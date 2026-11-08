using Microsoft.Extensions.Logging;
using Shenora.Core.Shell;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// An exception raised on a kit window's thread (an event handler, a posted callback) is reported and the pump goes on,
/// not the process ended with no record. <c>Application.ThreadException</c> is per thread, so the handler the bootstrap
/// adds covers the main window only: each pump the kit starts has to catch for itself. Real own-thread pumps drive these.
/// </summary>
[Collection(nameof(WindowThreadExceptionsTests))]   // WinFormsBootstrap.Current is process-wide
public class WindowThreadExceptionsTests
{
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for: {what}");
            Thread.Sleep(20);
        }
    }

    private sealed class RecordedLog : ILogger<SecondaryWindows>
    {
        public List<(LogLevel Level, string Message, Exception? Error)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((level, formatter(state, error), error));
        }

        public (LogLevel Level, string Message, Exception? Error)[] Errors()
        {
            lock (Lines) return Lines.Where(l => l.Level == LogLevel.Error).ToArray();
        }
    }

    private static SecondaryWindowOptions Quiet() => new()
    {
        CreateForm = () => new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized },
    };

    [Fact]
    public void An_exception_on_a_secondary_windows_thread_reaches_the_given_logger_and_the_window_goes_on()
    {
        WinFormsBootstrap.Current = null;
        var log = new RecordedLog();
        using var windows = new SecondaryWindows(log);
        windows.Open("w1", Quiet());
        WaitUntil(() => windows.TryGetForm("w1") is { IsHandleCreated: true }, "window creation");
        var form = windows.TryGetForm("w1")!;

        var boom = new InvalidOperationException("raised on the window's thread");
        form.BeginInvoke(() => throw boom);

        WaitUntil(() => log.Errors().Length > 0, "the exception to be logged");
        var logged = log.Errors().Single();
        Assert.Same(boom, logged.Error);
        Assert.Contains("secondary window 'w1'", logged.Message, StringComparison.Ordinal);

        // The process is still here, and so are the window and its pump: later posted work still runs on it.
        var ran = new ManualResetEventSlim();
        form.BeginInvoke(ran.Set);
        Assert.True(ran.Wait(TimeSpan.FromSeconds(10)), "the pump stopped after the exception");
        Assert.True(windows.HasWindow("w1"));

        windows.Close("w1");
        WaitUntil(() => !windows.HasWindow("w1"), "window close");
    }

    [Fact]
    public void Once_the_app_has_given_the_bootstrap_its_callback_every_window_thread_reports_to_it()
    {
        UnhandledExceptionReport? seen = null;
        var log = new RecordedLog();
        WinFormsBootstrap.Current = new WinFormsBootstrapOptions { OnUnhandledException = r => seen = r, ShowCrashDialog = false };
        try
        {
            using var windows = new SecondaryWindows(log);
            windows.Open("w1", Quiet());
            WaitUntil(() => windows.TryGetForm("w1") is { IsHandleCreated: true }, "window creation");

            var boom = new InvalidOperationException("raised on the window's thread");
            windows.TryGetForm("w1")!.BeginInvoke(() => throw boom);

            WaitUntil(() => Volatile.Read(ref seen) is not null, "the app's callback");
            Assert.Same(boom, seen!.Exception);
            Assert.Equal(UnhandledExceptionSource.UiThread, seen.Source);
            Assert.False(seen.IsTerminating);
            Assert.Empty(log.Errors());   // the app's callback is its log; the kit does not write it twice

            windows.Close("w1");
            WaitUntil(() => !windows.HasWindow("w1"), "window close");
        }
        finally { WinFormsBootstrap.Current = null; }
    }

    // The window thread's handler is the thread's own: another window's exception is reported once, by its own thread.
    [Fact]
    public void Each_window_reports_its_own_exception_once()
    {
        WinFormsBootstrap.Current = null;
        var log = new RecordedLog();
        using var windows = new SecondaryWindows(log);
        windows.Open("a", Quiet());
        windows.Open("b", Quiet());
        WaitUntil(() => windows.TryGetForm("a") is { IsHandleCreated: true } && windows.TryGetForm("b") is { IsHandleCreated: true }, "both windows");

        windows.TryGetForm("a")!.BeginInvoke(() => throw new InvalidOperationException("a"));
        windows.TryGetForm("b")!.BeginInvoke(() => throw new InvalidOperationException("b"));

        WaitUntil(() => log.Errors().Length >= 2, "both exceptions to be logged");
        Thread.Sleep(200);   // a duplicate, if there is one, arrives by now
        Assert.Equal(["a", "b"], log.Errors().Select(l => l.Error!.Message).Order().ToArray());

        windows.CloseAll();
        WaitUntil(() => windows.WindowCount == 0, "close all");
    }
}
