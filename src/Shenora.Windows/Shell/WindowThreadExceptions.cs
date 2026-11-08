using Shenora.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Shenora.Windows;

/// <summary>
/// What a window thread does with an exception nobody caught on it. <c>Application.ThreadException</c> is PER THREAD: the
/// handler <see cref="WinFormsBootstrap.Initialize"/> adds covers the main window's thread only, and on any other pump
/// WinForms, finding none, either shows its own dialog or ends the process with no record. So every pump the kit starts
/// (<see cref="SecondaryWindows"/>, the shared STA apartment) enters this first: the exception is caught, reported, and the
/// pump goes on.
/// </summary>
internal static class WindowThreadExceptions
{
    /// <summary>Catch this thread's unhandled exceptions until the scope is disposed (on this same thread). Call it before
    /// the thread creates a control: WinForms refuses a per-thread mode once one exists, and that is not an error here.
    /// A report goes where the main window's goes, <see cref="WinFormsBootstrapOptions.OnUnhandledException"/> and the
    /// crash dialog, once <see cref="WinFormsBootstrap.Initialize"/> has run; otherwise to <paramref name="logger"/>.</summary>
    public static IDisposable Enter(string window, ILogger? logger)
    {
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true); }
        catch (InvalidOperationException) { }

        ThreadExceptionEventHandler handler = (_, e) => Report(window, e.Exception, logger);
        Application.ThreadException += handler;
        return new Scope(handler);
    }

    // Never throws: it runs from an exception channel with nothing above it on the stack.
    private static void Report(string window, Exception exception, ILogger? logger)
    {
        if (WinFormsBootstrap.Current is { } options)
            WinFormsBootstrap.Handle(new UnhandledExceptionReport(exception, UnhandledExceptionSource.UiThread, IsTerminating: false), options);
        else
            AppCallback.Log(logger, () => $"An unhandled exception on the thread of {window}", LogLevel.Error, exception);
    }

    private sealed class Scope(ThreadExceptionEventHandler handler) : IDisposable
    {
        public void Dispose() => Application.ThreadException -= handler;
    }
}
