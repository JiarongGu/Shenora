using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;

namespace Shenora.Tests.Chromium;

/// <summary>
/// Tab past a page's last element is CEF asking the host to take the focus (<c>on_take_focus</c>). Left unanswered it
/// stayed in the page with nowhere to go (measured in a <c>ChromiumView</c>): so the client must hand CEF a focus
/// handler, and that handler must reach the page's host, direction intact.
/// </summary>
public unsafe class ChromiumFocusTests
{
    private sealed class RecordingHost : IChromiumBrowserHost
    {
        public readonly List<bool> Leaving = [];
        public void FocusLeaving(bool forward) => Leaving.Add(forward);
        public void DraggableRegionsChanged(nuint count, _cef_draggable_region_t* regions) { }
        public void TitleChanged(string title) { }
        public void DocumentStarted() { }
        public void BrowserCreated() { }
        public bool CloseRequested() => false;
        public void BrowserClosed() { }
    }

    /// <summary>The browser CEF passes into a callback, with the reference the callback releases.</summary>
    private sealed class FakeBrowser : CefObject<_cef_browser_t>;

    [Fact]
    public void Tab_leaving_the_page_asks_its_host_to_move_the_focus_on_in_that_direction()
    {
        var browser = ChromiumDropZonesTests.Window();
        var host = new RecordingHost();
        browser.Host = host;
        var client = browser.ClientForCef();
        // Checked before it is called: a null function pointer would take the whole test run down, not fail this test.
        Assert.True(client->get_focus_handler != null, "the client offers CEF no focus handler");
        var handler = client->get_focus_handler(client);
        var cefBrowser = new FakeBrowser();

        Assert.True(handler != null, "the client's focus handler getter returned none");
        Assert.True(handler->on_take_focus != null, "the focus handler does not answer on_take_focus");
        handler->on_take_focus(handler, cefBrowser.ForCef(), 1);   // Tab past the last element
        handler->on_take_focus(handler, cefBrowser.ForCef(), 0);   // Shift+Tab past the first

        Assert.Equal([true, false], host.Leaving);
        Assert.Equal(1, cefBrowser.References);   // each call released the reference CEF passed it

        ((_cef_base_ref_counted_t*)handler)->release((_cef_base_ref_counted_t*)handler);
        ((_cef_base_ref_counted_t*)client)->release((_cef_base_ref_counted_t*)client);
        cefBrowser.Release();
    }
}
