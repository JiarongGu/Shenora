using System.Text;
using Shenora.Core.Shell;

namespace Shenora.Sample.Maui;

/// <summary>
/// The pasteboard round trip, run at STARTUP and reported to the log.
///
/// <para>
/// 🔴 <b>A probe rather than a button, because the button could not be pressed.</b> Driving the sample's
/// web control needs synthetic input, and on a simulator over ssh that does not reach the WebView's
/// content — measured 2026-08-19: a mapped <c>cliclick</c> and a following <c>return</c> both left the
/// pasteboard untouched and the app silent, with Accessibility granted and the window geometry readable.
/// Reported from startup instead, the whole test becomes <c>deploy</c> then read the log, which needs no
/// GUI automation and no human.
/// </para>
///
/// <para>
/// ⚠ <b>The questions it answers are the ones a COMPILE cannot.</b> Whether several media types on one
/// item survive as one item; whether an app's own <c>application/…</c> type is accepted by the platform
/// at all or silently dropped; and — the one a self round-trip cannot answer — whether what lands is
/// readable by anything ELSE. That last one is why the text is a recognisable sentinel: read it back from
/// outside with <c>xcrun simctl pbpaste booted</c>, which is a foreign reader, not this app being asked
/// about itself.
/// </para>
/// </summary>
internal static class ClipboardProbe
{
    /// <summary>An app's OWN type, which is the interesting case — the kit must carry what it does not know.</summary>
    private const string CustomType = "application/x-shenora-probe";

    /// <summary>Recognisable in `pbpaste` output, and obviously ours if it turns up anywhere unexpected.</summary>
    internal const string Sentinel = "SHENORA-CLIPBOARD-PROBE";

    internal static async Task RunAsync(IClipboardService? clipboard, Action<string> log)
    {
        if (clipboard is null)
        {
            log("[CLIPBOARD] no IClipboardService registered — nothing to probe");
            return;
        }

        log("[CLIPBOARD] the question: does a multi-format item survive a round trip, and is it readable "
            + "from OUTSIDE this app?");

        // 🔴 TWO round trips, not one, because `SetAsync` is ALL-OR-NOTHING by contract — it refuses the
        // whole content rather than writing part of it. Asked together, Android's refusal of an app's own
        // media type aborted the write and the `text/html` question never got asked at all; the run
        // reported one exception and answered nothing. Measured on an emulator 2026-08-19.
        await Round(clipboard, log, "well-known", new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [ClipboardContent.Html] = Encoding.UTF8.GetBytes($"<b>{Sentinel}</b>"),
        });

        // ⚠ Expected to DIFFER per platform, which is the point: iOS's pasteboard takes an arbitrary UTI
        // and Android's `ClipData` does not. A refusal here is an ANSWER, not a failure.
        await Round(clipboard, log, "app's own", new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [CustomType] = Encoding.UTF8.GetBytes("custom-payload"),
        });

        log("[CLIPBOARD] CROSS-CHECK: read the pasteboard from OUTSIDE — `xcrun simctl pbpaste booted`, or "
            + "`adb shell service call clipboard` — which is the only reader that proves another app sees it.");
    }

    /// <summary>One write/read pair, reported on its own so a refusal of one cannot hide the rest.</summary>
    private static async Task Round(IClipboardService clipboard, Action<string> log, string label,
                                    Dictionary<string, ReadOnlyMemory<byte>> formats)
    {
        var name = string.Join(", ", formats.Keys);
        try
        {
            await clipboard.SetAsync(new ClipboardContent { Text = Sentinel, Formats = formats });
            var read = await clipboard.GetAsync();

            log($"[CLIPBOARD] {label,-10} text : "
                + (read.Text == Sentinel ? "round-tripped" : $"CHANGED -> {Describe(read.Text)}"));
            foreach (var key in formats.Keys)
            {
                log($"[CLIPBOARD] {label,-10} {key} : {(read.Formats.ContainsKey(key) ? "present" : "DROPPED")}");
            }
            log($"[CLIPBOARD] {label,-10} back : "
                + (read.Formats.Count == 0 ? "(no formats)" : string.Join(", ", read.Formats.Keys)));
        }
        catch (Exception ex)
        {
            // A refusal is an ANSWER on this surface — `Files` on a phone is a documented throw — so the
            // type is named rather than swallowed, and it must not take the app down on startup.
            log($"[CLIPBOARD] {label,-10} REFUSED ({name}): {ex.GetType().Name} — {ex.Message}");
        }
    }

    private static string Describe(string? value) =>
        value is null ? "(null)" : value.Length > 40 ? value[..40] + "…" : value;
}
