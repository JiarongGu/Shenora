using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shenora.Tests.Chromium;

/// <summary>
/// On Windows <c>&lt;App&gt;.exe</c> is CEF's launcher copied in, which presents as "CEF Bootstrap Application" with CEF's
/// icon and version. The layout gives it the app's own, from <c>&lt;App&gt;.App.dll</c>, and must leave its manifest
/// alone: CEF's processes need its supportedOS entry. Through the REAL targets file, on a copy of a real executable
/// that has both a version and a manifest (this test process's own).
/// </summary>
public class ChromiumLauncherStampTests
{
    [Fact]
    public void The_launcher_takes_the_apps_version_and_icon_and_keeps_its_manifest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "shenora-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var launcher = Path.Combine(root, "App.exe");
            File.Copy(Environment.ProcessPath!, launcher);
            var app = typeof(ChromiumLauncherStampTests).Assembly.Location;
            var manifest = Resource(launcher, 24, 1);
            Assert.NotNull(manifest);   // else the test proves nothing about keeping it
            Assert.NotEqual(FileVersionInfo.GetVersionInfo(app).FileDescription, FileVersionInfo.GetVersionInfo(launcher).FileDescription);

            var project = Path.Combine(root, "stamp.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <Import Project="{Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets")}" />
                  <Target Name="Stamp"><ShenoraChromiumStampLauncher Executable="{launcher}" From="{app}" /></Target>
                </Project>
                """);
            Run("dotnet", ["msbuild", project, "-t:Stamp", "-nologo", "-noAutoResponse", "-v:minimal"]);

            var stamped = FileVersionInfo.GetVersionInfo(launcher);
            var source = FileVersionInfo.GetVersionInfo(app);
            Assert.Equal(source.FileDescription, stamped.FileDescription);
            Assert.Equal(source.ProductVersion, stamped.ProductVersion);
            Assert.Equal(manifest, Resource(launcher, 24, 1));
            Assert.Equal(Count(app, 14), Count(launcher, 14));   // the app's icon groups, which here are none
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[]? Resource(string path, int type, int id)
    {
        var module = LoadLibraryExW(path, 0, 0x22);   // as data and image resource
        try
        {
            var info = FindResourceW(module, id, type);
            if (info == 0) return null;
            var size = (int)SizeofResource(module, info);
            var bytes = new byte[size];
            Marshal.Copy(LockResource(LoadResource(module, info)), bytes, 0, size);
            return bytes;
        }
        finally { FreeLibrary(module); }
    }

    private static int Count(string path, int type)
    {
        var module = LoadLibraryExW(path, 0, 0x22);
        var count = 0;
        try { EnumResourceNamesW(module, type, (_, _, _, _) => { count++; return true; }, 0); }
        finally { FreeLibrary(module); }
        return count;
    }

    private static void Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

    private delegate bool EnumNames(nint module, nint type, nint name, nint param);

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32")] private static extern nint FindResourceW(nint module, nint name, nint type);
    [DllImport("kernel32")] private static extern uint SizeofResource(nint module, nint info);
    [DllImport("kernel32")] private static extern nint LoadResource(nint module, nint info);
    [DllImport("kernel32")] private static extern nint LockResource(nint data);
    [DllImport("kernel32")] private static extern bool EnumResourceNamesW(nint module, nint type, EnumNames callback, nint param);
}
