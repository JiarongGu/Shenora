using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;
using Shenora.Modules.Platform;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell registers the app's colour scheme, seeded from its options, and applies the one the container
/// holds: an app's own registration wins over the options, and is the one Chromium's contexts then follow.
/// </summary>
public class ChromiumColorSchemeCompositionTests
{
    private static ShenoraApplication Build(TempDir root, ChromiumHostOptions options, IColorScheme? own = null)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "Colour scheme test",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Root },
        });
        if (own is not null) builder.Services.AddSingleton(own);
        builder.UseChromium(options);
        return builder.Build();
    }

    [Fact]
    public void The_options_seed_the_setting_the_shell_applies()
    {
        using var root = TempDir.Create();
        using var app = Build(root, new ChromiumHostOptions { ColorScheme = ColorScheme.Dark });

        var setting = app.Services.GetRequiredService<IColorScheme>();
        Assert.IsType<ColorSchemeState>(setting);
        Assert.Equal(ColorScheme.Dark, setting.Scheme);
        Assert.Equal(ColorScheme.Dark, app.Services.GetRequiredService<ChromiumColorSchemes>().Scheme);
    }

    [Fact]
    public void An_app_s_own_setting_wins_and_is_the_one_applied()
    {
        using var root = TempDir.Create();
        var own = new ColorSchemeState(ColorScheme.Light);
        using var app = Build(root, new ChromiumHostOptions { ColorScheme = ColorScheme.Dark }, own);

        Assert.Same(own, app.Services.GetRequiredService<IColorScheme>());
        var schemes = app.Services.GetRequiredService<ChromiumColorSchemes>();
        Assert.Equal(ColorScheme.Light, schemes.Scheme);
        own.Set(ColorScheme.System);
        Assert.Equal(ColorScheme.System, schemes.Scheme);
    }
}
