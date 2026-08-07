using Shenora;
using Shenora.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Tests.WinForms;

/// <summary>The native-services composition added by <c>UseWindows</c> (P4.3).</summary>
public class NativeServicesRegistrationTests
{
    private static ShenoraApplicationBuilder Builder() =>
        ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.NativeServices",
            BaseDirectory = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n"),
            GetEnvironmentVariable = _ => null,
        });

    /// <summary>
    /// 🔴 <b>The shell's dialog ROUTE, not just its service — and this had no test until 2026-08-08.</b>
    /// <c>UseWindows</c> registers <see cref="IFileDialogs"/> AND the facade that exposes it to the page,
    /// because a capability's route belongs with the platform that can satisfy it (D65). The media module
    /// got this proof when it was written; the dialogs one did not, which is the same D63 shape — a
    /// default with no test is indistinguishable from no default.
    /// <para>
    /// ⚠ <b>It drives a FAKE <see cref="IFileDialogs"/> rather than probing for an error code, and the
    /// first attempt at the latter is why.</b> A bogus route looked like the obvious probe — "did the
    /// module answer?" — but <c>BaseFacade.UnknownType</c> and the dispatcher's terminal BOTH return
    /// <c>NO_HANDLER</c> with identical <c>module</c>/<c>type</c> parameters, so "no such module" and
    /// "no such route in this module" are indistinguishable on the wire. Registering a fake before
    /// <c>UseWindows</c> (TryAdd means the app's wins) proves the whole chain instead: registration →
    /// mapping → facade → the shell's service.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UseWindows_registers_the_dialog_ROUTE_so_a_page_can_reach_it()
    {
        var builder = Builder();
        var dialogs = new RecordingDialogs();
        builder.Services.AddSingleton<IFileDialogs>(dialogs);
        builder.UseWindows(new WindowsHostOptions { MainForm = _ => new Form() });
        using var app = builder.Build();

        var response = await app.Services.GetRequiredService<Shenora.Ipc.IMessageDispatcher>()
            .DispatchAsync(new Shenora.Ipc.IpcRequest
            {
                Id = "r1",
                Module = Shenora.Ipc.FileDialogFacade.Module,
                Type = "OPEN_FILE",
            }, CancellationToken.None);

        Assert.True(response.Success, response.Error?.Code);
        Assert.True(dialogs.Opened, "the page's request never reached the shell's IFileDialogs");
    }

    private sealed class RecordingDialogs : IFileDialogs
    {
        public bool Opened { get; private set; }

        public Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null)
        {
            Opened = true;
            return Task.FromResult(FileDialogResult.Cancelled());
        }

        public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null) =>
            Task.FromResult(FileDialogResult.Cancelled());
        public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null) =>
            Task.FromResult(FileDialogResult.Cancelled());
        public Task<FileDialogResult> SaveAsync(SaveFileOptions? options, Func<Stream, CancellationToken, Task> write,
            CancellationToken cancellationToken = default) => Task.FromResult(FileDialogResult.Cancelled());
        public Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);
    }

    [Fact]
    public void UseWindows_registers_the_native_services()
    {
        var builder = Builder();
        builder.UseWindows(new WindowsHostOptions { MainForm = _ => new Form() });
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
        builder.UseWindows(new WindowsHostOptions { MainForm = _ => new Form() });
        using var app = builder.Build();

        Assert.Same(custom, app.Services.GetRequiredService<IFormInteraction>());
    }

    [Fact]
    public void The_runner_registers_the_main_form_on_form_interaction()
    {
        var root = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n");
        Form? created = null;
        var builder = Builder();
        builder.UseWindows(new WindowsHostOptions
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
