using Shenora.Core;

namespace Shenora.Tests.Core;

public class AppRootArgumentTests
{
    private const string Fallback = @"C:\Fallback";

    [Theory]
    [InlineData(new[] { "--app-root", @"C:\Install" }, @"C:\Install")]
    [InlineData(new[] { "--APP-ROOT", @"C:\Install" }, @"C:\Install")]
    [InlineData(new[] { @"--app-root=C:\Install" }, @"C:\Install")]
    [InlineData(new[] { "--app-root", "\" C:\\Install \"" }, @"C:\Install")] // quotes + padding stripped
    [InlineData(new[] { "other", "--app-root", @"C:\Install", "trailing" }, @"C:\Install")]
    public void Resolves_the_flag_in_both_forms(string[] args, string expected)
    {
        Assert.Equal(expected, AppRootArgument.Resolve(args, Fallback));
    }

    [Theory]
    [InlineData(null)]
    [InlineData((object)new string[0])]
    [InlineData((object)new[] { "--app-root" })]          // flag with no value
    [InlineData((object)new[] { "--app-root=" })]         // joined with no value
    [InlineData((object)new[] { "--app-root", "  " })]    // blank value
    [InlineData((object)new[] { "--unrelated", "x" })]
    public void Falls_back_when_flag_is_absent_or_blank(string[]? args)
    {
        Assert.Equal(Fallback, AppRootArgument.Resolve(args, Fallback));
    }
}
