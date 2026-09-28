using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// An app started without CEF's launcher (<c>dotnet &lt;App&gt;.App.dll</c>) has CEF start its subprocesses through the
/// launcher laid out beside it: measured, without it every subprocess was <c>dotnet.exe --type=…</c> with no app to
/// run and exited at once, until the app crashed.
/// </summary>
public class CefStartupTests
{
    private const string Dir = @"C:\apps\demo";
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";

    [Fact]
    public void Run_by_dotnet_the_launcher_is_the_app_assembly_name_without_its_App_suffix()
    {
        var asked = new List<string>();

        var launcher = CefStartup.LauncherBesideApp("MyApp.App", Dir, Dotnet, path => { asked.Add(path); return true; });

        Assert.Equal(Path.Combine(Dir, "MyApp.exe"), launcher);
        Assert.Equal([Path.Combine(Dir, "MyApp.exe")], asked);
    }

    // An app running an exe of its own is one CEF can start for each subprocess; an unrelated MyApp.exe beside it (a
    // referenced project's apphost) must not be launched as every renderer instead.
    [Theory]
    [InlineData(@"C:\apps\demo\MyApp.App.exe")]
    [InlineData(@"C:\apps\demo\MyApp.exe")]   // the launcher itself, which the shim normally answers first
    [InlineData(@"c:\APPS\demo\other.exe")]
    public void An_app_running_an_exe_in_its_own_folder_is_left_to_CEFs_default(string processPath) =>
        Assert.Null(CefStartup.LauncherBesideApp("MyApp.App", Dir + @"\", processPath, _ => true));

    [Theory]
    [InlineData("MyApp")]       // not the layout's `.App` name
    [InlineData(".App")]        // nothing before the suffix
    [InlineData(null)]          // no entry assembly (a test host, a native host)
    public void An_app_not_laid_out_by_the_build_has_no_launcher(string? entry) =>
        Assert.Null(CefStartup.LauncherBesideApp(entry, Dir, Dotnet, _ => true));

    [Fact]
    public void A_launcher_that_is_not_there_is_not_used() =>
        Assert.Null(CefStartup.LauncherBesideApp("MyApp.App", Dir, Dotnet, _ => false));
}
