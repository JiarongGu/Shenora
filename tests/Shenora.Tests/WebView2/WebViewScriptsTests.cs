using Shenora.WebView2;

namespace Shenora.Tests.WebView2;

public class WebViewScriptsTests
{
    [Fact]
    public void Global_script_serializes_camel_case_json()
    {
        var script = WebViewScripts.BuildGlobalScript("__APP_METADATA__", new { Name = "MyApp", AppVersion = "1.2" });
        Assert.StartsWith("window.__APP_METADATA__ = ", script);
        Assert.EndsWith(";", script);
        Assert.Contains("\"name\":\"MyApp\"", script);
        Assert.Contains("\"appVersion\":\"1.2\"", script);
    }

    [Fact]
    public void Global_script_cannot_break_out_of_the_injected_block()
    {
        // The source apps interpolated raw strings — a value containing a closing tag or quotes
        // could escape the script. STJ's default encoder escapes the dangerous characters.
        var script = WebViewScripts.BuildGlobalScript("__X__", new { Payload = "</script><script>alert(1)" });
        Assert.DoesNotContain("</script", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003C", script); // '<' escaped
    }

    [Fact]
    public void Global_script_serializes_null_and_primitives()
    {
        Assert.Equal("window.x = null;", WebViewScripts.BuildGlobalScript("x", null));
        Assert.Equal("window.$flag = true;", WebViewScripts.BuildGlobalScript("$flag", true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-name")]
    [InlineData("1x")]
    [InlineData("a b")]
    [InlineData("window.x")]
    public void Invalid_global_names_throw(string name)
    {
        Assert.Throws<ArgumentException>(() => WebViewScripts.BuildGlobalScript(name, 1));
    }

    [Fact]
    public void Family_scripts_are_wrapped_iifes()
    {
        Assert.StartsWith("(function()", WebViewScripts.PreventDefaultFileDrop);
        Assert.StartsWith("(function()", WebViewScripts.BlockBrowserShortcuts);
    }
}
