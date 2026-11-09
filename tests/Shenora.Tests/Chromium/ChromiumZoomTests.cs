using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The WebView2 host turns Ctrl+wheel, Ctrl+plus/minus/0 and pinch zoom off (<c>IsZoomControlEnabled</c>,
/// <c>IsPinchZoomEnabled</c>, the shortcut script), so a page zooms the same on both engines: not at all, unless the app
/// sets <c>AllowZoom</c>. One test for each input; none runs CEF, so what the handler and the script do inside a real
/// Chromium is <c>docs/design/shells.md</c>'s unmeasured part.
/// </summary>
public unsafe class ChromiumZoomTests
{
    private const uint Ctrl = (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN;
    private const uint Command = (uint)cef_event_flags_t.EVENTFLAG_COMMAND_DOWN;
    private const uint Shift = (uint)cef_event_flags_t.EVENTFLAG_SHIFT_DOWN;

    private const int Vk0 = 0x30, VkNumpad0 = 0x60, VkAdd = 0x6B, VkSubtract = 0x6D, VkOemPlus = 0xBB, VkOemMinus = 0xBD, VkA = 0x41;

    [Fact]
    public void The_options_default_to_zoom_off_as_the_WebView2_host_does()
    {
        Assert.False(new ChromiumHostOptions().AllowZoom);
        Assert.False(new ChromiumEngineOptions().AllowZoom);
    }

    // Ctrl+plus (Ctrl+'=' unshifted, Ctrl+Shift+'=' for the '+'), Ctrl+minus, Ctrl+0, and the numpad's: the keys the
    // WebView2 host's script blocks for zoom.
    [Theory]
    [InlineData(Ctrl, VkOemPlus)]
    [InlineData(Ctrl | Shift, VkOemPlus)]
    [InlineData(Ctrl, VkOemMinus)]
    [InlineData(Ctrl | Shift, VkOemMinus)]
    [InlineData(Ctrl, Vk0)]
    [InlineData(Ctrl, VkNumpad0)]
    [InlineData(Ctrl, VkAdd)]
    [InlineData(Ctrl, VkSubtract)]
    [InlineData(Command, VkOemPlus)]
    [InlineData(Command, VkOemMinus)]
    [InlineData(Command, Vk0)]
    public void A_zoom_key_is_blocked(uint modifiers, int keyCode) =>
        Assert.True(ChromiumZoom.BlocksKey(modifiers, keyCode), $"modifiers {modifiers} and key {keyCode} reached the page");

    [Theory]
    [InlineData(0u, VkOemPlus)]      // a typed '=' or '+'
    [InlineData(Shift, VkOemMinus)]  // a typed '_'
    [InlineData(0u, Vk0)]
    [InlineData(Ctrl, VkA)]          // select all, which the page keeps
    [InlineData(Ctrl | Shift, VkA)]
    public void Any_other_key_reaches_the_page(uint modifiers, int keyCode) =>
        Assert.False(ChromiumZoom.BlocksKey(modifiers, keyCode));

    [Fact]
    public void The_client_blocks_a_zoom_key_in_the_keyboard_handler_before_the_page_hears_it()
    {
        var browser = ChromiumDropZonesTests.Window();
        var client = browser.ClientForCef();
        Assert.True(client->get_keyboard_handler != null, "the client offers CEF no keyboard handler");
        var handler = client->get_keyboard_handler(client);
        Assert.True(handler != null, "the client's keyboard handler getter returned none, so Ctrl+plus zooms the page");
        Assert.True(handler->on_pre_key_event != null, "the keyboard handler does not answer on_pre_key_event");

        Assert.Equal(1, PreKey(handler, Ctrl, VkOemPlus));   // handled: neither Chromium nor the page sees it
        Assert.Equal(1, PreKey(handler, Ctrl, VkOemMinus));
        Assert.Equal(1, PreKey(handler, Ctrl, Vk0));
        Assert.Equal(0, PreKey(handler, 0, VkOemPlus));
        Assert.Equal(0, PreKey(handler, Ctrl, VkA));

        ((_cef_base_ref_counted_t*)handler)->release((_cef_base_ref_counted_t*)handler);
        ((_cef_base_ref_counted_t*)client)->release((_cef_base_ref_counted_t*)client);
    }

    [Fact]
    public void With_zoom_allowed_the_client_leaves_every_key_to_Chromium()
    {
        var browser = ChromiumDropZonesTests.Window(allowZoom: true);
        var client = browser.ClientForCef();
        Assert.True(client->get_keyboard_handler != null);
        var handler = client->get_keyboard_handler(client);
        Assert.True(handler == null, "a keyboard handler stands with zoom allowed");
        ((_cef_base_ref_counted_t*)client)->release((_cef_base_ref_counted_t*)client);
    }

    private sealed class FakeBrowser : CefObject<_cef_browser_t>;

    private static int PreKey(_cef_keyboard_handler_t* handler, uint modifiers, int keyCode)
    {
        var cefBrowser = new FakeBrowser();
        var key = new _cef_key_event_t
        {
            size = (nuint)sizeof(_cef_key_event_t), type = cef_key_event_type_t.KEYEVENT_RAWKEYDOWN, modifiers = modifiers, windows_key_code = keyCode,
        };
        var shortcut = 0;
        var handled = handler->on_pre_key_event(handler, cefBrowser.ForCef(), &key, default, &shortcut);
        cefBrowser.Release();
        return handled;
    }

    // Ctrl+wheel, and a precision touchpad's pinch, which Chromium raises as a Ctrl+wheel: CEF has no wheel callback, so the page's own
    // listener, set up in every document, is what refuses it.
    [Fact]
    public void The_page_script_cancels_a_ctrl_or_command_wheel_before_the_page_does_anything_with_it()
    {
        var script = ChromiumZoom.GuardScript;
        Assert.Contains("addEventListener('wheel'", script);
        Assert.Contains("e.ctrlKey || e.metaKey", script);
        Assert.Contains("e.preventDefault()", script);
        // Chromium treats a wheel listener on the window as passive unless told otherwise, and cannot cancel a passive one.
        Assert.Contains("passive: false", script);
        Assert.Contains("capture: true", script);
    }

    // A touch screen's pinch scales the page's visual viewport; touch-action without pinch-zoom is what disables it, as
    // IsPinchZoomEnabled does in WebView2.
    [Fact]
    public void The_page_script_takes_pinch_zoom_off_the_whole_document()
    {
        var script = ChromiumZoom.GuardScript;
        Assert.Contains("touch-action: pan-x pan-y", script);
        Assert.Contains("adoptedStyleSheets", script);   // the document has no head yet when the script runs
    }

    [Fact]
    public void The_script_is_registered_for_every_new_document_and_the_one_already_open()
    {
        var parameters = System.Text.Json.JsonDocument.Parse(ChromiumZoom.AddScriptParameters()).RootElement;
        Assert.Equal(ChromiumZoom.GuardScript, parameters.GetProperty("source").GetString());
        Assert.True(parameters.GetProperty("runImmediately").GetBoolean());
    }
}
