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

        try
        {
            var written = new ClipboardContent
            {
                Text = Sentinel,
                Formats = new Dictionary<string, ReadOnlyMemory<byte>>
                {
                    [ClipboardContent.Html] = Encoding.UTF8.GetBytes($"<b>{Sentinel}</b>"),
                    [CustomType] = Encoding.UTF8.GetBytes("custom-payload"),
                },
            };
            await clipboard.SetAsync(written);
            log($"[CLIPBOARD] wrote text + {written.Formats.Count} format(s)");

            var read = await clipboard.GetAsync();
            // ⚠ Each reported SEPARATELY rather than as one pass/fail. A platform that keeps the text and
            // drops the custom type is a different answer from one that keeps everything, and collapsing
            // them would hide exactly the case this exists to find.
            log($"[CLIPBOARD] text     : {(read.Text == Sentinel ? "round-tripped" : $"CHANGED -> {Describe(read.Text)}")}");
            log($"[CLIPBOARD] html     : {(read.Formats.ContainsKey(ClipboardContent.Html) ? "present" : "DROPPED")}");
            log($"[CLIPBOARD] custom   : {(read.Formats.ContainsKey(CustomType) ? "present" : "DROPPED")}"
                + $"   ({CustomType})");
            log($"[CLIPBOARD] formats back: {(read.Formats.Count == 0 ? "(none)" : string.Join(", ", read.Formats.Keys))}");
            log("[CLIPBOARD] CROSS-CHECK: `xcrun simctl pbpaste booted` should print the sentinel — that is a "
                + "FOREIGN reader, and the only one that proves another app could read this.");
        }
        catch (Exception ex)
        {
            // A refusal is an ANSWER on this surface (Files on a phone is a documented throw), so the type
            // is named rather than swallowed — but it must not take the app down on startup.
            log($"[CLIPBOARD] refused: {ex.GetType().Name} — {ex.Message}");
        }
    }

    private static string Describe(string? value) =>
        value is null ? "(null)" : value.Length > 40 ? value[..40] + "…" : value;
}
