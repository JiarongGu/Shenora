using System.Diagnostics;

namespace Shenora.Tests.Chromium;

/// <summary>
/// CEF's launcher is CET-compatible with strict context-IP validation, so a shell extension that raises and handles its
/// own exception under the app (a context-menu handler, on a right-click in a file picker) ends the process with
/// fail-fast 0xc0000409, parameter 0x30. The Windows layout relaxes that one check in the launcher it lays out, read back
/// here from the PE; <c>ShenoraChromiumRelaxContextIp=false</c> keeps CEF's own. Through the REAL targets file, on copies
/// of a real executable made CET-compatible the way CEF's launcher is.
/// </summary>
public class ChromiumContextIpTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void The_layout_relaxes_the_launchers_context_ip_validation_and_keeps_the_shadow_stack(string? option)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            var layout = Layout(root, option is null ? [] : [$"-p:ShenoraChromiumRelaxContextIp={option}"]);
            Assert.Equal(LauncherPe.CetCompatible | LauncherPe.RelaxedContextIp, LauncherPe.Flags(Path.Combine(layout, "MyApp.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_app_can_keep_CEFs_strict_validation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            var layout = Layout(root, ["-p:ShenoraChromiumRelaxContextIp=false"]);
            Assert.Equal(LauncherPe.CetCompatible, LauncherPe.Flags(Path.Combine(layout, "MyApp.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_console_launcher_is_relaxed_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            var layout = Layout(root, ["-p:OutputType=Exe"]);
            Assert.Equal(LauncherPe.CetCompatible | LauncherPe.RelaxedContextIp, LauncherPe.Flags(Path.Combine(layout, "MyApp.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A launcher that never declared CET compatibility has no strict mode to relax: it is not touched, and not given an entry.
    [Fact]
    public void A_launcher_that_is_not_CET_compatible_is_left_byte_for_byte()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            var launcher = Path.Combine(root, "App.exe");
            File.Copy(Environment.ProcessPath!, launcher);
            Assert.Null(LauncherPe.Flags(launcher));   // else the test proves nothing
            var before = File.ReadAllBytes(launcher);
            Relax(root, launcher);
            Assert.Equal(before, File.ReadAllBytes(launcher));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Relaxing_twice_changes_nothing_more()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        try
        {
            var launcher = Path.Combine(root, "App.exe");
            File.Copy(Environment.ProcessPath!, launcher);
            LauncherPe.MakeCetCompatible(launcher);
            Relax(root, launcher);
            var once = File.ReadAllBytes(launcher);
            Relax(root, launcher);
            Assert.Equal(LauncherPe.CetCompatible | LauncherPe.RelaxedContextIp, LauncherPe.Flags(launcher));
            Assert.Equal(once, File.ReadAllBytes(launcher));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-contextip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // The task alone, on one file.
    private static void Relax(string root, string launcher)
    {
        var project = Path.Combine(root, "relax.proj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="{TargetsFile()}" />
              <Target Name="Relax"><ShenoraChromiumRelaxContextIp Executable="{launcher}" /></Target>
            </Project>
            """);
        Run(["msbuild", project, "-t:Relax", "-nologo", "-noAutoResponse", "-v:minimal"]);
    }

    // The Windows layout into root\out from a fake CEF distribution whose launchers are CET-compatible, as CEF's are.
    private static string Layout(string root, string[] properties)
    {
        var dist = Path.Combine(root, "dist");
        Directory.CreateDirectory(Path.Combine(dist, "Release"));
        Directory.CreateDirectory(Path.Combine(dist, "Resources"));
        foreach (var name in new[] { "bootstrap.exe", "bootstrapc.exe" })
        {
            var launcher = Path.Combine(dist, "Release", name);
            File.Copy(Environment.ProcessPath!, launcher);
            LauncherPe.MakeCetCompatible(launcher);
        }
        File.WriteAllText(Path.Combine(dist, "Release", "libcef.dll"), "libcef");
        var shim = Path.Combine(root, "shim.dll");
        File.WriteAllText(shim, "shim");

        var layout = Path.Combine(root, "out") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(layout);
        File.Copy(typeof(ChromiumContextIpTests).Assembly.Location, Path.Combine(layout, "MyApp.App.dll"));   // a real assembly, for the stamp to read
        var project = Path.Combine(root, "probe.proj");
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
        // ShenoraCefDist on the command line: the targets file assigns it from the CEF cache, and only a global property outranks that.
        Run(["msbuild", project, "-t:_ShenoraChromiumWindowsInto", $"-p:_ShenoraWindowsInto={layout}", "-p:_ShenoraWindowsPublish=false",
            $"-p:ShenoraCefDist={dist}", "-nologo", "-noAutoResponse", "-v:minimal", .. properties]);
        return layout;
    }

    private static string TargetsFile() => Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");

    private static void Run(string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "msbuild did not finish in 2 minutes");
        Assert.True(process.ExitCode == 0, $"msbuild exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }
}
