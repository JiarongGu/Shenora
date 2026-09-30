using System.Diagnostics;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Core;

/// <summary>
/// The shell launcher both desktop shells register. The validation paths run for real; what it starts on each OS is
/// read off a recording seam, since the real thing opens a file manager on the machine running the tests. The Linux
/// file-manager call was run against a scripted FileManager1 on a private session bus.
/// </summary>
public class ShellLauncherTests
{
    private readonly ShellLauncher _launcher = new();

    private sealed class Recorder
    {
        public readonly List<ProcessStartInfo> Launched = [];
        public readonly List<ProcessStartInfo> Waited = [];
        public int ExitCode;

        public ShellLauncher For(ShellLauncher.Os os) =>
            new(os, Launched.Add, (info, _) => { Waited.Add(info); return ExitCode; });
    }

    [Fact]
    public void Reveal_requires_an_existing_file()
    {
        Assert.ThrowsAny<ArgumentException>(() => _launcher.RevealInFileManager(""));
        Assert.Throws<FileNotFoundException>(() => _launcher.RevealInFileManager(@"C:\definitely\missing\file.bin"));
    }

    [Fact]
    public void Open_directory_requires_an_existing_directory()
    {
        Assert.ThrowsAny<ArgumentException>(() => _launcher.OpenDirectory(" "));
        Assert.Throws<DirectoryNotFoundException>(() => _launcher.OpenDirectory(@"C:\definitely\missing\dir"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("not a url")]
    [InlineData("ftp://host/file")]
    public void Open_url_rejects_non_web_schemes(string url)
    {
        // The same policy as the WebView2 new-window handling: never shell-execute odd protocols.
        Assert.Throws<ArgumentException>(() => _launcher.OpenUrl(url));
    }

    [Fact]
    public void Launch_requires_an_existing_executable()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => _launcher.LaunchProcess(new ProcessLaunchOptions { ExecutablePath = "" }));
        Assert.Throws<FileNotFoundException>(
            () => _launcher.LaunchProcess(new ProcessLaunchOptions { ExecutablePath = @"C:\definitely\missing\tool.exe" }));
    }

    [Fact]
    public void Reveal_selects_the_file_in_Explorer_and_in_Finder()
    {
        using var temp = TempDir.Create();
        var file = Path.Combine(temp.Root, "a b.txt");
        File.WriteAllText(file, "x");

        var windows = new Recorder();
        windows.For(ShellLauncher.Os.Windows).RevealInFileManager(file);
        var explorer = Assert.Single(windows.Launched);
        Assert.Equal("explorer.exe", explorer.FileName);
        Assert.Equal($"/select,\"{file}\"", explorer.Arguments);

        var mac = new Recorder();
        mac.For(ShellLauncher.Os.MacOS).RevealInFileManager(file);
        var finder = Assert.Single(mac.Launched);
        Assert.Equal("open", finder.FileName);
        Assert.Equal(["-R", file], finder.ArgumentList);
        Assert.Empty(mac.Waited);
    }

    [Fact]
    public void On_Linux_reveal_asks_the_file_manager_to_show_the_item()
    {
        using var temp = TempDir.Create();
        var file = Path.Combine(temp.Root, "one, two.txt");
        File.WriteAllText(file, "x");
        var linux = new Recorder { ExitCode = 0 };

        linux.For(ShellLauncher.Os.Linux).RevealInFileManager(file);

        var call = Assert.Single(linux.Waited);
        Assert.Equal("dbus-send", call.FileName);
        Assert.Contains("org.freedesktop.FileManager1.ShowItems", call.ArgumentList);
        var uri = Assert.Single(call.ArgumentList, a => a.StartsWith("array:string:", StringComparison.Ordinal));
        Assert.DoesNotContain(",", uri["array:string:".Length..]);   // a comma would split dbus-send's array
        Assert.Contains("%2C", uri);
        Assert.Empty(linux.Launched);   // shown: no folder opened besides
    }

    [Fact]
    public void On_Linux_with_no_file_manager_to_ask_reveal_opens_the_folder()
    {
        using var temp = TempDir.Create();
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "x");
        var linux = new Recorder { ExitCode = 1 };   // no FileManager1 on the bus, or no dbus-send (-1)

        linux.For(ShellLauncher.Os.Linux).RevealInFileManager(file);

        var opened = Assert.Single(linux.Launched);
        Assert.Equal(Path.GetFullPath(temp.Root), opened.FileName);
        Assert.True(opened.UseShellExecute);
    }

    [Fact]
    public void On_Linux_a_machine_with_nothing_to_open_a_folder_is_named_as_such()
    {
        using var temp = TempDir.Create();
        var launcher = new ShellLauncher(ShellLauncher.Os.Linux,
            _ => throw new System.ComponentModel.Win32Exception(2, "No such file or directory"), (_, _) => -1);
        var ex = Assert.Throws<InvalidOperationException>(() => launcher.OpenDirectory(temp.Root));
        Assert.Contains("xdg-open", ex.Message);
    }

    [Fact]
    public void Open_directory_and_open_url_go_to_the_OS_handler()
    {
        using var temp = TempDir.Create();
        foreach (var os in new[] { ShellLauncher.Os.Windows, ShellLauncher.Os.MacOS, ShellLauncher.Os.Linux })
        {
            var recorder = new Recorder();
            var launcher = recorder.For(os);
            launcher.OpenDirectory(temp.Root);
            launcher.OpenUrl("https://example.com/a");
            Assert.Equal([temp.Root, "https://example.com/a"], recorder.Launched.Select(i => i.FileName));
            Assert.All(recorder.Launched, i => Assert.True(i.UseShellExecute));
        }
    }
}
