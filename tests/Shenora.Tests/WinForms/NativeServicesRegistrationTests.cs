using Shenora.Core;
using Shenora.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Tests.WinForms;

/// <summary>The native-services composition added by <c>UseWinForms</c> (P4.3).</summary>
public class NativeServicesRegistrationTests
{
    private static ShenoraApplicationBuilder Builder() =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.NativeServices",
            BaseDirectory = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n"),
            GetEnvironmentVariable = _ => null,
        });

    [Fact]
    public void UseWinForms_registers_the_native_services()
    {
        var builder = Builder();
        builder.UseWinForms(new WinFormsHostOptions { MainForm = _ => new Form() });
        using var app = builder.Build();

        Assert.IsType<FormInteraction>(app.Services.GetRequiredService<IFormInteraction>());
        Assert.IsType<ShellLauncher>(app.Services.GetRequiredService<IShellLauncher>());
        Assert.IsType<ClipboardService>(app.Services.GetRequiredService<IClipboardService>());
        Assert.IsType<FileDialogs>(app.Services.GetRequiredService<IFileDialogs>());
    }

    [Fact]
    public void App_registrations_win_over_the_defaults()
    {
        var builder = Builder();
        var custom = new FormInteraction();
        builder.Services.AddSingleton<IFormInteraction>(custom);
        builder.UseWinForms(new WinFormsHostOptions { MainForm = _ => new Form() });
        using var app = builder.Build();

        Assert.Same(custom, app.Services.GetRequiredService<IFormInteraction>());
    }

    [Fact]
    public void The_runner_registers_the_main_form_on_form_interaction()
    {
        var root = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n");
        Form? created = null;
        var builder = Builder();
        builder.UseWinForms(new WinFormsHostOptions
        {
            MainForm = _ => created = new Form(),
            SkipProcessInit = true,
            MessageLoop = _ => { },
            SingleInstance = new SingleInstanceHostOptions { Scope = root },
        });
        using var app = builder.Build();
        var interaction = app.Services.GetRequiredService<IFormInteraction>();

        app.Run();

        Assert.NotNull(created);
        Assert.Same(created, interaction.GetMainForm()); // disposed by now, but identity holds
    }
}
