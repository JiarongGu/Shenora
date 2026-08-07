using Shenora;
using Shenora.IO;
using Shenora.Ipc;
using Shenora.Windows;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Missions;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The reference composition — every Shenora piece in its intended place. This app doubles as the
/// e2e subject for the desktop verification loop (`dev.mjs sample [--dev]` / `shot` / `wgc` /
/// `click`), proving the paths the unit suite deliberately leaves to a real browser: prewarm,
/// EnsureCoreWebView2Async, virtual-host serving, script injection, the message pump.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            Args = args,
            ApplicationName = "Shenora Sample",
        });

        // ONE WebViewEnvironmentOptions instance shared by the prewarm hook and the window's
        // WebViewHost — same options + user-data folder ⇒ the prewarmed environment is the one
        // the window awaits (paying only the not-yet-overlapped remainder).
        builder.Services.AddSingleton(sp =>
        {
            var environment = sp.GetRequiredService<ShenoraEnvironment>();
            var paths = sp.GetRequiredService<ShenoraPaths>();
            return new WebViewEnvironmentOptions
            {
                UserDataFolder = paths.DataArea("webview2"),
                IsDevelopment = environment.IsDevelopment,
                Log = Console.WriteLine, // sample runs from a console-visible dev loop
                CustomSchemes =
                [
                    new WebViewCustomScheme
                    {
                        Name = RangeSchemeProbe.Scheme,
                        AllowedOrigins = ["https://sample.local", "http://localhost:3900"],
                    },
                ],
            };
        });

        builder.Services.AddSingleton<IWebViewResourceProvider>(sp =>
        {
            var environment = sp.GetRequiredService<ShenoraEnvironment>();
            var paths = sp.GetRequiredService<ShenoraPaths>();
            return new EmbeddedResourceProvider(new EmbeddedResourceProviderOptions
            {
                Assembly = typeof(Program).Assembly,
                ResourcePrefix = "Shenora.Sample.Desktop.wwwroot",
                FileFallbackDirectory = Path.Combine(paths.RootDir, "wwwroot"),
                PreferFiles = environment.IsDevelopment,
                Log = Console.WriteLine,
            });
        });

        builder.Services.AddSingleton(sp => new WebViewHostOptions
        {
            Environment = sp.GetRequiredService<WebViewEnvironmentOptions>(),
            // Must match samples/Shenora.Sample.Web/vite.config.ts (unique per app — family rule).
            DevUrl = "http://localhost:3900",
            VirtualHost = "sample.local",
            ResourceProvider = sp.GetRequiredService<IWebViewResourceProvider>(),
            DeferredSchemes = [RangeSchemeProbe.CreateScheme()],
            BackgroundColor = MainForm.Background, // the no-white-flash contract: form = webview = splash
            InjectedGlobals = new Dictionary<string, object?>
            {
                // Serialized camelCase: the page reads { name, version }.
                ["__SHENORA_SAMPLE__"] = new
                {
                    Name = "Shenora.Sample.Desktop",
                    Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                },
            },
        });

        // The IPC pipeline — facades live in DI; AddMessageDispatcher composes the family order
        // (error handler → app middleware → registered facades). The window-facing facades
        // (WINDOW commands, DROP_ZONE) map later, in MainForm, once the form exists.
        builder.Services.AddSingleton<Shenora.Windows.SecondaryWindows>();
        // Opt-in (D21): SampleFacade's SLOW route uses ctx.Run, so the sample pays for the registry
        // it demonstrates rather than getting it for free — the same bar every consumer faces.
        builder.Services.AddShenoraOperations();

        // The mission scheduler — a plain object, registered like any other singleton (Shenora
        // ships no DI extension for it, and it needs none). Composition, not framework: the app
        // chooses the scopes, the capacity, and how execution reports itself.
        builder.Services.AddSingleton<IMissionScheduler>(sp => new MissionScheduler(new MissionSchedulerOptions
        {
            // Explicit rather than the clamp(cores-1,1,4) default, so the sample behaves the same on
            // every machine — the same reason the concurrency tests pass one.
            GlobalLaneCapacity = 4,
            Scopes = [PathClaims.Scope],
            // Execution reports through the operations registry via ONE observer written in the app
            // (Shenora must never learn what an operation is — D19/D20). This is the whole cost
            // of the pairing that docs/ADOPTION.md describes.
            Observers = [new Shenora.Sample.Logic.MissionOperationObserver(
                sp.GetRequiredService<IOperationRegistry>(), Shenora.Sample.Logic.PortableSampleFacade.Module)],
            Log = Console.WriteLine,
        }));
        // The file-update queue: independent of the scheduler, and registered the same plain way.
        // Missions compute in parallel and hand their finished change sets here to land one at a time.
        builder.Services.AddSingleton<IFileUpdateQueue>(_ =>
            new FileUpdateQueue(new FileUpdateQueueOptions { Log = Console.WriteLine }));

        // Lane capacities are configured ONCE, at startup, by name — an unknown name is created at
        // the default capacity rather than rejected, so a typo silently costs the budget you meant.
        builder.OnStarting(app =>
            app.Services.GetRequiredService<IMissionScheduler>().Lane(Shenora.Sample.Logic.MissionLanes.DemoIo).Capacity = 2);
        // The kit's own dialog routes, so the PAGE can open a picker without this sample writing a route for
// it — which is what it used to do, in two samples, identically. Opt-in like every other kit cluster;
// it needs the IFileDialogs that UseWinForms registered above.
builder.Services.AddShenoraFileDialogs();

builder.Services.AddModuleFacade<SampleFacade>();
        // The app's PORTABLE logic, from a net10.0 assembly that cannot see Windows (D20/H4.3). It
        // resolves the same implementations through their platform-neutral contracts.
        builder.Services.AddModuleFacade<Shenora.Sample.Logic.PortableSampleFacade>();
        builder.Services.AddMessageDispatcher();

        builder.Services.AddSingleton<MainForm>();

        // Prewarm runs as a starting hook — after the single-instance gate (it takes the
        // user-data OS lock), overlapping form creation.
        builder.PrewarmWebView2(app => app.Services.GetRequiredService<WebViewEnvironmentOptions>());
        builder.OnStarting(app =>
            (app.Services.GetRequiredService<IWebViewResourceProvider>() as EmbeddedResourceProvider)?.BeginWarmup());

        builder.UseWinForms(new WinFormsHostOptions
        {
            MainForm = sp => sp.GetRequiredService<MainForm>(),
            WindowState = new WindowStateHostOptions
            {
                Store = sp => new JsonFileWindowStateStore(
                    Path.Combine(sp.GetRequiredService<ShenoraPaths>().DataArea("config"), "window-state.json")),
            },
        });

        using var app = builder.Build();
        app.Run();
    }
}
