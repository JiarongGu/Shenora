using Shenora;
using Shenora.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Shenora.Tests.WebView2;

public class WebView2BuilderExtensionsTests
{
    [Fact]
    public void PrewarmWebView2_registers_a_deferred_startup_hook()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.App",
            BaseDirectory = @"C:\MyApp",
            GetEnvironmentVariable = _ => null,
        });

        var optionsBuilt = false;
        builder.PrewarmWebView2(app =>
        {
            optionsBuilt = true;
            return new WebViewEnvironmentOptions { UserDataFolder = app.Paths.DataDir };
        });

        using var app = builder.Build();
        // Registered as a lifecycle hook (runs post-gate — see the extension's doc), and lazy:
        // nothing evaluated at composition time. The hook is deliberately NOT invoked here —
        // invoking would spawn a real browser process; the sample-app e2e covers that.
        Assert.Single(app.Services.GetServices<IShenoraLifecycleHook>());
        Assert.False(optionsBuilt);
    }
}
