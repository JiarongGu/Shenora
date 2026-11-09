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

    /// <summary><c>Target.setAutoAttach</c>'s parameters: flat sessions, which a message names by <c>sessionId</c>, and no wait for the
    /// frame to be released, so a frame that never answers is never held back.</summary>
    internal const string AutoAttachParameters = """{"autoAttach":true,"waitForDebuggerOnStart":false,"flatten":true}""";

    /// <summary>
    /// The script on every frame of another site too. <c>Page.addScriptToEvaluateOnNewDocument</c> reaches the page's own process,
    /// and a frame that Chromium puts in a process of its own is a DevTools target of its own (measured on real CEF: a Ctrl+wheel over
    /// one zoomed the page, its listener having none of the script). So each such frame is attached to as it appears, and given
    /// the script, and the same attachment for the frames inside it. Every call is made at once, on CEF's UI thread, where the event
    /// arrives; <paramref name="failed"/> hears of one the browser refuses, since the frame then stays zoomable.
    /// </summary>
    internal static void GuardFrames(DevToolsChannel channel, Action<Exception> failed)
    {
        void Observe(Task<string> call) =>
            _ = call.ContinueWith(t => failed(t.Exception!.GetBaseException()), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        channel.Subscribe("Target.attachedToTarget", parameters =>
        {
            string? session;
            try
            {
                using var document = JsonDocument.Parse(parameters);
                if (document.RootElement.GetProperty("targetInfo").GetProperty("type").GetString() != "iframe") return;
                session = document.RootElement.GetProperty("sessionId").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return; }
            if (session is null) return;
            Observe(channel.CallAsync("Target.setAutoAttach", AutoAttachParameters, session));
            Observe(channel.CallAsync("Page.addScriptToEvaluateOnNewDocument", AddScriptParameters(), session));
        });
        Observe(channel.CallAsync("Target.setAutoAttach", AutoAttachParameters));
    }
}
