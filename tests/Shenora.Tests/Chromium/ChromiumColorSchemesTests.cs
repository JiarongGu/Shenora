using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Core.Shell;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The app's colour scheme reaching Chromium: each request context the shell owns gets the setting as it is added, and
/// again on CEF's UI thread after each change. The targets are fakes; what is asserted is that they were USED (D63).
/// </summary>
public class ChromiumColorSchemesTests
{
    private sealed class Ui
    {
        public List<Action> Posted { get; } = [];
        public bool Post(Action work)
        {
            Posted.Add(work);
            return true;
        }
        public void Run()
        {
            foreach (var work in Posted.ToArray()) work();
            Posted.Clear();
        }
    }

    [Fact]
    public void A_target_gets_the_setting_as_it_is_added()
    {
        var setting = new ColorSchemeState(ColorScheme.Dark);
        using var schemes = new ChromiumColorSchemes(setting, new Ui().Post);
        var applied = new List<cef_color_variant_t>();

        using var _ = schemes.Add(applied.Add);

        Assert.Equal([cef_color_variant_t.CEF_COLOR_VARIANT_DARK], applied);
    }

    [Fact]
    public void A_change_reaches_every_target_on_the_UI_thread_and_a_removed_one_no_longer()
    {
        var setting = new ColorSchemeState();
        var ui = new Ui();
        using var schemes = new ChromiumColorSchemes(setting, ui.Post);
        var global = new List<cef_color_variant_t>();
        var session = new List<cef_color_variant_t>();
        var gone = new List<cef_color_variant_t>();
        using var a = schemes.Add(global.Add);
        using var b = schemes.Add(session.Add);
        schemes.Add(gone.Add).Dispose();

        setting.Set(ColorScheme.Light);
        Assert.Equal([cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM], global);   // not yet: posted, not run here
        ui.Run();

        Assert.Equal([cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM, cef_color_variant_t.CEF_COLOR_VARIANT_LIGHT], global);
        Assert.Equal([cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM, cef_color_variant_t.CEF_COLOR_VARIANT_LIGHT], session);
        Assert.Equal([cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM], gone);
    }

    [Fact]
    public void The_latest_setting_is_applied_when_changes_come_faster_than_the_UI_thread()
    {
        var setting = new ColorSchemeState();
        var ui = new Ui();
        using var schemes = new ChromiumColorSchemes(setting, ui.Post);
        var applied = new List<cef_color_variant_t>();
        using var _ = schemes.Add(applied.Add);

        setting.Set(ColorScheme.Light);
        setting.Set(ColorScheme.Dark);
        ui.Run();

        Assert.Equal(cef_color_variant_t.CEF_COLOR_VARIANT_DARK, applied[^1]);
    }

    [Fact]
    public void Once_disposed_a_change_reaches_nothing()
    {
        var setting = new ColorSchemeState();
        var ui = new Ui();
        var schemes = new ChromiumColorSchemes(setting, ui.Post);
        var applied = new List<cef_color_variant_t>();
        schemes.Add(applied.Add);

        schemes.Dispose();
        setting.Set(ColorScheme.Dark);
        ui.Run();

        Assert.Equal([cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM], applied);
    }

    /// <summary>An app's own setting, whose remover fails.</summary>
    private sealed class ThrowingScheme : IColorScheme
    {
        public ColorScheme Scheme => ColorScheme.System;
        public void Set(ColorScheme scheme) { }
        public event Action<ColorScheme>? Changed
        {
            add { }
            remove => throw new InvalidOperationException("an app's setting failed");
        }
    }

    [Fact]
    public void An_app_s_setting_that_fails_to_let_go_does_not_fail_the_dispose()
    {
        var schemes = new ChromiumColorSchemes(new ThrowingScheme(), new Ui().Post);
        schemes.Dispose();   // DI's or the engine's shutdown: an exception here would stop what follows it
    }

    [Theory]
    [InlineData(ColorScheme.System, null, null)]
    [InlineData(ColorScheme.System, true, true)]
    [InlineData(ColorScheme.Light, true, false)]
    [InlineData(ColorScheme.Dark, false, true)]
    public void Dark_is_the_setting_or_else_the_system_s(ColorScheme scheme, bool? systemDark, bool? dark) =>
        Assert.Equal(dark, ChromiumColorSchemes.Dark(scheme, systemDark));
}
