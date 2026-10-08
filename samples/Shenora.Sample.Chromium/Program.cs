using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Core.WebView;
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
            // Packaged: the page built into the app's assembly (wwwroot, embedded by the csproj). In development
            // (DOTNET_ENVIRONMENT=Development, or a .dev file beside the app) the page comes from the dev server
            // instead, with hot reload.
            ResourceProvider = new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = typeof(Program).Assembly,
                ResourcePrefix = "Shenora.Sample.Chromium.wwwroot",
            }),
            DevUrl = "http://localhost:3901",   // samples/Shenora.Sample.Web: npm run dev:chromium
            Window = new ChromiumWindowOptions
            {
                Title = "Shenora Chromium Sample",
                Width = 1000,
                Height = 760,
                Path = "chromium.html",
                BackgroundColor = System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e),
                // The page draws its own title bar (App.tsx), with drag regions and buttons.
                FramelessChrome = true,
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
            // The page is dark only, so the app is too: Chromium's own UI, the page's prefers-color-scheme and the
            // window's first frames are dark whatever the OS says. An app offering the choice passes its saved one.
            ColorScheme = ColorScheme.Dark,
            // A splash in the window's render area while the page loads, drawn by the OS rather than Chromium: a skeleton of
            // the page's first screen in its own colours, so the lift changes rows into content without moving anything.
            // The frame stays the window's: on this frameless window the splash's title strip (the page's title bar's
            // height) drags it and holds its buttons until the page's own title bar takes over. Its boot work stands in
            // for an app's own (opening a database, warming a cache) and reports as it goes; the last line comes from an
            // event, as any module of the app could send one. It lifts once the boot work is done and the page says it has
            // painted (closeSplash() in App.tsx), not at its handshake, which comes before its first paint.
            // Launched with --splash-card, a card shows from the app's first moments until the window exists.
            Splash = new ChromiumSplashOptions
            {
                HoldUntilClosed = true,
                Card = args.Contains("--splash-card") ? new SplashCardOptions { Width = 420, Height = 260 } : null,
                TitleBar = new SplashTitleBarOptions { Height = 35 },   // the page's title bar: 2.2rem
                Component = context =>
                {
                    var status = context.State("Starting…");
                    var progress = context.State<double?>(0);
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
                    // The page's colours (chromium.html, App.tsx): its text, its controls, its quiet grey.
                    var text = System.Drawing.Color.FromArgb(0xe8, 0xe8, 0xe8);
                    var block = System.Drawing.Color.FromArgb(0x2c, 0x2c, 0x2c);
                    var quiet = System.Drawing.Color.FromArgb(0x9a, 0x9a, 0x9a);
                    SplashElement Row(double width) => new SplashLayer { Width = width, Height = 14, Background = block, HorizontalAlign = SplashAlign.Start };
                    return () => context.Surface == SplashSurface.Card
                        ? new SplashStack
                        {
                            Spacing = 12,
                            Children =
                            [
                                new SplashText("神阙 Shenora") { FontSize = 26, Color = text },
                                new SplashText(status.Value) { FontSize = 12, Color = quiet },
                                new SplashProgress { Value = progress.Value, Width = 240, Height = 3 },
                            ],
                        }
                        : new SplashLayer
                        {
                            Children =
                            [
                                new SplashProgress { Value = progress.Value, Height = 2, VerticalAlign = SplashAlign.Start, HorizontalAlign = SplashAlign.Stretch },
                                // The page's column: 44rem wide, 1.5rem down, its heading, its rows and its drop box.
                                new SplashStack
                                {
                                    Width = 704, VerticalAlign = SplashAlign.Start, Margin = new SplashInsets(0, 24, 0, 0), Spacing = 12,
                                    Children =
                                    [
                                        new SplashText("神阙 Shenora") { FontSize = 32, Color = text, HorizontalAlign = SplashAlign.Start },
                                        Row(420), Row(380), Row(460), Row(300),
                                        new SplashLayer { Height = 56, Background = block },
                                        Row(340), Row(400),
                                        new SplashText(status.Value) { FontSize = 12, Color = quiet, HorizontalAlign = SplashAlign.Start, Margin = new SplashInsets(0, 12, 0, 0) },
                                    ],
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
