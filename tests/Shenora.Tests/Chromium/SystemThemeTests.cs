using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>The operating system's app theme, which the splash and the painted caption buttons both start from.</summary>
public class SystemThemeTests
{
    [Fact]
    public void It_reads_what_the_registry_says_and_nothing_when_it_says_nothing()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool? expected = key?.GetValue("AppsUseLightTheme") is int light ? light == 0 : null;
        Assert.Equal(expected, SystemTheme.IsDark());
    }
}
