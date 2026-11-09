using System.Text.Json;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// Zoom turned off for a page, as the WebView2 host does (<c>IsZoomControlEnabled</c>, <c>IsPinchZoomEnabled</c>, its shortcut
/// script), unless the app sets <see cref="ChromiumHostOptions.AllowZoom"/>. CEF offers no setting for it, so each input has its
/// own answer: a key is refused in the client's keyboard handler (<see cref="BlocksKey"/>), where Chromium and the page never
/// hear it; a wheel and a pinch are refused by a script in every document (<see cref="GuardScript"/>), CEF having no wheel
/// callback; a page's own <c>zoom</c> property and a call to <c>set_zoom_level</c> are not zoom inputs and are left alone.
/// </summary>
internal static class ChromiumZoom
{
    /// <summary>
    /// Ctrl+wheel, which a precision touchpad's pinch also raises, is cancelled where it starts, so Chromium's own zoom never
    /// runs; the page still receives the event. A touch screen's pinch scales the visual viewport, and a document that allows
    /// only panning has none of it. The listener is non-passive and on the capture phase, or it could not cancel a wheel the
    /// page handles itself; the document has no head when the script runs, hence the adopted sheet.
    /// </summary>
    internal const string GuardScript = """
        (function () {
            window.addEventListener('wheel', function (e) {
                if (e.ctrlKey || e.metaKey) e.preventDefault();
            }, { capture: true, passive: false });
            try {
                var sheet = new CSSStyleSheet();
                sheet.replaceSync('html { touch-action: pan-x pan-y; }');
                document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
            } catch (e) { }
        })();
        """;

    // The keys the WebView2 host's script blocks for zoom: plus, minus and 0 with Ctrl (Command on a Mac), on the main row and the
    // numpad. Windows virtual-key codes, which CEF fills in on every OS.
    private const int Vk0 = 0x30, VkNumpad0 = 0x60, VkAdd = 0x6B, VkSubtract = 0x6D, VkOemPlus = 0xBB, VkOemMinus = 0xBD;

    /// <summary>
    /// Whether a key event is a zoom shortcut, for any of the four events one press raises: nothing the page hears is left to
    /// tell it a zoom was asked for.
    /// </summary>
    internal static bool BlocksKey(cef_key_event_type_t type, uint modifiers, int windowsKeyCode)
    {
        const uint ctrlOrCommand = (uint)cef_event_flags_t.EVENTFLAG_CONTROL_DOWN | (uint)cef_event_flags_t.EVENTFLAG_COMMAND_DOWN;
        return (modifiers & ctrlOrCommand) != 0
            && windowsKeyCode is Vk0 or VkNumpad0 or VkAdd or VkSubtract or VkOemPlus or VkOemMinus;
    }

    /// <summary>The <c>Page.addScriptToEvaluateOnNewDocument</c> parameters: every document from now on, and the one open now.</summary>
    internal static string AddScriptParameters() => JsonSerializer.Serialize(new { source = GuardScript, runImmediately = true });
}
