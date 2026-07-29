using Shenora.Core;

namespace Shenora.Tests.Core;

public class ShenoraEnvironmentTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("development", true)]
    [InlineData("Production", false)]
    [InlineData(null, false)]
    public void Detects_environment_variable(string? value, bool expected)
    {
        var env = ShenoraEnvironment.Detect(Path.GetTempPath(), _ => value);
        Assert.Equal(expected, env.IsDevelopment);
    }

    [Fact]
    public void Dotnet_environment_wins_over_aspnetcore()
    {
        var env = ShenoraEnvironment.Detect(Path.GetTempPath(),
            name => name == "DOTNET_ENVIRONMENT" ? "Production" : "Development");
        Assert.False(env.IsDevelopment);
    }

    [Fact]
    public void Dev_marker_file_enables_development()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.False(ShenoraEnvironment.Detect(dir, _ => null).IsDevelopment);
            File.WriteAllText(Path.Combine(dir, ShenoraEnvironment.DevMarkerFileName), "");
            Assert.True(ShenoraEnvironment.Detect(dir, _ => null).IsDevelopment);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Base_directory_is_preserved()
    {
        var env = ShenoraEnvironment.Detect(@"C:\SomeApp", _ => null);
        Assert.Equal(@"C:\SomeApp", env.BaseDirectory);
    }
}
