using System.Diagnostics;

namespace Shenora.Tests.Chromium;

/// <summary>
/// An engine the app brings (D99): <c>ShenoraChromiumEnginePath</c> through the REAL targets file replaces the fetch, a
/// build for another platform than the engine's is refused, and the engine package's own props point at the folder beside
/// them. CEF itself is a fake distribution; the real engine playing is <c>dev.mjs chromium-codecs</c>.
/// </summary>
public class ChromiumEnginePathTests
{
    [Fact]
    public void An_engine_path_replaces_the_fetch()
    {
        var root = NewRoot();
        try
        {
            var engine = Path.Combine(root, "engine");
            Directory.CreateDirectory(Path.Combine(engine, "Release"));
            var cache = Path.Combine(root, "cache");
            var output = Msbuild(root, engine, "win-x64", "windows64", $"<ShenoraCefCache>{cache}</ShenoraCefCache>", "-t:ShenoraChromiumFetchCef", expectSuccess: true);
            // Nothing was asked for or made: the cache a fetch would fill does not exist, and the dist is the engine.
            Assert.False(Directory.Exists(cache), output);
            Assert.Equal(Path.GetFullPath(engine).TrimEnd('\\'), Property(root, engine, "win-x64", "windows64", "ShenoraCefDist").TrimEnd('\\'));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_build_for_another_platform_than_the_engines_is_refused()
    {
        var root = NewRoot();
        try
        {
            var engine = Path.Combine(root, "engine");
            Directory.CreateDirectory(Path.Combine(engine, "Release"));
            var output = Msbuild(root, engine, "win-arm64", "windows64", "", "-t:ShenoraChromiumFetchCef", expectSuccess: false);
            Assert.Contains("is a build for windows64", output);
            Assert.Contains("windowsarm64", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_engine_path_with_no_CEF_in_it_is_refused()
    {
        var root = NewRoot();
        try
        {
            var engine = Path.Combine(root, "empty");
            Directory.CreateDirectory(engine);
            var output = Msbuild(root, engine, "win-x64", "windows64", "", "-t:ShenoraChromiumFetchCef", expectSuccess: false);
            Assert.Contains("has no Release", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_engine_packages_props_point_at_the_engine_beside_them()
    {
        var props = Path.Combine(RepoRoot(), "engines", "Shenora.Chromium.Codecs.win-x64", "build", "Shenora.Chromium.Codecs.win-x64.props");
        var project = Path.Combine(NewRoot(), "probe.proj");
        File.WriteAllText(project, $"""<Project><Import Project="{props}" /><Target Name="T" /></Project>""");
        try
        {
            var path = Run("dotnet", ["msbuild", project, "-getProperty:ShenoraChromiumEnginePath", "-nologo"], expectSuccess: true).Trim();
            // build\..\tools\engine\, as NuGet lays the package out; the platform is CEF's name for it.
            Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(props)!, "..", "tools", "engine")).TrimEnd('\\'),
                Path.GetFullPath(path).TrimEnd('\\'));
            Assert.Equal("windows64", Run("dotnet", ["msbuild", project, "-getProperty:ShenoraChromiumEnginePlatform", "-nologo"], expectSuccess: true).Trim());
        }
        finally { Directory.Delete(Path.GetDirectoryName(project)!, recursive: true); }
    }

    private static string Property(string root, string engine, string rid, string enginePlatform, string name) =>
        Msbuild(root, engine, rid, enginePlatform, "", $"-getProperty:{name}", expectSuccess: true).Trim();

    private static string Msbuild(string root, string engine, string rid, string enginePlatform, string extra, string argument, bool expectSuccess)
    {
        var shim = Path.Combine(root, "shim.dll");
        File.WriteAllText(shim, "shim");
        var project = Path.Combine(root, "probe.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <AssemblyName>MyApp.App</AssemblyName>
                <ShenoraChromiumShim>{shim}</ShenoraChromiumShim>
                <ShenoraChromiumEnginePath>{engine}</ShenoraChromiumEnginePath>
                <ShenoraChromiumEnginePlatform>{enginePlatform}</ShenoraChromiumEnginePlatform>
                {extra}
              </PropertyGroup>
              <Import Project="{TargetsFile()}" />
            </Project>
            """);
        return Run("dotnet", ["msbuild", project, argument, "-nologo", "-noAutoResponse", "-v:minimal"], expectSuccess);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "engine-path-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string TargetsFile() => Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");

    private static string Run(string exe, string[] args, bool expectSuccess)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), "the build did not finish in 3 minutes");
        Assert.True((process.ExitCode == 0) == expectSuccess, $"msbuild exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result + stderr.Result;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }
}
