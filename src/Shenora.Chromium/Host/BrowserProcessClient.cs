using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// The client of every browser in a process that is only Chromium's browser (D86). Chrome's UI makes each one itself,
/// a tab, a CDP target or a page's popup, and this counts them, so the process ends when the last closes and can close
/// them all when it is asked to stop. It handles nothing else: a handler here would replace Chrome's own UI for
/// downloads, permissions and popups. CEF's UI thread throughout.
/// </summary>
internal sealed unsafe class BrowserProcessClient : CefObject<_cef_client_t>
{
    private readonly LifeSpan _lifeSpan;
    private readonly ILogger? _log;
    // By identifier, each with the reference on_after_created passed: CEF may wrap one browser in more than one struct.
    private readonly Dictionary<int, nint> _browsers = [];
    private bool _opened;
    private bool _stopping;

    public BrowserProcessClient(ILogger? log)
    {
        _log = log;
        _lifeSpan = new LifeSpan(this);
        Struct->get_life_span_handler = &GetLifeSpan;
    }

    /// <summary>Browsers open now.</summary>
    public int Count => _browsers.Count;

    /// <summary>Close every browser, skipping their pages' <c>beforeunload</c>, and quit once none is left; with none
    /// open, quit now.</summary>
    public void CloseAll()
    {
        _stopping = true;
        if (_browsers.Count == 0)
        {
            Cef.cef_quit_message_loop();
            return;
        }
        AppCallback.Log(_log, () => $"[Shenora.Chromium] Browser process stopping: closing {_browsers.Count} browser(s)");
        foreach (var handle in _browsers.Values.ToArray())
        {
            var browser = (_cef_browser_t*)handle;
            using var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser));
            if (!host.IsNull) host.Ptr->close_browser(host.Ptr, 1);
        }
    }

    private void Created(_cef_browser_t* browser)
    {
        _browsers[browser->get_identifier(browser)] = (nint)browser;
        _opened = true;
    }

    private void Closing(_cef_browser_t* browser)
    {
        if (_browsers.Remove(browser->get_identifier(browser), out var kept))
            new CefRef<_cef_browser_t>((_cef_browser_t*)kept).Dispose();
        if (_browsers.Count == 0 && (_opened || _stopping))
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] Browser process: the last browser closed");
            Cef.cef_quit_message_loop();
        }
    }

    [UnmanagedCallersOnly]
    private static _cef_life_span_handler_t* GetLifeSpan(_cef_client_t* self) => From<BrowserProcessClient>(self)._lifeSpan.ForCef();

    private sealed class LifeSpan : CefObject<_cef_life_span_handler_t>
    {
        private readonly BrowserProcessClient _owner;

        public LifeSpan(BrowserProcessClient owner)
        {
            _owner = owner;
            Struct->on_after_created = &AfterCreated;
            Struct->on_before_close = &BeforeClose;
        }

        [UnmanagedCallersOnly]
        private static void AfterCreated(_cef_life_span_handler_t* self, _cef_browser_t* browser) =>
            AppCallback.Run(() => From<LifeSpan>(self)._owner.Created(browser));   // the argument's reference is kept

        [UnmanagedCallersOnly]
        private static void BeforeClose(_cef_life_span_handler_t* self, _cef_browser_t* browser)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            AppCallback.Run(() => From<LifeSpan>(self)._owner.Closing(browser));
        }
    }
}
