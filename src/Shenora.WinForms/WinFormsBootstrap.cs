namespace Shenora.WinForms;

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

    /// <summary>DPI mode. The family standard is PerMonitorV2 (see <see cref="DpiHelper"/> for the implications).</summary>
    public HighDpiMode HighDpiMode { get; init; } = HighDpiMode.PerMonitorV2;

    /// <summary>
    /// Receives EVERY unhandled exception (log it here — this is the crash log the source apps
    /// lacked). Must never throw; a throwing handler is swallowed.
    /// </summary>
    public Action<UnhandledExceptionReport>? OnUnhandledException { get; init; }

    /// <summary>
    /// Show a last-resort MessageBox for UI-thread and terminating exceptions. Off for headless
    /// tests/tools.
    /// </summary>
    public bool ShowCrashDialog { get; init; } = true;

    /// <summary>
    /// Mark unobserved task exceptions observed so they don't escalate (they still reach
    /// <see cref="OnUnhandledException"/>). Matches the .NET default of not crashing, but LOGGED —
    /// the silent-swallow was the gap.
    /// </summary>
    public bool ObserveUnobservedTaskExceptions { get; init; } = true;
}

/// <summary>
/// One-call WinForms process initialization — the family's proven settings PLUS the global
/// exception handling every source app lacked (the audit's #1 gap: an unhandled UI-thread
/// exception after startup was completely undefended).
///
/// Call FIRST in <c>Main</c>, before any form or control is created (the text-rendering and DPI
/// settings reject later calls). Deliberately NOT ported from the sources: a reflection hack
/// against a non-public <c>Application</c> property (it targeted <c>RuntimeType</c>, a guaranteed
/// no-op).
/// </summary>
public static class WinFormsBootstrap
{
    private static int _initialized; // 0 = not yet; the wiring below is process-global and must run ONCE

    // MessageBox.Show PUMPS messages, so the crash dialog can re-enter Handle. See ShowCrashDialog.
    [ThreadStatic]
    private static bool _showingCrashDialog;

    /// <summary>
    /// Test seam for the last-resort dialog: a real <c>MessageBox</c> would block the suite forever, and
    /// the re-entrancy guard is the whole point of that method, so it needs to be drivable. Receives
    /// (title, body). Null = show the real dialog.
    /// </summary>
    internal static Action<string, string>? ShowDialogOverride;

    /// <summary>
    /// Initialize WinForms (visual styles, GDI+ text rendering, DPI mode, catch-mode for UI
    /// exceptions) and wire the three global exception channels to
    /// <see cref="WinFormsBootstrapOptions.OnUnhandledException"/> + a last-resort dialog.
    /// <para>
    /// IDEMPOTENT (P5.5 H2): only the first call does anything, and its options win. A second call used
    /// to re-register all three exception channels, so every later exception was reported twice and
    /// raised two stacked crash dialogs — and the natural way to hit that is a library and its host app
    /// both trying to be well-behaved.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The calling thread is not STA. See the message for why this is fatal rather than a warning.
    /// </exception>
    public static void Initialize(WinFormsBootstrapOptions? options = null)
    {
        var opt = options ?? new WinFormsBootstrapOptions();

        // Fail HERE, loudly, with the fix in the message (P5.5 H2). WinForms needs an STA thread for
        // every OLE feature — drag-and-drop registration, the shell file dialogs, the clipboard — and
        // without [STAThread] the failure lands much later and much worse: handle creation throws
        // inside WndProc, which WinForms answers with a BLOCKING modal dialog, on a window that is
        // often not visible yet. The repo's own test suite has an earned rule about this (xunit workers
        // are MTA), and it cost a stalled suite to learn.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "WinFormsBootstrap.Initialize must run on an STA thread. Add [STAThread] to your Main " +
                "method (or call Thread.CurrentThread.SetApartmentState(ApartmentState.STA) before any " +
                "WinForms type is touched). Without it, OLE features — drag-and-drop, the file dialogs, " +
                "the clipboard — fail later inside window creation instead of here.");
        }

        // Interlocked, not a plain bool: Initialize is documented as the FIRST call in Main, but nothing
        // stops a library from calling it on another thread at startup.
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
        // The app's crash logger is app code invoked from an exception channel — nothing above it on
        // the stack. Through the one guard (P5.5 H2): the crash handler must never crash.
        if (options.OnUnhandledException is { } onException)
            Shenora.Core.AppCallback.Run(() => onException(report));

        // Unobserved tasks get logged, not dialogued — they're background noise by definition.
        if (options.ShowCrashDialog && report.Source != UnhandledExceptionSource.UnobservedTask)
            ShowCrashDialog(report, options);
    }

    /// <summary>
    /// The last-resort dialog, with a RE-ENTRANCY GUARD (P5.5 H2).
    /// <para>
    /// <see cref="MessageBox.Show(string)"/> runs its own modal message loop, so it PUMPS — which means
    /// a UI-thread exception that recurs (a broken paint handler, a timer that throws every tick) is
    /// dispatched again while this dialog is still up, re-entering <see cref="Handle"/> and stacking
    /// another dialog on top. Every one of them pumps too, so the app ends up with an unbounded pile of
    /// modal dialogs over a window nobody can reach, and the user cannot dismiss them faster than they
    /// arrive. One dialog at a time per thread; recurrences still reach the app's logger above, which is
    /// where a repeating fault belongs anyway.
    /// </para>
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
