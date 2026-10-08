using System.Diagnostics;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <c>ShenoraChromiumPublishFolders</c> through the REAL targets file: the SDK's publish items moved into lib\, CEF's
/// files into engine\, and with the option off the flat publish 0.20 shipped. CEF itself is a fake distribution.
/// </summary>
public class ChromiumPublishFoldersTests
{
    private static readonly string[] CefFiles =
    [
        "Release/libcef.dll", "Release/chrome_elf.dll", "Release/d3dcompiler_47.dll", "Release/v8_context_snapshot.bin",
        "Release/vk_swiftshader_icd.json",
        "Resources/resources.pak", "Resources/icudtl.dat", "Resources/locales/de.pak", "Resources/locales/en-US.pak",
        "Resources/locales/fr.pak",
    ];
    // CEF's launchers are real executables, as ChromiumLauncherStampTests' is: the stamp writes resources into one.
    private static readonly string[] Launchers = ["Release/bootstrap.exe", "Release/bootstrapc.exe"];

    // A real `dotnet publish`, because the SDK decides what it publishes and in what order: a deps file it regenerates
    // (PreserveStoreLayout here; a PackageReference with Publish="false" or a runtime store alike) is an item it adds
    // after the rest of the list.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_the_option_everything_the_SDK_publishes_goes_into_lib(bool regeneratedDeps)
    {
        var root = NewRoot();
        try
        {
            var tree = Publish(root, folders: true, regeneratedDeps ? ["-p:PreserveStoreLayout=true"] : []);
            Assert.Equal(["MyApp.dll", "MyApp.exe", "chrome_elf.dll"], tree.Where(f => !f.Contains('/')));
            Assert.Equal(
                ["lib/MyApp.App.deps.json", "lib/MyApp.App.dll", "lib/MyApp.App.pdb", "lib/MyApp.App.runtimeconfig.json"],
                tree.Where(f => f.StartsWith("lib/", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Without_it_the_SDK_publishes_beside_the_launcher()
    {
        var root = NewRoot();
        try
        {
            var tree = Publish(root, folders: false, []);
            Assert.Contains("MyApp.App.dll", tree);
            Assert.Contains("MyApp.App.deps.json", tree);
            Assert.Contains("libcef.dll", tree);
            Assert.DoesNotContain(tree, f => f.StartsWith("engine/", StringComparison.Ordinal) || f.StartsWith("lib/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Publish only: a build is what an IDE and `dotnet run` start.
    [Fact]
    public void A_build_stays_flat_with_the_option()
    {
        var root = NewRoot();
        try
        {
            var tree = Build(root, folders: true);
            Assert.Equal(["MyApp.App.deps.json", "MyApp.App.dll", "MyApp.App.pdb", "MyApp.App.runtimeconfig.json", "MyApp.dll",
                "MyApp.exe", "chrome_elf.dll", "d3dcompiler_47.dll", "icudtl.dat", "libcef.dll", "resources.pak",
                "v8_context_snapshot.bin", "vk_swiftshader_icd.json"], tree.Where(f => !f.Contains('/')));
            Assert.Equal(["locales/de.pak", "locales/en-US.pak", "locales/fr.pak"], tree.Where(f => f.Contains('/')));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void With_the_option_CEF_goes_into_engine_and_three_files_stay_at_the_root()
    {
        var root = NewRoot();
        try
        {
            Assert.Equal(
            [
                "MyApp.dll", "MyApp.exe", "chrome_elf.dll",
                "engine/d3dcompiler_47.dll", "engine/icudtl.dat", "engine/libcef.dll", "engine/locales/en-US.pak",
                "engine/locales/fr.pak", "engine/resources.pak", "engine/v8_context_snapshot.bin", "engine/vk_swiftshader_icd.json",
                "lib/MyApp.App.dll",
            ], Layout(root, folders: true, locales: "fr"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Without_it_the_publish_is_flat_as_before()
    {
        var root = NewRoot();
        try
        {
            var tree = Layout(root, folders: false, locales: "");
            Assert.Contains("libcef.dll", tree);
            Assert.Contains("chrome_elf.dll", tree);
            Assert.Contains("locales/fr.pak", tree);
            Assert.DoesNotContain(tree, f => f.StartsWith("engine/", StringComparison.Ordinal) || f.StartsWith("lib/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The option switched off and the app published into the same folder again. The SDK's incremental clean removes
    // only what IT wrote, never CEF, and the shim prefers engine\ and lib\ when they are there: a leftover would run.
    [Fact]
    public void A_flat_publish_over_a_folders_one_leaves_no_engine_and_no_app_in_lib()
    {
        var root = NewRoot();
        try
        {
            Layout(root, folders: true, locales: "");
            File.WriteAllText(Path.Combine(root, "publish", "engine", "the-apps-own.txt"), "");
            var tree = Layout(root, folders: false, locales: "");
            Assert.Equal(["engine/the-apps-own.txt"], tree.Where(f => f.StartsWith("engine/", StringComparison.Ordinal)));
            Assert.DoesNotContain("lib/MyApp.App.dll", tree);
            Assert.Contains("libcef.dll", tree);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_folders_publish_over_a_flat_one_leaves_no_CEF_at_the_root()
    {
        var root = NewRoot();
        try
        {
            Layout(root, folders: false, locales: "");
            // Filtered now: the flat publish's de.pak, a locale this one does not lay out, goes too.
            var tree = Layout(root, folders: true, locales: "fr");
            // MyApp.App.dll is this harness's flat copy; in a real publish the SDK's incremental clean removes it.
            Assert.Equal(["MyApp.App.dll", "MyApp.dll", "MyApp.exe", "chrome_elf.dll"], tree.Where(f => !f.Contains('/')));
            Assert.DoesNotContain(tree, f => f.StartsWith("locales/", StringComparison.Ordinal));
            Assert.Contains("engine/libcef.dll", tree);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "shenora-publish-" + Guid.NewGuid().ToString("N"));

    // A real publish of a one-file app through the real targets into root\publish; the tree it left.
    private static string[] Publish(string root, bool folders, string[] properties)
    {
        var (project, dist) = App(root, folders);
        var publish = Path.Combine(root, "publish");
        Run("dotnet", ["publish", project, "-o", publish, $"-p:ShenoraCefDist={dist}", "--disable-build-servers", "-nologo", "-v:minimal",
            .. properties]);
        return Tree(publish);
    }

    // A real build of the same app; the tree of its output folder.
    private static string[] Build(string root, bool folders)
    {
        var (project, dist) = App(root, folders);
        Run("dotnet", ["build", project, $"-p:ShenoraCefDist={dist}", "--disable-build-servers", "-nologo", "-v:minimal"]);
        return Tree(Path.Combine(root, "app", "bin", "Debug", "net10.0", "win-x64"));
    }

    // A one-file app whose project imports the real targets, from a fake CEF distribution and a fake shim.
    private static (string Project, string Dist) App(string root, bool folders)
    {
        var (dist, shim) = FakeCef(root);
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Program.cs"), "static class Program { static void Main() { } }");
        var project = Path.Combine(app, "MyApp.App.csproj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                <SelfContained>false</SelfContained>
                <AssemblyName>MyApp.App</AssemblyName>
                <ShenoraChromiumShim>{shim}</ShenoraChromiumShim>
                <ShenoraChromiumPublishFolders>{(folders ? "true" : "false")}</ShenoraChromiumPublishFolders>
              </PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              <Import Project="{TargetsFile()}" />
            </Project>
            """);
        return (project, dist);
    }

    // The Windows layout written into root\publish from a fake CEF distribution and a fake shim, over whatever an earlier
    // call left there; the tree it left.
    private static string[] Layout(string root, bool folders, string locales)
    {
        var (dist, shim) = FakeCef(root);
        var publish = Path.Combine(root, "publish") + Path.DirectorySeparatorChar;
        var app = Path.Combine(publish, folders ? "lib" : "", "MyApp.App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.Copy(typeof(ChromiumPublishFoldersTests).Assembly.Location, app, overwrite: true);   // a real assembly, for the stamp to read
        var project = Path.Combine(root, "probe.proj");
        File.WriteAllText(project, $"""
            <Project>
              <PropertyGroup>
                <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                <AssemblyName>MyApp.App</AssemblyName>
                <OutputType>WinExe</OutputType>
                <ShenoraChromiumShim>{shim}</ShenoraChromiumShim>
                <ShenoraChromiumLocales>{locales}</ShenoraChromiumLocales>
                <ShenoraChromiumPublishFolders>{(folders ? "true" : "false")}</ShenoraChromiumPublishFolders>
              </PropertyGroup>
              <Import Project="{TargetsFile()}" />
            </Project>
            """);
        // ShenoraCefDist on the command line: the targets file assigns it from the CEF cache, and only a global
        // property outranks that.
        Run("dotnet", ["msbuild", project, "-t:_ShenoraChromiumWindowsInto", $"-p:_ShenoraWindowsInto={publish}",
            "-p:_ShenoraWindowsPublish=true", $"-p:ShenoraCefDist={dist}", "-nologo", "-noAutoResponse", "-v:minimal"]);
        return Tree(publish);
    }

    private static (string Dist, string Shim) FakeCef(string root)
    {
        var dist = Path.Combine(root, "dist");
        var shim = Path.Combine(root, "shim.dll");
        if (Directory.Exists(dist)) return (dist, shim);
        foreach (var file in CefFiles)
        {
            var path = Path.Combine(dist, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Path.GetFileNameWithoutExtension(file));
        }
        foreach (var launcher in Launchers) File.Copy(Environment.ProcessPath!, Path.Combine(dist, launcher));
        File.WriteAllText(shim, "shim");
        return (dist, shim);
    }

    private static string[] Tree(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    private static string TargetsFile() => Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");

    private static string Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), "the build did not finish in 3 minutes");
        Assert.True(process.ExitCode == 0, $"the build exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }
}
