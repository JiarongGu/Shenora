using Microsoft.Extensions.DependencyInjection;
using Shenora;
using Shenora.Chromium;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The Chromium engine in a WinForms app, where it can go wrong without CEF: the test output has no CEF runtime,
/// which is exactly an app that forgot the package whose build lays it out. The working path is proven against a
/// real CEF in a WinForms probe (D83).
/// </summary>
public class ChromiumViewTests
{
    private static ShenoraApplicationBuilder Builder() =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.Chromium",
            BaseDirectory = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n"),
            GetEnvironmentVariable = _ => null,
        });

    [Fact]
    public void A_view_whose_engine_is_not_running_opens_nothing_and_does_not_throw_from_handle_creation()
    {
        Sta.Run(() =>
        {
            using var view = new ChromiumView(new ChromiumEngine());

            view.CreateControl();   // WinForms answers a throw here with a blocking dialog

            Assert.True(view.IsHandleCreated);
            Assert.Null(view.Browser);
        });
    }

    [Fact]
    public void UseChromiumEngine_registers_one_engine_in_either_order_with_UseWindows()
    {
        var builder = Builder();
        builder.UseChromiumEngine();
        builder.UseWindows(new WindowsHostOptions { MainForm = _ => new Form() });
        builder.UseChromiumEngine();
        using var app = builder.Build();

        Assert.Single(app.Services.GetServices<ChromiumEngine>());
    }

    [Fact]
    public void An_app_with_the_engine_but_no_CEF_stops_before_building_its_form_naming_the_package()
    {
        var built = false;
        var builder = Builder();
        builder.UseChromiumEngine();
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => { built = true; return new Form(); },
            SkipProcessInit = true,
            MessageLoop = _ => Assert.Fail("the loop must not run without CEF"),
        });
        using var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(app.Run);

        Assert.Contains("Reference the Shenora.Chromium package", error.Message);
        Assert.False(built);
    }
}
