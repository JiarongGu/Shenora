using Shenora.Windows;

namespace Shenora.Tests.WebView2;

public class BrowserArgumentsTests
{
    [Fact]
    public void Feature_switches_appear_exactly_once()
    {
        // Chromium keeps only the LAST occurrence of a repeated switch — a duplicate silently
        // drops the first list (the incident that earned the builder).
        var args = BrowserArguments.Build(isDevelopment: false);
        Assert.Equal(1, Count(args, "--enable-features="));
        Assert.Equal(1, Count(args, "--disable-features="));
        Assert.Contains("IsolatedCodeCache", args);
        Assert.Contains("msWebView2CodeCache", args);
    }

    [Fact]
    public void Dev_extra_arguments_append_only_in_development()
    {
        const string cdp = "--remote-debugging-port=9222";
        Assert.Contains(cdp, BrowserArguments.Build(true, cdp));
        Assert.DoesNotContain(cdp, BrowserArguments.Build(false, cdp));
        Assert.EndsWith(cdp, BrowserArguments.Build(true, "  " + cdp + "  ")); // trimmed, appended last
    }

    [Fact]
    public void App_additional_arguments_append_in_all_modes()
    {
        const string extra = "--mute-audio";
        Assert.Contains(extra, BrowserArguments.Build(false, null, extra));
        Assert.Contains(extra, BrowserArguments.Build(true, null, extra));
    }

    // This environment hosts the page holding the bridge: a DevTools port there, in production, hands any local process
    // the app's IPC. Development keeps it, as the Chromium shell does its own.
    [Fact]
    public void An_apps_remote_debugging_switches_are_dropped_outside_development()
    {
        const string extra = "--mute-audio --remote-debugging-port=9222 --remote-debugging-address=0.0.0.0 --remote-debugging-pipe";

        var production = BrowserArguments.Build(false, null, extra);
        Assert.DoesNotContain("--remote-debugging", production);
        Assert.Contains("--mute-audio", production);
        Assert.Contains("--remote-debugging-port=9222", BrowserArguments.Build(true, null, extra));
        Assert.True(BrowserArguments.HasRemoteDebugging(extra));
        Assert.False(BrowserArguments.HasRemoteDebugging("--mute-audio"));

        // A session browser's arguments are the app's to drive over CDP: Compose leaves them alone.
        Assert.Contains("--remote-debugging-port=9222", BrowserArguments.Compose("--x", false, null, extra));
    }

    [Fact]
    public void No_arguments_are_empty_or_double_spaced()
    {
        var args = BrowserArguments.Build(true, "--x", "--y");
        Assert.DoesNotContain("  ", args);
        Assert.All(args.Split(' '), a => Assert.StartsWith("--", a));
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
