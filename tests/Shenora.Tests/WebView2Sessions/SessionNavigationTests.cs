using Shenora.Core.Sessions;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// Which completion ends a session's wait. Starting a navigation while another loads aborts that one, and its completion
/// arrives first: a wait that took it returned before its own page loaded (a pool reset, or a navigation after the soft
/// cap returned early), the race e739b78 closed for the first navigation only.
/// </summary>
public class SessionNavigationTests
{
    [Theory]
    [InlineData("https://old.example/slow", false, "ConnectionAborted", true)]    // WebView2's, measured: its server had not answered
    [InlineData("https://old.example/slow", false, "OperationCanceled", true)]    // WebView2's abort of the replaced one
    [InlineData("https://old.example/slow", false, "ERR_ABORTED", true)]          // CEF's
    [InlineData("https://new.example/", false, "OperationCanceled", false)]       // the target itself refused
    [InlineData("https://new.example", false, "ERR_ABORTED", false)]              // the same address, unparsed
    [InlineData("https://old.example/slow", false, "ConnectionRefused", false)]   // a real failure ends the wait
    [InlineData("https://new.example/", false, "ConnectionAborted", false)]       // and a real abort of the target
    [InlineData("https://redirected.example/", true, "Unknown", false)]           // a success ends it, wherever it landed
    public void Only_the_abort_of_a_replaced_navigation_is_skipped(string uri, bool success, string status, bool superseded) =>
        Assert.Equal(superseded, SessionNavigation.Superseded(new SessionNavigationResult(uri, success, status), "https://new.example/"));

    [Fact]
    public void The_pool_reset_skips_the_abort_of_the_lease_it_replaces()
    {
        Assert.True(SessionNavigation.Superseded(new SessionNavigationResult("https://page.example/", false, "ERR_ABORTED"), "about:blank"));
        Assert.False(SessionNavigation.Superseded(new SessionNavigationResult("about:blank", true, "Unknown"), "about:blank"));
    }
}
