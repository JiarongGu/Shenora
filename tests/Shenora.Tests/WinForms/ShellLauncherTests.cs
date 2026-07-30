using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Validation-path tests only — the happy paths launch real shell processes (Explorer, the
/// browser), which is e2e/manual territory.
/// </summary>
public class ShellLauncherTests
{
    private readonly ShellLauncher _launcher = new();

    [Fact]
    public void Reveal_requires_an_existing_file()
    {
        Assert.ThrowsAny<ArgumentException>(() => _launcher.RevealInExplorer(""));
        Assert.Throws<FileNotFoundException>(() => _launcher.RevealInExplorer(@"C:\definitely\missing\file.bin"));
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
        Assert.ThrowsAny<ArgumentException>(() => _launcher.LaunchProcess(""));
        Assert.Throws<FileNotFoundException>(() => _launcher.LaunchProcess(@"C:\definitely\missing\tool.exe"));
    }
}
