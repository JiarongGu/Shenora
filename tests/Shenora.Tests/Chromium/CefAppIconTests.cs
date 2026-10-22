using System.Runtime.InteropServices;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A Chromium window's class icon is Chromium's logo unless CEF is given an icon id in the running exe
/// (<c>chrome_app_icon_id</c>), so the taskbar and Alt+Tab showed Chromium's icon for an app with an
/// <c>ApplicationIcon</c> (reported by an adopter). The shell passes the exe's first icon group. Measured on the
/// Chromium sample: the class icon went from Chromium's blue to the app's colour, at both sizes.
/// </summary>
public class CefAppIconTests
{
    private const uint AsDataFile = 0x2, AsImageResource = 0x20;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(nint module);

    private static T WithModule<T>(string name, Func<nint, T> use)
    {
        var module = LoadLibraryExW(Path.Combine(Environment.SystemDirectory, name), 0, AsDataFile | AsImageResource);
        Assert.NotEqual(0, module);
        try { return use(module); }
        finally { FreeLibrary(module); }
    }

    private delegate int EnumResourceName(nint module, nint type, nint name, nint param);

    [DllImport("kernel32.dll")]
    private static extern int EnumResourceNamesW(nint module, nint type, EnumResourceName callback, nint param);

    [Fact]
    public void An_exe_with_an_icon_group_gives_its_FIRST_integer_id()
    {
        var (id, all) = WithModule("shell32.dll", m =>
        {
            var ids = new List<nint>();
            EnumResourceNamesW(m, 14, (_, _, name, _) => { ids.Add(name); return 1; }, 0);
            return (CefStartup.AppIconId(m, @"C:\apps\demo\MyApp.exe"), ids);
        });

        Assert.True(all.Count > 1, "the fixture needs more than one icon group to tell first from any");
        Assert.Equal(all.First(name => name is > 0 and < 0x10000), (nint)id);
    }

    /// <summary>An app with no <c>ApplicationIcon</c>: its launcher carries no icon group, and CEF keeps Chromium's.</summary>
    [Fact]
    public void A_module_without_an_icon_group_gives_none() =>
        Assert.Equal(0, WithModule("ntdll.dll", m => CefStartup.AppIconId(m, @"C:\apps\demo\MyApp.exe")));

    /// <summary>Under <c>dotnet &lt;App&gt;.App.dll</c> the running exe is the .NET host, whose icon is not the app's.</summary>
    [Fact]
    public void Run_by_dotnet_there_is_no_app_icon() =>
        Assert.Equal(0, WithModule("shell32.dll", m => CefStartup.AppIconId(m, @"C:\Program Files\dotnet\dotnet.exe")));
}
