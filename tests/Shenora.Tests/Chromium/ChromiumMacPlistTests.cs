using System.Diagnostics;
using System.Xml.Linq;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The macOS bundle's Info.plist, written by the REAL targets file's task: the app's icon, display name and copyright
/// when it has them, and no key at all when it has none (an empty CFBundleIconFile is not "no icon" to Finder).
/// </summary>
public class ChromiumMacPlistTests
{
    [Fact]
    public void The_plist_names_the_apps_icon_name_and_copyright_only_when_it_has_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-plist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var full = Path.Combine(root, "full.plist");
            var bare = Path.Combine(root, "bare.plist");
            var project = Path.Combine(root, "plist.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <Import Project="{Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets")}" />
                  <Target Name="Plist">
                    <ShenoraMacPlist File="{full}" Executable="App" Identifier="com.example.app" Version="1.2.3"
                                     IconFile="App.icns" DisplayName="My App &amp; Co" Copyright="(c) Example" />
                    <ShenoraMacPlist File="{bare}" Executable="App" Identifier="com.example.app" Version="1.2.3" />
                  </Target>
                </Project>
                """);
            Run("dotnet", ["msbuild", project, "-t:Plist", "-nologo", "-noAutoResponse", "-v:minimal"]);

            var withAll = Keys(full);
            Assert.Equal("App.icns", withAll["CFBundleIconFile"]);
            Assert.Equal("My App & Co", withAll["CFBundleDisplayName"]);
            Assert.Equal("(c) Example", withAll["NSHumanReadableCopyright"]);
            Assert.Equal("App", withAll["CFBundleName"]);

            var without = Keys(bare);
            Assert.DoesNotContain("CFBundleIconFile", without.Keys);
            Assert.DoesNotContain("CFBundleDisplayName", without.Keys);
            Assert.DoesNotContain("NSHumanReadableCopyright", without.Keys);
            Assert.Equal("1.2.3", without["CFBundleShortVersionString"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The app's own entries (<c>ShenoraChromiumInfoPlist</c>) merge in: a URL scheme and a document type as written,
    /// an app's value in place of the layout's for the same key, and the keys that make the bundle what it is refused.
    /// </summary>
    [Fact]
    public void The_apps_own_entries_merge_in_and_the_bundles_identity_stays_the_layouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-plist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var entries = Path.Combine(root, "entries.plist");
            File.WriteAllText(entries, """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleURLTypes</key>
                  <array><dict><key>CFBundleURLName</key><string>Example</string>
                    <key>CFBundleURLSchemes</key><array><string>example</string></array></dict></array>
                  <key>LSMinimumSystemVersion</key><string>13.0</string>
                  <key>NSCameraUsageDescription</key><string>Calls &amp; video</string>
                </dict>
                </plist>
                """);
            var refused = Path.Combine(root, "refused.plist");
            File.WriteAllText(refused, """
                <plist version="1.0"><dict><key>CFBundleIdentifier</key><string>com.other</string></dict></plist>
                """);
            var merged = Path.Combine(root, "merged.plist");
            var project = Path.Combine(root, "plist.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <Import Project="{Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets")}" />
                  <Target Name="Merged">
                    <ShenoraMacPlist File="{merged}" Executable="App" Identifier="com.example.app" Version="1.2.3" Entries="{entries}" />
                  </Target>
                  <Target Name="Refused">
                    <ShenoraMacPlist File="{Path.Combine(root, "refused-out.plist")}" Executable="App" Identifier="com.example.app"
                                     Version="1.2.3" Entries="{refused}" />
                  </Target>
                </Project>
                """);
            Run("dotnet", ["msbuild", project, "-t:Merged", "-nologo", "-noAutoResponse", "-v:minimal"]);

            var keys = Keys(merged);
            Assert.Equal("13.0", keys["LSMinimumSystemVersion"]);   // the app's, in place of the layout's
            Assert.Equal("Calls & video", keys["NSCameraUsageDescription"]);
            Assert.Equal("com.example.app", keys["CFBundleIdentifier"]);
            var dict = XDocument.Load(merged).Root!.Element("dict")!.Elements().ToList();
            Assert.Single(dict, e => e.Name == "key" && e.Value == "LSMinimumSystemVersion");
            var schemes = dict[dict.FindIndex(e => e.Name == "key" && e.Value == "CFBundleURLTypes") + 1];
            Assert.Equal("example", schemes.Descendants("string").Last().Value);

            var (code, output) = RunUnchecked("dotnet", ["msbuild", project, "-t:Refused", "-nologo", "-noAutoResponse", "-v:minimal"]);
            Assert.NotEqual(0, code);
            Assert.Contains("ShenoraChromiumBundleId", output);
            Assert.False(File.Exists(Path.Combine(root, "refused-out.plist")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // The plist's <key>/<string> pairs, skipping the <true/> ones.
    private static Dictionary<string, string> Keys(string file)
    {
        var dict = XDocument.Load(file).Root!.Element("dict")!.Elements().ToList();
        var keys = new Dictionary<string, string>();
        for (var i = 0; i + 1 < dict.Count; i++)
            if (dict[i].Name == "key" && dict[i + 1].Name == "string") keys[dict[i].Value] = dict[i + 1].Value;
        return keys;
    }

    private static void Run(string exe, string[] args)
    {
        var (code, output) = RunUnchecked(exe, args);
        Assert.True(code == 0, $"msbuild exited {code}: {output}");
    }

    private static (int Code, string Output) RunUnchecked(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(2)), "msbuild did not finish in 2 minutes");
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }
}
