using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Tests.Chromium;

/// <summary>
/// An app that sets <c>ShenoraChromiumStartupScreen</c> gets its description and picture stamped into
/// <c>&lt;App&gt;.exe</c> as RCDATA, where the shim reads them (D103). Through the REAL targets file, on a copy of a real
/// executable, which is what the shim's reader is written against: the names, and one <c>key=value</c> per line.
/// </summary>
public class ChromiumStartupScreenStampTests
{
    private const string ScreenResource = "SHENORA_STARTUP_SCREEN";
    private const string ImageResource = "SHENORA_STARTUP_IMAGE";

    // A 1x1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public void The_description_and_the_picture_are_stamped_into_the_launcher_with_the_defaults_filled_in()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        var launcher = Stamp(work, ["ShenoraChromiumStartupScreen=" + work.Image]);

        Assert.Equal(
            "width=480\nheight=300\nbackground=FF202020\nprogress_bar=0\nprogress_color=FF3B82F6\ncorners=rounded\nmonitor=cursor\ntimeout_ms=20000",
            Text(launcher, ScreenResource));
        Assert.Equal(Png, Resource(launcher, ImageResource));
    }

    [Fact]
    public void Every_option_reaches_the_description_and_a_colour_without_alpha_is_opaque()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        var launcher = Stamp(work,
        [
            "ShenoraChromiumStartupScreen=" + work.Image, "ShenoraChromiumStartupScreenWidth=600", "ShenoraChromiumStartupScreenHeight=320",
            "ShenoraChromiumStartupScreenBackground=#10203040", "ShenoraChromiumStartupScreenProgressBar=True",
            "ShenoraChromiumStartupScreenProgressColor=ff0000", "ShenoraChromiumStartupScreenCorners=SQUARE",
            "ShenoraChromiumStartupScreenMonitor=Primary", "ShenoraChromiumStartupScreenTimeoutSeconds=7",
        ]);

        Assert.Equal(
            "width=600\nheight=320\nbackground=10203040\nprogress_bar=1\nprogress_color=FFFF0000\ncorners=square\nmonitor=primary\ntimeout_ms=7000",
            Text(launcher, ScreenResource));
    }

    // A path in the project is relative to the project's folder, whatever folder the build was started from.
    [Fact]
    public void A_path_relative_to_the_project_is_found_from_any_working_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        var launcher = Stamp(work, ["ShenoraChromiumStartupScreen=screen.png"]);

        Assert.Equal(Png, Resource(launcher, ImageResource));
    }

    [Fact]
    public void Without_the_property_the_launcher_carries_no_screen()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        var launcher = Stamp(work, []);

        Assert.Null(Resource(launcher, ScreenResource));
        Assert.Null(Resource(launcher, ImageResource));
    }

    [Fact]
    public void A_file_that_is_not_a_PNG_fails_the_stamp_naming_the_property()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        File.WriteAllText(work.Image, "not a picture, but long enough to hold a signature");
        var output = Stamp(work, ["ShenoraChromiumStartupScreen=" + work.Image], expectSuccess: false);

        Assert.Contains("ShenoraChromiumStartupScreen names", output);
        Assert.Contains("not a PNG", output);
    }

    // The check runs before the fetch of CEF, so each refusal costs no download.
    [Theory]
    [InlineData("ShenoraChromiumStartupScreen", "no-such.png", "does not exist")]
    [InlineData("ShenoraChromiumStartupScreenWidth", "0", "positive whole number")]
    [InlineData("ShenoraChromiumStartupScreenWidth", "wide", "positive whole number")]
    [InlineData("ShenoraChromiumStartupScreenHeight", "-3", "positive whole number")]
    [InlineData("ShenoraChromiumStartupScreenTimeoutSeconds", "1.5", "positive whole number")]
    [InlineData("ShenoraChromiumStartupScreenBackground", "red", "#RRGGBB or #AARRGGBB")]
    [InlineData("ShenoraChromiumStartupScreenProgressColor", "#12345", "#RRGGBB or #AARRGGBB")]
    [InlineData("ShenoraChromiumStartupScreenProgressBar", "yes", "true or false")]
    [InlineData("ShenoraChromiumStartupScreenCorners", "round", "Rounded or Square")]
    [InlineData("ShenoraChromiumStartupScreenMonitor", "left", "Cursor or Primary")]
    public void A_wrong_option_is_refused_by_name_before_the_fetch(string property, string value, string says)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        var output = Check(work, [property == "ShenoraChromiumStartupScreen" ? "" : "ShenoraChromiumStartupScreen=" + work.Image,
            property + "=" + (property == "ShenoraChromiumStartupScreen" ? Path.Combine(work.Root, value) : value)], expectSuccess: false);

        Assert.Contains(property, output);
        Assert.Contains(says, output);
    }

    [Fact]
    public void Every_option_at_a_valid_value_passes_the_check()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var work = new Scratch();
        Check(work,
        [
            "ShenoraChromiumStartupScreen=" + work.Image, "ShenoraChromiumStartupScreenWidth=600", "ShenoraChromiumStartupScreenHeight=320",
            "ShenoraChromiumStartupScreenBackground=#10203040", "ShenoraChromiumStartupScreenProgressBar=false",
            "ShenoraChromiumStartupScreenProgressColor=#ff0000", "ShenoraChromiumStartupScreenCorners=square",
            "ShenoraChromiumStartupScreenMonitor=primary", "ShenoraChromiumStartupScreenTimeoutSeconds=7",
        ], expectSuccess: true);
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "shenora-screen-" + Guid.NewGuid().ToString("N"));
        public string Image => Path.Combine(Root, "screen.png");
        public string Launcher => Path.Combine(Root, "App.exe");

        public Scratch()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Image, Png);
            File.Copy(Environment.ProcessPath!, Launcher);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    // The stamp task on a copy of a real executable, with the app's own options as MSBuild properties.
    private static string Stamp(Scratch work, string[] properties, bool expectSuccess = true)
    {
        var app = typeof(ChromiumStartupScreenStampTests).Assembly.Location;
        var options = string.Join(' ', Names.Select(n => $"StartupScreen{n}=\"$(ShenoraChromiumStartupScreen{n})\"").Prepend("StartupScreen=\"$(ShenoraChromiumStartupScreen)\""));
        var project = Path.Combine(work.Root, "stamp.proj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{TargetsFile()}" />
              <Target Name="Stamp"><ShenoraChromiumStampLauncher Executable="{work.Launcher}" From="{app}" {options} /></Target>
            </Project>
            """);
        var output = Run(["msbuild", project, "-t:Stamp", .. properties.Where(p => p.Length > 0).Select(p => "-p:" + p), "-nologo", "-noAutoResponse", "-v:minimal"], expectSuccess);
        return expectSuccess ? work.Launcher : output;
    }

    // The task's parameters after StartupScreen, each filled from the property of the same name after ShenoraChromiumStartupScreen.
    private static readonly string[] Names = ["Width", "Height", "Background", "ProgressBar", "ProgressColor", "Corners", "Monitor", "TimeoutSeconds"];

    // _ShenoraChromiumCheck, the target that refuses a build before it downloads CEF.
    private static string Check(Scratch work, string[] properties, bool expectSuccess)
    {
        var shim = Path.Combine(work.Root, "shim.dll");
        File.WriteAllText(shim, "shim");
        var project = Path.Combine(work.Root, "check.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                <AssemblyName>MyApp.App</AssemblyName>
                <OutputType>WinExe</OutputType>
                <ShenoraChromiumShim>{shim}</ShenoraChromiumShim>
              </PropertyGroup>
              <Import Project="{TargetsFile()}" />
            </Project>
            """);
        return Run(["msbuild", project, "-t:_ShenoraChromiumCheck", .. properties.Where(p => p.Length > 0).Select(p => "-p:" + p), "-nologo", "-noAutoResponse", "-v:minimal"], expectSuccess);
    }

    private static string Run(string[] args, bool expectSuccess)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "msbuild did not finish in 2 minutes");
        Assert.True((process.ExitCode == 0) == expectSuccess, $"msbuild exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result + stderr.Result;
    }

    private static string Text(string path, string name) => Encoding.UTF8.GetString(Resource(path, name) ?? throw new InvalidOperationException($"{path} has no {name}"));

    // An RCDATA resource of an executable, by its string name; null when it has none.
    private static byte[]? Resource(string path, string name)
    {
        var module = LoadLibraryExW(path, 0, 0x22);   // as data and image resource
        try
        {
            var info = FindResourceW(module, name, 10);
            if (info == 0) return null;
            var size = (int)SizeofResource(module, info);
            var bytes = new byte[size];
            Marshal.Copy(LockResource(LoadResource(module, info)), bytes, 0, size);
            return bytes;
        }
        finally { FreeLibrary(module); }
    }

    private static string TargetsFile() => Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint FindResourceW(nint module, string name, nint type);
    [DllImport("kernel32")] private static extern uint SizeofResource(nint module, nint info);
    [DllImport("kernel32")] private static extern nint LoadResource(nint module, nint info);
    [DllImport("kernel32")] private static extern nint LockResource(nint data);
}
