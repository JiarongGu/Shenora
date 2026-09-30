using Shenora;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.Core;

public class ShenoraPathsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] vars) =>
        name => vars.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Root_defaults_to_base_directory()
    {
        var paths = ShenoraPaths.Resolve(baseDirectory: @"C:\MyApp\", getEnvironmentVariable: Env());
        Assert.Equal(@"C:\MyApp\", paths.RootDir);
        Assert.Equal(@"C:\MyApp\data", paths.DataDir);
        Assert.Equal(@"C:\MyApp\res", paths.ResourcesDir);
    }

    [Theory]
    [InlineData(@"C:\MyApp\libs")]
    [InlineData(@"C:\MyApp\libs\")]
    [InlineData(@"C:\MyApp\Lib")]
    public void Exe_inside_a_libs_subfolder_resolves_the_parent_as_root(string baseDir)
    {
        var paths = ShenoraPaths.Resolve(baseDirectory: baseDir, getEnvironmentVariable: Env());
        Assert.Equal(@"C:\MyApp", paths.RootDir);
    }

    [Fact]
    public void Explicit_root_wins_over_everything()
    {
        var paths = ShenoraPaths.Resolve(
            new ShenoraPathsOptions { ExplicitRoot = @"D:\Portable", RootEnvironmentVariable = "MYAPP_ROOT" },
            baseDirectory: @"C:\MyApp\libs",
            getEnvironmentVariable: Env(("MYAPP_ROOT", @"E:\EnvRoot")));
        Assert.Equal(@"D:\Portable", paths.RootDir);
    }

    [Fact]
    public void Root_env_var_wins_over_detection()
    {
        var paths = ShenoraPaths.Resolve(
            new ShenoraPathsOptions { RootEnvironmentVariable = "MYAPP_ROOT" },
            baseDirectory: @"C:\MyApp\libs",
            getEnvironmentVariable: Env(("MYAPP_ROOT", @"E:\EnvRoot")));
        Assert.Equal(@"E:\EnvRoot", paths.RootDir);
    }

    [Fact]
    public void Data_env_var_shares_the_hosts_data_dir_with_child_processes()
    {
        var paths = ShenoraPaths.Resolve(
            new ShenoraPathsOptions { DataEnvironmentVariable = "MYAPP_DATA" },
            baseDirectory: @"C:\Child",
            getEnvironmentVariable: Env(("MYAPP_DATA", @"C:\Host\data")));
        Assert.Equal(@"C:\Host\data", paths.DataDir);
        Assert.Equal(@"C:\Child", paths.RootDir); // root still the child's own — only DATA is shared
    }

    [Fact]
    public void Folder_names_are_configurable()
    {
        var paths = ShenoraPaths.Resolve(
            new ShenoraPathsOptions { DataFolderName = "appdata", ResourcesFolderName = "assets" },
            baseDirectory: @"C:\MyApp", getEnvironmentVariable: Env());
        Assert.Equal(@"C:\MyApp\appdata", paths.DataDir);
        Assert.Equal(@"C:\MyApp\assets", paths.ResourcesDir);
    }

    [Fact]
    public void DataArea_creates_on_first_access()
    {
        using var temp = TempDir.Create();
        var dir = temp.Root;
        var paths = ShenoraPaths.Resolve(baseDirectory: dir, getEnvironmentVariable: Env());
        var area = paths.DataArea("cache");
        Assert.True(Directory.Exists(area));
        Assert.Equal(Path.Combine(dir, "data", "cache"), area);
    }

    // A bundle as the Chromium shell's build lays it out: the app's assemblies in Contents/MacOS, its Info.plist beside.
    private static string Bundle(TempDir temp, string name, string? identifier)
    {
        var macOS = Directory.CreateDirectory(Path.Combine(temp.Root, name + ".app", "Contents", "MacOS")).FullName;
        if (identifier is not null)
        {
            File.WriteAllText(Path.Combine(macOS, "..", "Info.plist"), $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleExecutable</key><string>{name}</string>
                  <key>CFBundleIdentifier</key><string>{identifier}</string>
                </dict>
                </plist>
                """);
        }
        return macOS + Path.DirectorySeparatorChar;
    }

    [Fact]
    public void A_macOS_bundle_keeps_its_data_in_Application_Support_under_its_identifier()
    {
        // D89: beside the executable is inside the bundle, which a signed bundle must not change.
        using var temp = TempDir.Create();
        var baseDir = Bundle(temp, "My App", "com.example.myapp");
        var paths = ShenoraPaths.ResolveFor(null, baseDir, Env(("HOME", "/Users/someone")), isMacOS: true);
        Assert.Equal(Path.GetFullPath(Path.Combine("/Users/someone", "Library", "Application Support", "com.example.myapp")), paths.DataDir);
        Assert.Equal(Path.GetFullPath(baseDir), paths.RootDir);                    // the install, and its res/, stay
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "res")), paths.ResourcesDir);
    }

    [Fact]
    public void A_bundle_whose_plist_names_no_identifier_uses_its_name()
    {
        using var temp = TempDir.Create();
        var baseDir = Bundle(temp, "My App", identifier: null);
        var paths = ShenoraPaths.ResolveFor(null, baseDir, Env(("HOME", "/Users/someone")), isMacOS: true);
        Assert.Equal(Path.GetFullPath(Path.Combine("/Users/someone", "Library", "Application Support", "My App")), paths.DataDir);
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("")]
    public void An_identifier_that_is_not_a_plain_name_is_not_used_as_a_folder(string identifier)
    {
        using var temp = TempDir.Create();
        var baseDir = Bundle(temp, "My App", identifier);
        var paths = ShenoraPaths.ResolveFor(null, baseDir, Env(("HOME", "/Users/someone")), isMacOS: true);
        Assert.Equal(Path.GetFullPath(Path.Combine("/Users/someone", "Library", "Application Support", "My App")), paths.DataDir);
    }

    [Fact]
    public void The_bundle_rule_is_macOS_only_and_only_for_a_bundle()
    {
        using var temp = TempDir.Create();
        var bundle = Bundle(temp, "My App", "com.example.myapp");
        Assert.Equal(Path.GetFullPath(Path.Combine(bundle, "data")),
            ShenoraPaths.ResolveFor(null, bundle, Env(("HOME", "/Users/someone")), isMacOS: false).DataDir);

        var plain = Directory.CreateDirectory(Path.Combine(temp.Root, "portable", "MacOS")).FullName;   // not in X.app/Contents
        Assert.Equal(Path.GetFullPath(Path.Combine(plain, "data")),
            ShenoraPaths.ResolveFor(null, plain, Env(("HOME", "/Users/someone")), isMacOS: true).DataDir);
    }

    [Fact]
    public void A_root_the_app_or_its_launcher_chose_keeps_its_data_in_it_on_macOS_too()
    {
        using var temp = TempDir.Create("chosen");
        var bundle = Bundle(temp, "My App", "com.example.myapp");
        var chosen = Path.Combine(temp.Root, "chosen");
        var paths = ShenoraPaths.ResolveFor(new ShenoraPathsOptions { ExplicitRoot = chosen }, bundle,
            Env(("HOME", "/Users/someone")), isMacOS: true);
        Assert.Equal(Path.GetFullPath(Path.Combine(chosen, "data")), paths.DataDir);
    }

    [Fact]
    public void A_data_directory_of_the_apps_own_wins_over_the_defaults_and_the_hosts_variable_over_it()
    {
        using var temp = TempDir.Create();
        var bundle = Bundle(temp, "My App", "com.example.myapp");
        var own = Path.Combine(temp.Root, "own-data");
        var options = new ShenoraPathsOptions { DataDirectory = own, DataEnvironmentVariable = "MYAPP_DATA_DIR" };

        Assert.Equal(Path.GetFullPath(own), ShenoraPaths.ResolveFor(options, bundle, Env(("HOME", "/Users/someone")), isMacOS: true).DataDir);
        Assert.Equal(Path.GetFullPath(own), ShenoraPaths.ResolveFor(options, @"C:MyApp", Env(), isMacOS: false).DataDir);
        var hosts = Path.Combine(temp.Root, "hosts");
        Assert.Equal(Path.GetFullPath(hosts),
            ShenoraPaths.ResolveFor(options, bundle, Env(("HOME", "/Users/someone"), ("MYAPP_DATA_DIR", hosts)), isMacOS: true).DataDir);
    }
}
