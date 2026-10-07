using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Shenora.Core.Shell;
using Shenora.Modules.Platform;
using Shenora.Windows;

namespace Shenora.Tests.WebView2;

/// <summary>
/// The app's colour scheme on the WebView2 shell: <c>UseWindows</c> registers it from its options, and a
/// <see cref="WebViewHost"/> given it follows it until its control goes. Applying it to a live profile needs a browser
/// process, which these tests never start; the desktop sample proves that half.
/// </summary>
public class WebViewColorSchemeTests
{
    /// <summary>Counts who listens, so a test can see the host take the setting and let it go.</summary>
    private sealed class CountingScheme : IColorScheme
    {
        private Action<ColorScheme>? _changed;
        public int Listeners { get; private set; }
        public ColorScheme Scheme { get; private set; }
        public void Set(ColorScheme scheme)
        {
            Scheme = scheme;
            _changed?.Invoke(scheme);
        }
        public event Action<ColorScheme>? Changed
        {
            add { _changed += value; Listeners++; }
            remove { _changed -= value; Listeners--; }
        }
    }

    [Theory]
    [InlineData(ColorScheme.System, CoreWebView2PreferredColorScheme.Auto)]
    [InlineData(ColorScheme.Light, CoreWebView2PreferredColorScheme.Light)]
    [InlineData(ColorScheme.Dark, CoreWebView2PreferredColorScheme.Dark)]
    public void The_setting_maps_to_the_profile_s_preference(ColorScheme scheme, CoreWebView2PreferredColorScheme preferred) =>
        Assert.Equal(preferred, WebViewHost.Preferred(scheme));

    [Fact]
    public void A_host_follows_the_setting_until_its_control_goes()
    {
        var scheme = new CountingScheme();
        var webView = new Microsoft.Web.WebView2.WinForms.WebView2();
        _ = new WebViewHost(webView, new WebViewHostOptions
        {
            Environment = new WebViewEnvironmentOptions { UserDataFolder = Path.GetTempPath() },
            ColorScheme = scheme,
        });
        Assert.Equal(1, scheme.Listeners);

        scheme.Set(ColorScheme.Dark);   // before the WebView exists: nothing to apply yet, and nothing thrown
        webView.Dispose();

        // The setting outlives every window: a host still listening would keep its disposed control alive.
        Assert.Equal(0, scheme.Listeners);
    }

    [Fact]
    public void UseWindows_registers_the_setting_seeded_from_its_options()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Shenora.Tests.ColorScheme",
            BaseDirectory = @"C:\ShenoraTests\" + Guid.NewGuid().ToString("n"),
            GetEnvironmentVariable = _ => null,
        });
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = _ => new Form(),
            SkipProcessInit = true,
            MessageLoop = _ => { },
            SingleInstance = null,
            ColorScheme = ColorScheme.Light,
        });
        using var app = builder.Build();

        var setting = app.Services.GetRequiredService<IColorScheme>();
        Assert.IsType<ColorSchemeState>(setting);
        Assert.Equal(ColorScheme.Light, setting.Scheme);
    }
}
