using Shenora.Core;
using Shenora.Ipc;
using Shenora.WebView2;
using Shenora.WinForms;
using Microsoft.Extensions.DependencyInjection;

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

        // The IPC pipeline — facades live in DI, the dispatcher maps each at composition time
        // (error handler FIRST so it wraps everything after it).
        builder.Services.AddSingleton<IModuleFacade, SampleFacade>();
        builder.Services.AddSingleton<IMessageDispatcher>(sp =>
        {
            var dispatcher = new MessageDispatcher().UseErrorHandler();
            foreach (var facade in sp.GetServices<IModuleFacade>()) dispatcher.MapModule(facade);
            return dispatcher;
        });

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
