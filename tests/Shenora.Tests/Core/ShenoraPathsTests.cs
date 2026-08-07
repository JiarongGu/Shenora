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
}
