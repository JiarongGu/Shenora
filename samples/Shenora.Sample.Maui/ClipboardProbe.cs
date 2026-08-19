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

    /// <summary>
    /// How long to wait before touching the clipboard at all. Generous rather than tuned — the cost of
    /// being too short is a WRONG ANSWER that reads as a platform limitation.
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(12);

    internal static async Task RunAsync(IClipboardService? clipboard, Action<string> log)
    {
        if (clipboard is null)
        {
            log("[CLIPBOARD] no IClipboardService registered — nothing to probe");
            return;
        }

        log("[CLIPBOARD] the question: does a multi-format item survive a round trip, and is it readable "
            + "from OUTSIDE this app?");

        // 🔴 WAIT FOR FOCUS BEFORE MEASURING ANYTHING. Android restricts clipboard READS to the focused
        // app, and this runs from startup — so an early read answers as though the clipboard held plain
        // text no matter what is on it. Two conclusions were drawn from exactly that and both were
        // wrong: first "Android drops text/html", then a kit-side check that refused a write which had
        // actually succeeded. The same control call answered differently on consecutive runs, which is
        // the tell that the INSTRUMENT was moving, not the thing being measured.
        await Task.Delay(SettleDelay);

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

        // ⚠ ONE control, deliberately. Variants of it (two in a row, one on the main thread) were an
        // EXPERIMENT and the experiment is finished — its record is in TASKS.md, because three
        // near-identical calls in a probe are something nobody will maintain and everybody will
        // misread as a claim.
        await ControlWrite(log, "control", onMainThread: false);

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
            // 🔴 Ask the PLATFORM what it is holding, immediately after the write and before the kit's own
            // read. `text/html` came back missing on Android and three different faults produce that:
            // never written, replaced by something else, or written and not readable back. Only the clip's
            // own description separates them, and each needs a different fix.
            await DescribePlatformClip(label, log);
            var read = await clipboard.GetAsync();

            log($"[CLIPBOARD] {label,-10} text : "
                + (read.Text == Sentinel ? "round-tripped" : $"CHANGED -> {Describe(read.Text)}"));
            foreach (var key in formats.Keys)
            {
                log($"[CLIPBOARD] {label,-10} {key} : {(read.Formats.ContainsKey(key) ? "present" : "DROPPED")}");
            }
            log($"[CLIPBOARD] {label,-10} back : "
                + (read.Formats.Count == 0 ? "(no formats)" : string.Join(", ", read.Formats.Keys)));

            // 🔴 THE SAME READ, FIVE SECONDS LATER, through the kit. Every "DROPPED" so far came from a
            // read taken immediately after the write, and `PrimaryClip` is a cross-process call — so the
            // one thing never tested is whether the data is simply not VISIBLE yet. If the late read
            // finds the format the early one missed, nothing was ever dropped and the probe was the bug.
            await Task.Delay(TimeSpan.FromSeconds(5));
            var late = await clipboard.GetAsync();
            log($"[CLIPBOARD] {label,-10} late : "
                + (late.Formats.Count == 0 ? "(no formats)" : string.Join(", ", late.Formats.Keys))
                + $"  text={(late.Text == Sentinel ? "sentinel" : Describe(late.Text))}");
        }
        catch (Exception ex)
        {
            // A refusal is an ANSWER on this surface — `Files` on a phone is a documented throw — so the
            // type is named rather than swallowed, and it must not take the app down on startup.
            log($"[CLIPBOARD] {label,-10} REFUSED ({name}): {ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>
    /// A CONTROL: write HTML with the platform API directly, bypassing the kit entirely, and report what
    /// the clipboard then says it holds.
    /// </summary>
    /// <remarks>
    /// 🔴 This is the one measurement that separates the last two explanations for HTML vanishing on
    /// Android. If the control's clip declares <c>text/html</c>, the platform call works and the KIT is
    /// not reaching it; if the control declares only <c>text/plain</c>, then
    /// <c>ClipData.NewHtmlText</c> no longer describes itself as HTML at this API level and the kit is
    /// blameless. Same call, no kit in the path — which is what makes it a control rather than a
    /// second opinion.
    /// </remarks>
    private static async Task ControlWrite(Action<string> log, string label, bool onMainThread)
    {
#if ANDROID
        // The whole experiment is this line: identical work, one thread apart.
        if (onMainThread) { await MainThread.InvokeOnMainThreadAsync(() => ControlBody(log, label)); return; }
        ControlBody(log, label);
    }

    private static void ControlBody(Action<string> log, string label)
    {
        // 🔴 A SETTLE BETWEEN THE SET AND THE READ-BACK. Four hypotheses died here — platform drops HTML,
        // the kit is at fault, the writing THREAD matters, the ORDER matters — and each was killed by the
        // next measurement. What every one of them had in common was reading the clipboard immediately
        // after writing it. `PrimaryClip` is a cross-process call, so an immediate read is a race with the
        // service, and a racing instrument explains inconsistent results better than any property of the
        // write does. If a settled read is stable, every earlier "DROPPED" was this.
        System.Threading.Thread.Sleep(1200);
        try
        {
            var manager = (global::Android.Content.ClipboardManager?)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.ClipboardService);
            if (manager is null) { log($"[CLIPBOARD] {label,-12}: no ClipboardManager"); return; }

            manager.PrimaryClip = global::Android.Content.ClipData.NewHtmlText(
                "probe", Sentinel, $"<b>{Sentinel}</b>");

            var clip = manager.PrimaryClip;
            var description = clip?.Description;
            var mimes = description is null
                ? "(none)"
                : string.Join(", ", Enumerable.Range(0, description.MimeTypeCount)
                    .Select(i => description.GetMimeType(i)));
            var item = clip is { ItemCount: > 0 } ? clip.GetItemAt(0) : null;
            log($"[CLIPBOARD] {label,-12}: NewHtmlText direct -> mime=[{mimes}] "
                + $"htmlText={(item?.HtmlText is null ? "null" : $"{item.HtmlText.Length} chars")}"
                + $" main={MainThread.IsMainThread}");
        }
        catch (Exception ex)
        {
            log($"[CLIPBOARD] {label,-12}: failed — {ex.GetType().Name}");
        }
#else
        _ = log; _ = label;
#endif
    }

    /// <summary>
    /// What the platform clipboard says it is holding, straight from the OS rather than through the kit.
    /// </summary>
    /// <remarks>
    /// ⚠ Deliberately NOT routed through <see cref="IClipboardService"/>. The kit's read is one of the
    /// suspects, so asking it would be asking the accused — this goes to the platform object directly.
    /// </remarks>
    private static Task DescribePlatformClip(string label, Action<string> log)
    {
#if ANDROID
        try
        {
            var manager = (global::Android.Content.ClipboardManager?)global::Android.App.Application.Context
                .GetSystemService(global::Android.Content.Context.ClipboardService);
            var clip = manager?.PrimaryClip;
            if (clip is null) { log($"[CLIPBOARD] {label,-10} platform: PrimaryClip is NULL (read denied?)"); return Task.CompletedTask; }

            var description = clip.Description;
            var mimes = description is null
                ? "(no description)"
                : string.Join(", ", Enumerable.Range(0, description.MimeTypeCount)
                    .Select(i => description.GetMimeType(i)));
            var item = clip.ItemCount > 0 ? clip.GetItemAt(0) : null;
            log($"[CLIPBOARD] {label,-10} platform: items={clip.ItemCount} mime=[{mimes}] "
                + $"htmlText={(item?.HtmlText is null ? "null" : $"{item.HtmlText.Length} chars")}");
        }
        catch (Exception ex)
        {
            log($"[CLIPBOARD] {label,-10} platform: could not inspect — {ex.GetType().Name}");
        }
#else
        _ = label; _ = log;
#endif
        return Task.CompletedTask;
    }

    private static string Describe(string? value) =>
        value is null ? "(null)" : value.Length > 40 ? value[..40] + "…" : value;
}
