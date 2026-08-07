using Shenora;
using Shenora.Windows;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Engine.Files;
using Shenora.Engine.Missions;
using Shenora.Core.Ipc;

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

        // 🔴 WHAT IS NOT HERE IS THE POINT (D64/D65). The IPC dispatcher, the mission scheduler, the
        // file-update queue, the media player, the operations registry and the kit's own route modules
        // are ALL registered by `Build()` — or, where they need a platform, by `UseWindows` above. This
        // sample used to hand-construct four of them inside `AddSingleton` lambdas, with a comment
        // claiming the kit "ships no DI extension for it, and it needs none". **That block is the
        // acceptance test for the whole rewire, and its deletion is the result.**
        //
        // What remains below is the app's OWN composition: its windows, its facades, and the two places
        // it genuinely disagrees with a default.
        builder.Services.AddSingleton<Shenora.Windows.SecondaryWindows>();

        // The scheduler is already registered; this CONFIGURES it (D64 — `Use…` no longer enables).
        builder.UseMissions(options =>
        {
            // Explicit rather than the clamp(cores-1,1,4) default, so the sample behaves the same on
            // every machine — the same reason the concurrency tests pass one.
            options.GlobalLaneCapacity = 4;
            options.Scopes = [PathClaims.Scope];
            options.Log = Console.WriteLine;
        });
        // ⚠ The observer is attached in a STARTING hook rather than in the options above, because it
        // needs `IOperationRegistry` — a service, not a value — and the options object is built before
        // any provider exists. Execution reports through ONE observer written in the APP (Shenora must
        // never learn what an operation is — D19/D20); that pairing is still the app's whole cost.
        builder.OnStarting(app =>
        {
            var scheduler = app.Services.GetRequiredService<IMissionScheduler>();
            app.Services.GetRequiredService<MissionSchedulerOptions>().Observers =
                [new Shenora.Sample.Logic.MissionOperationObserver(
                    app.Services.GetRequiredService<IOperationRegistry>(),
                    Shenora.Sample.Logic.PortableSampleModule.Module)];
            // Lane capacities are configured ONCE, at startup, by name — an unknown name is created at
            // the default capacity rather than rejected, so a typo silently costs the budget you meant.
            scheduler.Lane(Shenora.Sample.Logic.MissionLanes.DemoIo).Capacity = 2;
        });

        builder.Services.AddIpcModule<SampleModule>();
        // The app's PORTABLE logic, from a net10.0 assembly that cannot see Windows (D20/H4.3). It
        // resolves the same implementations through their platform-neutral contracts.
        builder.Services.AddIpcModule<Shenora.Sample.Logic.PortableSampleModule>();

        builder.Services.AddSingleton<MainForm>();

        // Prewarm runs as a starting hook — after the single-instance gate (it takes the
        // user-data OS lock), overlapping form creation.
        builder.PrewarmWebView2(app => app.Services.GetRequiredService<WebViewEnvironmentOptions>());
        builder.OnStarting(app =>
            (app.Services.GetRequiredService<IWebViewResourceProvider>() as EmbeddedResourceProvider)?.BeginWarmup());

        builder.UseWindows(new WindowsHostOptions
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
