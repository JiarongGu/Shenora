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
    /// <summary>
    /// Initialize WinForms (visual styles, GDI+ text rendering, DPI mode, catch-mode for UI
    /// exceptions) and wire the three global exception channels to
    /// <see cref="WinFormsBootstrapOptions.OnUnhandledException"/> + a last-resort dialog.
    /// </summary>
    public static void Initialize(WinFormsBootstrapOptions? options = null)
    {
        var opt = options ?? new WinFormsBootstrapOptions();

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
        try
        {
            options.OnUnhandledException?.Invoke(report);
        }
        catch
        {
            // the crash handler must never crash
        }

        // Unobserved tasks get logged, not dialogued — they're background noise by definition.
        if (options.ShowCrashDialog && report.Source != UnhandledExceptionSource.UnobservedTask)
        {
            try
            {
                var title = report.IsTerminating
                    ? $"{options.ApplicationName} — fatal error"
                    : $"{options.ApplicationName} — unexpected error";
                MessageBox.Show(
                    $"{report.Exception.GetType().Name}: {report.Exception.Message}\n\n" +
                    (report.IsTerminating
                        ? "The application has to close. Details were written to the log."
                        : "The application will try to continue. Details were written to the log."),
                    title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // best-effort — a dead UI must not mask the original failure
            }
        }
    }
}
