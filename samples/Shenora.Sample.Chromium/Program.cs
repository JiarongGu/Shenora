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
            // The main window opens where it was left: its size, its place, maximized or not.
            WindowState = new WindowStateHostOptions
            {
                Store = sp => new JsonFileWindowStateStore(
                    Path.Combine(sp.GetRequiredService<ShenoraPaths>().DataArea("config"), "window-state.json")),
            },
            // One instance per install (the default): a later launch brings this window forward, and its arguments reach
            // the page, as a file opened with the app would.
            SingleInstance = new SingleInstanceHostOptions
            {
                OnActivated = (_, launch) => events?.Emit(Module, "LAUNCHED_AGAIN", new { launch.Arguments }),
            },
            // A splash the OS draws from the moment the app runs, so the window is there while Chromium is still starting.
            // Its boot work stands in for an app's own (opening a database, warming a cache) and reports as it goes; the
            // last line comes from an event, as any module of the app could send one. It lifts once the boot work is done
            // and the page has said it is ready.
            Splash = new ChromiumSplashOptions
            {
                Component = context =>
                {
                    var status = context.State("Starting…");
                    var progress = context.State<double?>(null);
                    context.OnShown(async ct =>
                    {
                        string[] steps = ["Opening the library…", "Warming the cache…", "Loading the interface…"];
                        for (var i = 0; i < steps.Length; i++)
                        {
                            status.Value = steps[i];
                            progress.Value = (i + 1) / (double)(steps.Length + 1);
                            await Task.Delay(250, ct);
                        }
                    });
                    context.Subscribe(Module, "BOOT", message => status.Value = message.Payload as string ?? status.Value);
                    var dim = System.Drawing.Color.FromArgb(0x9a, 0x9a, 0x9a);
                    return () => new SplashLayer
                    {
                        Children =
                        [
                            new SplashStack
                            {
                                Spacing = 14,
                                Children =
                                [
                                    new SplashText("Shenora Chromium Sample") { FontSize = 22, Bold = true },
                                    new SplashText(status.Value) { FontSize = 13, Color = dim },
                                    new SplashProgress { Value = progress.Value, Width = 260, Height = 3 },
                                ],
                            },
                            new SplashText("Drawn by the OS before Chromium starts")
                            {
                                FontSize = 11, Color = dim, Margin = 16,
                                HorizontalAlign = SplashAlign.End, VerticalAlign = SplashAlign.End,
                            },
                        ],
                    };
                },
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
            events.Emit(Module, "BOOT", "Starting the app's services…");
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
