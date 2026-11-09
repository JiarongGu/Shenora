using System.Drawing;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Sessions;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium session browser's history (<see cref="ISessionBrowserHistory"/>), as far as a test reaches without CEF's
/// UI thread: it is offered, and a browser CEF has not made yet has nowhere to go and steps nowhere. A step in a real
/// browser is proven by the sample's panel probe.
/// </summary>
public class ChromiumSessionBrowserHistoryTests
{
    private static ChromiumSessionBrowser Browser() =>
        new(new SessionBrowserOptions { ProfileDirectory = "profile" }, () => null, null, new Size(100, 100),
            new CefUiDispatcher(_ => false, () => false));

    [Fact]
    public void The_Chromium_session_browser_offers_its_history()
    {
        Assert.IsAssignableFrom<ISessionBrowserHistory>(Browser());
    }

    [Fact]
    public void A_browser_CEF_has_not_made_has_no_history_to_step_through()
    {
        var history = Assert.IsAssignableFrom<ISessionBrowserHistory>(Browser());

        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        history.GoBack();
        history.GoForward();
        history.Reload();
        history.Stop();
    }
}
