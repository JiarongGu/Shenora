using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The wiring in <see cref="WinFormsBootstrap.Initialize"/> is process-global (AppDomain events,
/// WinForms one-shot settings) and can't run in-suite — these tests cover the handler pipeline
/// via the internal <see cref="WinFormsBootstrap.Handle"/> seam.
/// </summary>
public class WinFormsBootstrapTests
{
    private static WinFormsBootstrapOptions Options(Action<UnhandledExceptionReport> onException) =>
        new() { OnUnhandledException = onException, ShowCrashDialog = false };

    [Fact]
    public void Handle_delivers_the_report_to_the_callback()
    {
        UnhandledExceptionReport? seen = null;
        var report = new UnhandledExceptionReport(new InvalidOperationException("boom"),
            UnhandledExceptionSource.UiThread, IsTerminating: false);

        WinFormsBootstrap.Handle(report, Options(r => seen = r));

        Assert.Same(report, seen);
    }

    [Fact]
    public void A_throwing_callback_is_swallowed()
    {
        var report = new UnhandledExceptionReport(new Exception("x"), UnhandledExceptionSource.AppDomain, true);
        var ex = Record.Exception(() =>
            WinFormsBootstrap.Handle(report, Options(_ => throw new Exception("handler crashed"))));
        Assert.Null(ex); // the crash handler must never crash
    }

    [Fact]
    public void Null_callback_and_disabled_dialog_are_a_no_op()
    {
        var report = new UnhandledExceptionReport(new Exception("x"), UnhandledExceptionSource.UnobservedTask, false);
        var ex = Record.Exception(() =>
            WinFormsBootstrap.Handle(report, new WinFormsBootstrapOptions { ShowCrashDialog = false }));
        Assert.Null(ex);
    }

    [Fact]
    public void Initialize_rejects_a_non_STA_thread_with_an_actionable_message()
    {
        // xunit workers are MTA, which is exactly the shape of an app whose Main lacks [STAThread].
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());

        // It must fail HERE rather than later inside window creation, where WinForms answers with a
        // BLOCKING modal dialog on a window that may not be visible yet (P5.5 H2).
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WinFormsBootstrap.Initialize(new WinFormsBootstrapOptions { ShowCrashDialog = false }));

        Assert.Contains("STAThread", ex.Message, StringComparison.Ordinal); // the fix is in the message
    }

    [Fact]
    public void Only_one_crash_dialog_is_shown_at_a_time()
    {
        // MessageBox.Show runs its own modal loop, so it PUMPS: a UI-thread exception that RECURS (a
        // broken paint handler, a timer throwing every tick) is dispatched again while the dialog is
        // still up, re-entering Handle and stacking another dialog — unboundedly, over a window nobody
        // can reach (P5.5 H2). The override stands in for that pumping.
        var shown = 0;
        WinFormsBootstrapOptions options = null!;
        options = new WinFormsBootstrapOptions { ApplicationName = "Test" };
        WinFormsBootstrap.ShowDialogOverride = (_, _) =>
        {
            shown++;
            if (shown < 4) // "the pump dispatched the recurring exception while we were up"
            {
                WinFormsBootstrap.Handle(
                    new UnhandledExceptionReport(new Exception("again"), UnhandledExceptionSource.UiThread, false),
                    options);
            }
        };
        try
        {
            WinFormsBootstrap.Handle(
                new UnhandledExceptionReport(new Exception("first"), UnhandledExceptionSource.UiThread, false), options);

            Assert.Equal(1, shown); // the re-entrant reports are dropped, not stacked
        }
        finally
        {
            WinFormsBootstrap.ShowDialogOverride = null;
        }
    }

    [Fact]
    public void A_recurring_exception_still_reaches_the_app_logger()
    {
        // The dialog is suppressed on re-entry; the LOG must not be — a repeating fault is exactly what
        // an app needs recorded.
        var logged = 0;
        WinFormsBootstrapOptions options = null!;
        options = new WinFormsBootstrapOptions
        {
            OnUnhandledException = _ =>
            {
                if (++logged < 3)
                {
                    WinFormsBootstrap.Handle(
                        new UnhandledExceptionReport(new Exception("again"), UnhandledExceptionSource.UiThread, false),
                        options);
                }
            },
        };
        WinFormsBootstrap.ShowDialogOverride = (_, _) => { };
        try
        {
            WinFormsBootstrap.Handle(
                new UnhandledExceptionReport(new Exception("first"), UnhandledExceptionSource.UiThread, false), options);

            Assert.Equal(3, logged);
        }
        finally
        {
            WinFormsBootstrap.ShowDialogOverride = null;
        }
    }

    [Fact]
    public void The_dialog_title_and_body_distinguish_a_fatal_exception()
    {
        var seen = new List<(string Title, string Body)>();
        WinFormsBootstrap.ShowDialogOverride = (t, b) => seen.Add((t, b));
        try
        {
            var options = new WinFormsBootstrapOptions { ApplicationName = "Shenora Sample" };
            WinFormsBootstrap.Handle(
                new UnhandledExceptionReport(new Exception("x"), UnhandledExceptionSource.AppDomain, IsTerminating: true),
                options);

            Assert.Contains("Shenora Sample", seen[0].Title, StringComparison.Ordinal);
            Assert.Contains("fatal", seen[0].Title, StringComparison.Ordinal);
            Assert.Contains("has to close", seen[0].Body, StringComparison.Ordinal);
        }
        finally
        {
            WinFormsBootstrap.ShowDialogOverride = null;
        }
    }

    [Fact]
    public void An_unobserved_task_exception_is_logged_but_never_dialogued()
    {
        var shown = 0;
        var logged = 0;
        WinFormsBootstrap.ShowDialogOverride = (_, _) => shown++;
        try
        {
            WinFormsBootstrap.Handle(
                new UnhandledExceptionReport(new Exception("x"), UnhandledExceptionSource.UnobservedTask, false),
                new WinFormsBootstrapOptions { OnUnhandledException = _ => logged++ });

            Assert.Equal(1, logged);
            Assert.Equal(0, shown); // background noise by definition — log it, don't interrupt the user
        }
        finally
        {
            WinFormsBootstrap.ShowDialogOverride = null;
        }
    }

    [Fact]
    public void Defaults_are_the_family_standard()
    {
        var opt = new WinFormsBootstrapOptions();
        Assert.Equal(HighDpiMode.PerMonitorV2, opt.HighDpiMode);
        Assert.True(opt.ShowCrashDialog);
        Assert.True(opt.ObserveUnobservedTaskExceptions);
    }
}
