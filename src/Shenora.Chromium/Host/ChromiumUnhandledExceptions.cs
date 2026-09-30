using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The three places an exception can go unhandled in the Chromium shell, and the app's
/// <see cref="ChromiumHostOptions.OnUnhandledException"/> they report to (D88, the WinForms bootstrap's channels):
/// work posted to CEF's UI thread, an <c>async void</c> continuation there included, which the dispatcher catches and
/// keeps the loop running; any other thread (<c>AppDomain.UnhandledException</c>); and a faulted task nobody observed.
/// No dialog: the shell has no portable one, and the app owns what it shows.
/// </summary>
internal static class ChromiumUnhandledExceptions
{
    private static int _installed;
    private static ChromiumHostOptions? _options;

    /// <summary>The runner, once per process, since the channels are the process's: from here on they report to
    /// <paramref name="options"/>' callback.</summary>
    public static void Install(ChromiumHostOptions options)
    {
        Use(options);
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(
            e.ExceptionObject as Exception ?? new Exception($"Non-exception object: {e.ExceptionObject}"),
            UnhandledExceptionSource.AppDomain, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (Volatile.Read(ref _options)?.ObserveUnobservedTaskExceptions ?? true) e.SetObserved();
            Report(e.Exception, UnhandledExceptionSource.UnobservedTask, isTerminating: false);
        };
    }

    /// <summary>Report to <paramref name="options"/> without taking the process's channels (tests).</summary>
    internal static void Use(ChromiumHostOptions? options) => Volatile.Write(ref _options, options);

    /// <summary>Hand the app the exception. Never throws: the app's handler is guarded, as a crash handler must be.</summary>
    public static void Report(Exception exception, UnhandledExceptionSource source, bool isTerminating)
    {
        if (Volatile.Read(ref _options)?.OnUnhandledException is { } onException)
            AppCallback.Run(() => onException(new UnhandledExceptionReport(exception, source, isTerminating)));
    }
}
