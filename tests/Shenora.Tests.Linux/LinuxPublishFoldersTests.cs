using System.Diagnostics;
using System.Runtime.InteropServices;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Linux;

/// <summary>
/// <c>ShenoraChromiumPublishFolders</c> on Linux (D96), through the REAL targets file and a real <c>dotnet publish</c>: the
/// app's apphost at the root pointed at <c>lib/</c>, CEF's runtime and the helper in <c>engine/</c>, and with the option
/// off the flat publish. CEF itself is a fake distribution here; the real one is the Chromium sample's, in
/// <see cref="LinuxWebPanelTests"/>.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class LinuxPublishFoldersTests
{
    private static readonly string[] CefFiles =
    [
        "Release/libcef.so", "Release/chrome-sandbox", "Release/libvk_swiftshader.so", "Release/libvulkan.so.1",
        "Release/v8_context_snapshot.bin", "Release/vk_swiftshader_icd.json",
        "Resources/resources.pak", "Resources/icudtl.dat", "Resources/chrome_100_percent.pak",
        "Resources/locales/de.pak", "Resources/locales/en-US.pak", "Resources/locales/fr.pak",
    ];

    [Fact]
    public void With_the_option_the_root_holds_the_one_executable_CEF_and_the_helper_are_in_engine_and_the_app_in_lib()
    {
        var root = NewRoot();
        try
        {
            var tree = Publish(root, folders: true, ["-p:ShenoraChromiumLocales=fr"]);
            Assert.Equal(
            [
                "MyApp",
                "engine/MyApp-helper", "engine/chrome-sandbox", "engine/chrome_100_percent.pak", "engine/icudtl.dat", "engine/libcef.so",
                "engine/libvk_swiftshader.so", "engine/libvulkan.so.1", "engine/locales/en-US.pak", "engine/locales/fr.pak",
                "engine/resources.pak", "engine/v8_context_snapshot.bin", "engine/vk_swiftshader_icd.json",
                "lib/MyApp.App.deps.json", "lib/MyApp.App.dll", "lib/MyApp.App.pdb", "lib/MyApp.App.runtimeconfig.json",
            ], tree);
            AssertExecutable(Path.Combine(root, "publish", "MyApp"));
            AssertExecutable(Path.Combine(root, "publish", "engine", "MyApp-helper"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The apphost the root's executable is: it must start the app from lib/, and say where it ran from.
    [Fact]
    public void The_folders_executable_starts_the_app_from_lib()
    {
        var root = NewRoot();
        try
        {
            Publish(root, folders: true, []);
            Assert.Equal(Path.Combine(root, "publish", "lib") + "/", Start(Path.Combine(root, "publish", "MyApp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Without_it_the_publish_is_flat_as_before_and_its_executable_starts_the_app_beside_it()
    {
        var root = NewRoot();
        try
        {
            var tree = Publish(root, folders: false, []);
            Assert.Contains("MyApp", tree);
            Assert.Contains("MyApp-helper", tree);
            Assert.Contains("MyApp.App.dll", tree);
            Assert.Contains("libcef.so", tree);
            Assert.Contains("locales/fr.pak", tree);
            Assert.DoesNotContain(tree, f => f.StartsWith("engine/", StringComparison.Ordinal) || f.StartsWith("lib/", StringComparison.Ordinal));
            Assert.Equal(Path.Combine(root, "publish") + "/", Start(Path.Combine(root, "publish", "MyApp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A startup screen (D103) needs a native starter in front of the app on Linux: <App> is the starter with the screen stamped on the end of its
    // file, and the apphost, which stays as the SDK made it, is beside it, or in lib/ with the folders.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_a_startup_screen_the_root_executable_is_the_starter_carrying_it_and_the_apphost_is_beside_it_or_in_lib(bool folders)
    {
        var root = NewRoot();
        try
        {
            var tree = Publish(root, folders, ScreenProperties(root));
            var starter = File.ReadAllBytes(Path.Combine(root, "publish", "MyApp"));
            AssertStamp(starter, "starter-bytes");
            Assert.Contains(folders ? "lib/MyApp.App" : "MyApp.App", tree);
            Assert.Contains(folders ? "lib/MyApp.App.dll" : "MyApp.App.dll", tree);
            if (folders) Assert.Equal(["MyApp"], tree.Where(f => !f.Contains('/')));
            AssertExecutable(Path.Combine(root, "publish", "MyApp"));
            AssertExecutable(Path.Combine(root, "publish", folders ? "lib" : "", "MyApp.App"));

            // Published again over itself the stamp is on the starter once, and a publish without a screen over it is the apphost again.
            Publish(root, folders, ScreenProperties(root));
            Assert.Equal(starter, File.ReadAllBytes(Path.Combine(root, "publish", "MyApp")));
            Publish(root, folders, []);
            Assert.DoesNotContain("SHENORASCREEN001", MagicOf(File.ReadAllBytes(Path.Combine(root, "publish", "MyApp"))));
            Assert.Equal(Path.Combine(root, "publish", folders ? "lib" : "") + "/", Start(Path.Combine(root, "publish", "MyApp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_startup_screen_without_the_starter_is_an_error_naming_it()
    {
        var root = NewRoot();
        try
        {
            var (project, dist) = App(root, folders: false);
            var png = ScreenProperties(root)[0];
            var said = Run("dotnet", ["build", project, "-c", "Debug", $"-p:ShenoraCefDist={dist}", png, $"-p:ShenoraChromiumStarter={Path.Combine(root, "missing")}",
                "--disable-build-servers", "-nologo", "-v:minimal"], expectSuccess: false);
            Assert.Contains("The kit's Linux starter, which draws the startup screen, is missing", said);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The screen's own description and picture, and a stand-in for the starter, in the test's folder.
    private static string[] ScreenProperties(string root)
    {
        Directory.CreateDirectory(root);
        var png = Path.Combine(root, "screen.png");
        var starter = Path.Combine(root, "starter");
        if (!File.Exists(png)) File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "png-body"u8.ToArray()]);
        if (!File.Exists(starter)) File.WriteAllText(starter, "starter-bytes");
        return [$"-p:ShenoraChromiumStartupScreen={png}", $"-p:ShenoraChromiumStarter={starter}", "-p:ShenoraChromiumStartupScreenWidth=300",
            "-p:ShenoraChromiumStartupScreenHeight=180", "-p:ShenoraChromiumStartupScreenProgressBar=true", "-p:ShenoraChromiumStartupScreenBackground=#102030"];
    }

    private static string MagicOf(byte[] file) => System.Text.Encoding.ASCII.GetString(file, Math.Max(0, file.Length - 16), Math.Min(16, file.Length));

    // The stamp starter_linux.cpp reads: [description][PNG][description size: u32le][PNG size: u32le][magic] after the starter's own bytes.
    private static void AssertStamp(byte[] file, string starter)
    {
        Assert.Equal("SHENORASCREEN001", MagicOf(file));
        var textSize = (int)BitConverter.ToUInt32(file, file.Length - 24);
        var pngSize = (int)BitConverter.ToUInt32(file, file.Length - 20);
        Assert.Equal(starter.Length + textSize + pngSize + 24, file.Length);
        Assert.Equal(starter, System.Text.Encoding.ASCII.GetString(file, 0, starter.Length));
        var text = System.Text.Encoding.UTF8.GetString(file, starter.Length, textSize).Split('\n');
        Assert.Equal(["width=300", "height=180", "background=FF102030", "progress_bar=1", "progress_color=FF3B82F6", "corners=rounded", "monitor=cursor", "timeout_ms=20000"], text);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "png-body"u8.ToArray()], file[(starter.Length + textSize)..(starter.Length + textSize + pngSize)]);
    }

    // A build is what an IDE and `dotnet run` start: the option is for a publish.
    [Fact]
    public void A_build_stays_flat_with_the_option()
    {
        var root = NewRoot();
        try
        {
            var (project, dist) = App(root, folders: true);
            Run("dotnet", ["build", project, "-c", "Debug", $"-p:ShenoraCefDist={dist}", "--disable-build-servers", "-nologo", "-v:minimal"]);
            var tree = Tree(Path.Combine(root, "app", "bin", "Debug", "net10.0", "linux-x64"));
            Assert.Contains("MyApp", tree);
            Assert.Contains("MyApp-helper", tree);
            Assert.Contains("libcef.so", tree);
            Assert.DoesNotContain(tree, f => f.StartsWith("engine/", StringComparison.Ordinal) || f.StartsWith("lib/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The option switched and the app published into the same folder again. The SDK's incremental clean removes only what
    // IT wrote, never CEF, and the app finds engine/ before its own folder: a leftover would run.
    [Fact]
    public void A_flat_publish_over_a_folders_one_leaves_no_engine_and_no_app_in_lib_and_the_executable_starts_beside_itself()
    {
        var root = NewRoot();
        try
        {
            Publish(root, folders: true, []);
            File.WriteAllText(Path.Combine(root, "publish", "engine", "the-apps-own.txt"), "");
            var tree = Publish(root, folders: false, []);
            Assert.Equal(["engine/the-apps-own.txt"], tree.Where(f => f.StartsWith("engine/", StringComparison.Ordinal)));
            Assert.DoesNotContain("lib/MyApp.App.dll", tree);
            Assert.Contains("libcef.so", tree);
            Assert.Equal(Path.Combine(root, "publish") + "/", Start(Path.Combine(root, "publish", "MyApp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_folders_publish_over_a_flat_one_leaves_no_CEF_at_the_root_and_the_executable_starts_from_lib()
    {
        var root = NewRoot();
        try
        {
            Publish(root, folders: false, []);
            var tree = Publish(root, folders: true, ["-p:ShenoraChromiumLocales=fr"]);
            Assert.Equal(["MyApp"], tree.Where(f => !f.Contains('/')));
            Assert.DoesNotContain(tree, f => f.StartsWith("locales/", StringComparison.Ordinal));
            Assert.Contains("engine/libcef.so", tree);
            Assert.Equal(Path.Combine(root, "publish", "lib") + "/", Start(Path.Combine(root, "publish", "MyApp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // The runtime is in lib/ with the app, and the root's apphost finds it there (measured: it searches the folder its
    // embedded path names).
    [Fact]
    public void A_self_contained_publish_carries_the_runtime_in_lib_and_the_executable_starts_it()
    {
        var root = NewRoot();
        try
        {
            var tree = Publish(root, folders: true, ["--self-contained", "true"]);
            Assert.Equal(["MyApp"], tree.Where(f => !f.Contains('/')));
            Assert.Contains("lib/libhostfxr.so", tree);
            Assert.Contains("lib/libcoreclr.so", tree);
            Assert.Contains("lib/MyApp.App.dll", tree);
            Assert.DoesNotContain("lib/MyApp.App", tree);
            Assert.Equal(Path.Combine(root, "publish", "lib") + "/", Start(Path.Combine(root, "publish", "MyApp"), withDotnet: false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // What the pointing task refuses, rather than leave an executable that starts nothing.
    [Theory]
    [InlineData("none")]
    [InlineData("twice")]
    [InlineData("followed")]
    public void An_apphost_the_task_does_not_know_is_an_error_naming_why(string shape)
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(root);
            var apphost = Path.Combine(root, "apphost");
            var path = "MyApp.App.dll\0"u8.ToArray();
            var padding = new byte[40];
            byte[] bytes = shape switch
            {
                "none" => [.. padding, .. "something else\0"u8.ToArray(), .. padding],
                "twice" => [.. padding, .. path, .. padding, .. path, .. padding],
                _ => [.. padding, .. path, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7],
            };
            File.WriteAllBytes(apphost, bytes);
            var probe = Path.Combine(root, "point.proj");
            File.WriteAllText(probe, $"""
                <Project>
                  <Import Project="{TargetsFile()}" />
                  <Target Name="Point"><ShenoraChromiumPointApphost Executable="{apphost}" From="MyApp.App.dll" To="lib/MyApp.App.dll" /></Target>
                </Project>
                """);
            var said = Run("dotnet", ["msbuild", probe, "-t:Point", "-nologo", "-noAutoResponse", "-v:minimal"], expectSuccess: false);
            Assert.Equal(bytes, File.ReadAllBytes(apphost));   // refused: untouched
            Assert.Contains(shape switch
            {
                "none" => "does not carry the app path",
                "twice" => "more than once",
                _ => "is followed by data",
            }, said);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_engine_is_beside_the_app_or_in_the_engine_folder_beside_lib()
    {
        static Func<string, bool> Exists(string[] files) => path => files.Contains(path);

        // Flat: libcef.so beside the app, the executable beside it.
        Assert.Equal("/app", LinuxPlatform.EngineOf("/app", Exists(["/app/libcef.so"])));
        Assert.Equal("/app", LinuxPlatform.RootOf("/app", Exists(["/app/libcef.so"])));
        // Folders: the app in lib/, CEF in engine/ beside it, the executable above.
        Assert.Equal("/app/engine", LinuxPlatform.EngineOf("/app/lib/", Exists(["/app/engine/libcef.so"])));
        Assert.Equal("/app", LinuxPlatform.RootOf("/app/lib/", Exists(["/app/engine/libcef.so"])));
        // Neither has it: the app's own folder, so the message names where a flat layout puts it.
        Assert.Equal("/app/lib/", LinuxPlatform.EngineOf("/app/lib/", Exists([])));
        Assert.Equal("/app/lib/", LinuxPlatform.RootOf("/app/lib/", Exists([])));
        // Only a folder named lib is looked above, and a runtime beside the app wins.
        Assert.Equal("/app/bin", LinuxPlatform.EngineOf("/app/bin", Exists(["/app/engine/libcef.so"])));
        Assert.Equal("/app/lib", LinuxPlatform.EngineOf("/app/lib", Exists(["/app/lib/libcef.so", "/app/engine/libcef.so"])));
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "shenora-linux-publish-" + Guid.NewGuid().ToString("N"));

    // A real publish of a one-file app through the real targets into root/publish, over whatever an earlier call left; the tree.
    private static string[] Publish(string root, bool folders, string[] properties)
    {
        var (project, dist) = App(root, folders);
        var publish = Path.Combine(root, "publish");
        Run("dotnet", ["publish", project, "-o", publish, $"-p:ShenoraCefDist={dist}", "--disable-build-servers", "-nologo", "-v:minimal",
            .. properties]);
        return Tree(publish);
    }

    // A one-file app whose project imports the real targets, from a fake CEF distribution and a fake helper. It prints the
    // folder it runs from.
    private static (string Project, string Dist) App(string root, bool folders)
    {
        var dist = Path.Combine(root, "dist");
        var helper = Path.Combine(root, "helper");
        if (!Directory.Exists(dist))
        {
            foreach (var file in CefFiles)
            {
                var path = Path.Combine(dist, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Path.GetFileNameWithoutExtension(file));
            }
            File.WriteAllText(helper, "helper");
        }
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "Program.cs"), "System.Console.Write(System.AppContext.BaseDirectory);");
        var project = Path.Combine(app, "MyApp.App.csproj");
        File.WriteAllText(project, $"""
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
                <SelfContained>false</SelfContained>
                <AssemblyName>MyApp.App</AssemblyName>
                <ShenoraChromiumShim>{helper}</ShenoraChromiumShim>
                <ShenoraChromiumPublishFolders>{(folders ? "true" : "false")}</ShenoraChromiumPublishFolders>
              </PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              <Import Project="{TargetsFile()}" />
            </Project>
            """);
        return (project, dist);
    }

    // The executable run, and what it printed: the folder the app ran from.
    private static string Start(string executable, bool withDotnet = true)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (withDotnet) info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        else { info.Environment.Remove("DOTNET_ROOT"); info.Environment["DOTNET_ROOT_X64"] = ""; info.Environment["PATH"] = "/usr/bin:/bin"; }
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(30)), "the app did not end in 30 s");
        Assert.True(process.ExitCode == 0, $"the app exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result;
    }

    private static void AssertExecutable(string path) =>
        Assert.True((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0, $"{path} has no execute bit");

    private static string[] Tree(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();

    private static string TargetsFile() => Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets");

    private static string Run(string exe, string[] args, bool expectSuccess = true)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "the build did not finish in 5 minutes");
        Assert.True((process.ExitCode == 0) == expectSuccess, $"the build exited {process.ExitCode}: {stdout.Result}{stderr.Result}");
        return stdout.Result + stderr.Result;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }
}
