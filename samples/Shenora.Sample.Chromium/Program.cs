using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Sample.Logic;

namespace Shenora.Sample.Chromium;

/// <summary>
/// The Chromium shell on its own, which is how an app runs on macOS and Linux, and on Windows too. Everything the
/// page does goes through the app's portable logic (<see cref="PortableSampleModule"/>, the same module the Windows
/// and MAUI samples run) or the kit's own routes, so nothing here is written for one OS.
/// </summary>
internal static class Program
{
    /// <summary>The module this app's own host-side events arrive under.</summary>
    private const string Module = "SAMPLE_CHROMIUM";

    [STAThread]
    private static int Main(string[] args)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            Args = args,
            ApplicationName = "Shenora Chromium Sample",
        });

        IEventBus? events = null;
        builder.UseChromium(new ChromiumHostOptions
        {
            // Packaged: the page built into wwwroot beside the app. In development (DOTNET_ENVIRONMENT=Development,
            // or a .dev file beside the app) the page comes from the dev server instead, with hot reload.
            ContentRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            DevUrl = "http://localhost:3901",   // samples/Shenora.Sample.Web: npm run dev:chromium
            Window = new ChromiumWindowOptions
            {
                Title = "Shenora Chromium Sample",
                Width = 1000,
                Height = 760,
                Path = "chromium.html",
                BackgroundColor = System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e),
            },
            // What the page may offer. Every name is something this app composed: the page reads the list from the
            // ready handshake and hides what is not there, instead of sniffing the OS.
            Shell = new ShellInfo
            {
                Name = "chromium",
                Capabilities =
                [
                    ShellCapability.WindowChrome, ShellCapability.DropZones,
                    ShellCapability.FilePicker, ShellCapability.FolderPicker, ShellCapability.SavePicker,
                    ShellCapability.ClipboardFiles, ShellCapability.Tray,
                ],
            },
            // The tray reopens the window, and one item of the app's own tells the page it was clicked: a native
            // event reaching React through the event bus. CloseToTray off, so closing the window ends the app.
            Tray = new ChromiumTrayOptions
            {
                CloseToTray = false,
                MenuItems = () =>
                [
                    new ChromiumTrayMenuItem("Say hello to the page", () =>
                        events?.Emit(Module, "TRAY_HELLO", new { At = DateTime.Now.ToString("T") })),
                ],
            },
        });

        // The portable logic, and what it needs: the mission scheduler it queues work on, observed so the page can
        // watch the missions run.
        builder.UseMissions(options =>
        {
            // Explicit rather than the clamp(cores-1,1,4) default, so the demo overlaps the same on every machine.
            options.GlobalLaneCapacity = 4;
            options.Scopes = [PathClaims.Scope];
        });
        builder.OnStarting(app =>
        {
            events = app.Services.GetRequiredService<IEventBus>();
            app.Services.GetRequiredService<MissionSchedulerOptions>().Observers =
                [new MissionEventPublisher(events, PortableSampleModule.Module)];
            app.Services.GetRequiredService<IMissionScheduler>().Lane(MissionLanes.DemoIo).Capacity = 2;
        });
        builder.Services.AddIpcModule<PortableSampleModule>();

        using var app = builder.Build();
        app.Run();
        return 0;
    }
}
