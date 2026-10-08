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
        "Resources/resources.pak", "Resources/icudtl.dat", "Resources/locales/en-US.pak", "Resources/locales/fr.pak",
    ];
    // CEF's launchers are real executables, as ChromiumLauncherStampTests' is: the stamp writes resources into one.
    private static readonly string[] Launchers = ["Release/bootstrap.exe", "Release/bootstrapc.exe"];

    [Fact]
    public void With_the_option_the_SDKs_items_and_its_deps_file_go_into_lib()
    {
        var output = Probe(folders: true, "Items");
        Assert.Contains(@"ITEM lib\MyApp.App.dll", output);
        Assert.Contains(@"ITEM lib\MyApp.App.runtimeconfig.json", output);
        Assert.Contains(@"ITEM lib\sub\content.txt", output);
        Assert.Contains(@"DEPS PUBLISH\lib\MyApp.App.deps.json", output.Replace('/', '\\'));
    }

    [Fact]
    public void Without_it_the_items_stay_where_the_SDK_put_them()
    {
        var output = Probe(folders: false, "Items");
        Assert.Contains("ITEM MyApp.App.dll", output);
        Assert.Contains(@"ITEM sub\content.txt", output);
        Assert.Contains("DEPS (default)", output);
    }

    [Fact]
    public void With_the_option_CEF_goes_into_engine_and_three_files_stay_at_the_root()
    {
        var (root, tree) = Layout(folders: true, locales: "fr");
        try
        {
            Assert.Equal(
            [
                "MyApp.dll", "MyApp.exe", "chrome_elf.dll",
                "engine/d3dcompiler_47.dll", "engine/icudtl.dat", "engine/libcef.dll", "engine/locales/en-US.pak",
                "engine/locales/fr.pak", "engine/resources.pak", "engine/v8_context_snapshot.bin", "engine/vk_swiftshader_icd.json",
                "lib/MyApp.App.dll",
            ], tree);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Without_it_the_publish_is_flat_as_before()
    {
        var (root, tree) = Layout(folders: false, locales: "");
        try
        {
            Assert.Contains("libcef.dll", tree);
            Assert.Contains("chrome_elf.dll", tree);
            Assert.Contains("locales/fr.pak", tree);
            Assert.DoesNotContain(tree, f => f.StartsWith("engine/", StringComparison.Ordinal) || f.StartsWith("lib/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The probe: the real targets file, the SDK's publish targets stood in for by empty ones of the same names.
    private static string Probe(bool folders, string target)
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "probe.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <PropertyGroup>
                    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
                    <AssemblyName>MyApp.App</AssemblyName>
                    <ProjectDepsFileName>MyApp.App.deps.json</ProjectDepsFileName>
                    <PublishDir>PUBLISH\</PublishDir>
                    <ShenoraChromiumPublishFolders>{(folders ? "true" : "false")}</ShenoraChromiumPublishFolders>
                  </PropertyGroup>
                  <Import Project="{TargetsFile()}" />
                  <ItemGroup>
                    <ResolvedFileToPublish Include="a.dll" RelativePath="MyApp.App.dll" />
                    <ResolvedFileToPublish Include="b.json" RelativePath="MyApp.App.runtimeconfig.json" />
                    <ResolvedFileToPublish Include="c.txt" RelativePath="sub\content.txt" />
                  </ItemGroup>
                  <Target Name="ComputeResolvedFilesToPublishList" />
                  <Target Name="GeneratePublishDependencyFile" />
                  <Target Name="Items" DependsOnTargets="ComputeResolvedFilesToPublishList;GeneratePublishDependencyFile">
                    <Message Importance="high" Text="ITEM %(ResolvedFileToPublish.RelativePath)" />
                    <Message Importance="high" Condition="'$(PublishDepsFilePath)' != ''" Text="DEPS $(PublishDepsFilePath)" />
                    <Message Importance="high" Condition="'$(PublishDepsFilePath)' == ''" Text="DEPS (default)" />
                  </Target>
                </Project>
                """);
            return Run("dotnet", ["msbuild", project, $"-t:{target}", "-nologo", "-noAutoResponse", "-v:minimal"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The Windows layout written into a publish folder from a fake CEF distribution and a fake shim; the tree it left.
    private static (string Root, string[] Tree) Layout(bool folders, string locales)
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-publish-" + Guid.NewGuid().ToString("N"));
        var dist = Path.Combine(root, "dist");
        foreach (var file in CefFiles)
        {
            var path = Path.Combine(dist, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Path.GetFileNameWithoutExtension(file));
        }
        foreach (var launcher in Launchers) File.Copy(Environment.ProcessPath!, Path.Combine(dist, launcher));
        var shim = Path.Combine(root, "shim.dll");
        File.WriteAllText(shim, "shim");
        var publish = Path.Combine(root, "publish") + Path.DirectorySeparatorChar;
        var app = Path.Combine(publish, folders ? "lib" : "", "MyApp.App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.Copy(typeof(ChromiumPublishFoldersTests).Assembly.Location, app);   // a real assembly, for the stamp to read
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
        var tree = Directory.GetFiles(publish, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(publish, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        return (root, tree);
    }

    private static string TargetsFile() => Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");

    private static string Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "msbuild did not finish in 2 minutes");
        Assert.True(process.ExitCode == 0, $"msbuild exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }
}
