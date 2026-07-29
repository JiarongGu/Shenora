using Shenora.WinForms;

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
    public void Defaults_are_the_family_standard()
    {
        var opt = new WinFormsBootstrapOptions();
        Assert.Equal(HighDpiMode.PerMonitorV2, opt.HighDpiMode);
        Assert.True(opt.ShowCrashDialog);
        Assert.True(opt.ObserveUnobservedTaskExceptions);
    }
}
