using System.Diagnostics;

namespace Shenora.Tests.Chromium;

/// <summary>
/// <c>ShenoraChromiumLocales</c> through the REAL targets file, over a fake layout of each OS: Windows names a locale
/// by its <c>.pak</c> file, macOS by its <c>.lproj</c> folder with '_' for '-' and en-US as plain <c>en</c>, and each
/// has gendered variants. A mistake in that mapping drops a language, or keeps them all, with no error; the probe
/// measured both OSes once, and this keeps the mapping honest.
/// </summary>
public class ChromiumLocaleFilterTests
{
    private static readonly string[] Files =
    [
        "win/Resources/locales/en-US.pak", "win/Resources/locales/en-US_FEMININE.pak",
        "win/Resources/locales/fr.pak", "win/Resources/locales/fr_NEUTER.pak",
        "win/Resources/locales/de.pak", "win/Resources/locales/de_MASCULINE.pak",
        "win/Resources/locales/zh-CN.pak", "win/Resources/locales/zh-TW.pak",
        "win/Resources/resources.pak",
        "mac/Resources/en.lproj/locale.pak", "mac/Resources/en_GB.lproj/locale.pak",
        "mac/Resources/fr.lproj/locale.pak", "mac/Resources/fr_FEMININE.lproj/locale.pak",
        "mac/Resources/de.lproj/locale.pak",
        "mac/Resources/zh_CN.lproj/locale.pak", "mac/Resources/zh_CN_MASCULINE.lproj/locale.pak",
        "mac/Resources/icudtl.dat",
    ];

    [Fact]
    public void It_drops_every_other_locale_on_both_layouts_and_names_one_the_build_lacks()
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-locales-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var file in Files)
            {
                var path = Path.Combine(root, "layout", file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
            }
            var project = Path.Combine(root, "probe.proj");
            File.WriteAllText(project, $"""
                <Project>
                  <Import Project="{Path.Combine(RepoRoot(), "src", "Shenora.Chromium", "build", "Shenora.Chromium.targets")}" />
                  <Target Name="Probe">
                    <ItemGroup><F Include="{Path.Combine(root, "layout")}/**/*" /></ItemGroup>
                    <ShenoraChromiumLocaleFilter Files="@(F)" Locales="fr;zh-CN;xx-YY">
                      <Output TaskParameter="Dropped" ItemName="D" />
                      <Output TaskParameter="Missing" PropertyName="M" />
                    </ShenoraChromiumLocaleFilter>
                    <Message Importance="high" Text="DROPPED %(D.Identity)" />
                    <Message Importance="high" Text="MISSING $(M)" />
                  </Target>
                </Project>
                """);

            var output = Run("dotnet", ["msbuild", project, "-t:Probe", "-nologo", "-noAutoResponse", "-v:minimal"]);

            var dropped = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("DROPPED ", StringComparison.Ordinal))
                .Select(l => Path.GetRelativePath(Path.Combine(root, "layout"), l["DROPPED ".Length..]).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(
            [
                "mac/Resources/de.lproj/locale.pak",
                "mac/Resources/en_GB.lproj/locale.pak",
                "win/Resources/locales/de.pak",
                "win/Resources/locales/de_MASCULINE.pak",
                "win/Resources/locales/zh-TW.pak",
            ], dropped);
            Assert.Contains("MISSING xx-YY", output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
