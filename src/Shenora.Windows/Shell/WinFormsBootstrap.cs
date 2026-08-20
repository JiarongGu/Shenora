namespace Shenora.Windows;

/// <summary>Where an unhandled exception surfaced.</summary>
public enum UnhandledExceptionSource
{
    /// <summary>WinForms UI-thread exception (<c>Application.ThreadException</c>) — recoverable.</summary>
    UiThread,

    /// <summary>Any other thread (<c>AppDomain.UnhandledException</c>) — the process is usually dying.</summary>
    AppDomain,

    /// <summary>A faulted Task nobody observed (<c>TaskScheduler.UnobservedTaskException</c>).</summary>
    UnobservedTask,
}

/// <summary>An unhandled exception delivered to <see cref="WinFormsBootstrapOptions.OnUnhandledException"/>.</summary>
public sealed record UnhandledExceptionReport(Exception Exception, UnhandledExceptionSource Source, bool IsTerminating);

/// <summary>Options for <see cref="WinFormsBootstrap.Initialize"/>.</summary>
public sealed class WinFormsBootstrapOptions
{
    /// <summary>Shown in the last-resort crash dialog's title.</summary>
    public string ApplicationName { get; init; } = "Application";

    /// <summary>DPI mode. PerMonitorV2 by default — see <see cref="DpiHelper"/> for what that implies.</summary>
    public HighDpiMode HighDpiMode { get; init; } = HighDpiMode.PerMonitorV2;

    /// <summary>
    /// Receives EVERY unhandled exception — log it here. Must never throw; a throwing handler is
    /// swallowed.
    /// </summary>
    public Action<UnhandledExceptionReport>? OnUnhandledException { get; init; }

    /// <summary>Show a last-resort MessageBox for UI-thread and terminating exceptions. Off for headless
    /// tests/tools.</summary>
    public bool ShowCrashDialog { get; init; } = true;

    /// <summary>
    /// Mark unobserved task exceptions observed so they don't escalate — they still reach
    /// <see cref="OnUnhandledException"/>, so they are logged rather than silently swallowed.
    /// </summary>
    public bool ObserveUnobservedTaskExceptions { get; init; } = true;
}

/// <summary>
/// One-call WinForms process initialization — visual styles, text rendering, DPI mode, and the three
/// global exception channels. ⚠ Call FIRST in <c>Main</c>, before any form or control is created: the
/// text-rendering and DPI settings reject a later call.
/// </summary>
public static class WinFormsBootstrap
{
    private static int _initialized; // 0 = not yet; the wiring below is process-global and must run ONCE

    // MessageBox.Show PUMPS messages, so the crash dialog can re-enter Handle. See ShowCrashDialog.
    [ThreadStatic]
    private static bool _showingCrashDialog;

    /// <summary>
    /// Test seam for the last-resort dialog — a real <c>MessageBox</c> would block the suite forever.
    /// Receives (title, body); null = show the real dialog.
    /// </summary>
    internal static Action<string, string>? ShowDialogOverride;

    /// <summary>
    /// Initialize WinForms (visual styles, GDI+ text rendering, DPI mode, catch-mode for UI exceptions)
    /// and wire the three global exception channels to
    /// <see cref="WinFormsBootstrapOptions.OnUnhandledException"/> + a last-resort dialog.
    /// <para>
    /// 🔴 IDEMPOTENT: first call wins. A second call re-registering all three channels reported every
    /// later exception twice and raised two stacked crash dialogs — the natural way to hit it is a
    /// library and its host app both being well-behaved.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The calling thread is not STA. See the message for why this is fatal rather than a warning.
    /// </exception>
    public static void Initialize(WinFormsBootstrapOptions? options = null)
    {
        var opt = options ?? new WinFormsBootstrapOptions();

        // 🔴 STA-OR-FAIL, here and loudly. Every OLE feature — drag-and-drop registration, the shell
        // dialogs, the clipboard — needs it, and without [STAThread] the failure lands far later and far
        // worse: handle creation throws inside WndProc, which WinForms answers with a BLOCKING modal
        // dialog on a window that is often not visible yet.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "WinFormsBootstrap.Initialize must run on an STA thread. Add [STAThread] to your Main " +
                "method (or call Thread.CurrentThread.SetApartmentState(ApartmentState.STA) before any " +
                "WinForms type is touched). Without it, OLE features — drag-and-drop, the file dialogs, " +
                "the clipboard — fail later inside window creation instead of here.");
        }

        // Interlocked, not a plain bool: nothing stops a library from calling this on another thread.
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(opt.HighDpiMode);
        // Route UI-thread exceptions to Application.ThreadException instead of crashing the pump.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
            Handle(new UnhandledExceptionReport(e.Exception, UnhandledExceptionSource.UiThread, IsTerminating: false), opt);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle(new UnhandledExceptionReport(
                e.ExceptionObject as Exception ?? new Exception($"Non-exception object: {e.ExceptionObject}"),
                UnhandledExceptionSource.AppDomain, e.IsTerminating), opt);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            if (opt.ObserveUnobservedTaskExceptions) e.SetObserved();
            Handle(new UnhandledExceptionReport(e.Exception, UnhandledExceptionSource.UnobservedTask, IsTerminating: false), opt);
        };
    }

    /// <summary>Internal for tests (the wiring above is process-global and untestable in-suite).</summary>
    internal static void Handle(UnhandledExceptionReport report, WinFormsBootstrapOptions options)
    {
        // Guarded: the app's crash logger runs from an exception channel with nothing above it on the
        // stack, and the crash handler must never crash.
        if (options.OnUnhandledException is { } onException)
            Shenora.AppCallback.Run(() => onException(report));

        // Unobserved tasks get logged, not dialogued — they're background noise by definition.
        if (options.ShowCrashDialog && report.Source != UnhandledExceptionSource.UnobservedTask)
            ShowCrashDialog(report, options);
    }

    /// <summary>
    /// The last-resort dialog, with a RE-ENTRANCY GUARD: <see cref="MessageBox.Show(string)"/> runs its
    /// own modal message loop, so a RECURRING UI-thread exception is dispatched again while the dialog is
    /// up, re-entering <see cref="Handle"/> and stacking dialogs unboundedly over a window nobody can
    /// reach. One at a time per thread; recurrences still reach the app's logger.
    /// </summary>
    private static void ShowCrashDialog(UnhandledExceptionReport report, WinFormsBootstrapOptions options)
    {
        if (_showingCrashDialog) return;
        _showingCrashDialog = true;
        try
        {
            var title = report.IsTerminating
                ? $"{options.ApplicationName} — fatal error"
                : $"{options.ApplicationName} — unexpected error";
            var body = $"{report.Exception.GetType().Name}: {report.Exception.Message}\n\n" +
                (report.IsTerminating
                    ? "The application has to close. Details were written to the log."
                    : "The application will try to continue. Details were written to the log.");

            if (ShowDialogOverride is { } show) show(title, body);
            else MessageBox.Show(body, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // best-effort — a dead UI must not mask the original failure
        }
        finally
        {
            _showingCrashDialog = false;
        }
    }
}
