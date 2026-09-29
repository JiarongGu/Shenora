namespace Shenora.Sample.Maui;

/// <summary>
/// Does a webview whose HANDLER was released come back when its view returns to the tree?
///
/// <para>
/// <b>The case <c>MobileIpcBridgeOptions.ReleaseHandlerOnDispose</c> was feared to break.</b> The default is
/// ON because a configuration change killed the app 8 times in 10 with the handler left connected (0 in 10
/// after). But the release fires from the bridge's <c>Dispose</c>, which an app calls from <c>Unloaded</c> —
/// and <c>Unloaded</c> also fires when a page merely NAVIGATES AWAY and will be back. Measured with the
/// <c>navigate</c> arms (<c>docs/design/mobile-shells.md</c>): the view comes back either way, and its
/// document reloads whoever releases the handler.
/// </para>
///
/// <para>
/// ⚠ <b>It asks the question WITHOUT a <c>NavigationPage</c>, deliberately.</b> Wrapping the sample in one
/// would change its layout semantics — a navigation bar, and different safe-area behaviour — and the sample
/// is the safe-area probe too. Three modes instead: <c>1</c> releases the handler and re-parents the view,
/// which is NOT what a navigation does and throws; <c>navigate</c> swaps the window's page away and back,
/// which is, and leaves the release to the page's own <c>Unloaded</c>; <c>navigate-keep</c> is its control,
/// with nothing releasing the handler.
/// </para>
///
/// <para>
/// 🔴 <b>THREE OUTCOMES, and the middle one is the one nobody would think to look for.</b> A rebuilt
/// handler brings a NEW platform webview, which re-navigates — so "the page answers again" does not mean
/// "the page came back". A stamp taken before the release is what separates them, and an adopter's SPA
/// losing its state is a real cost even where nothing crashes.
/// </para>
///
/// <para>
/// ⚠ <b>DESTRUCTIVE, so it is opt-in per launch and runs last.</b> It tears down the live webview; every
/// probe after it would be measuring the wreckage.
/// </para>
/// </summary>
public static class HandlerReleaseProbe
{
    /// <summary>A global the page never defines, set before the handler goes and looked for afterwards.</summary>
    private const string Stamp = "__shenoraHandlerProbe";

    /// <summary>
    /// True once the navigation mode has swapped the page away. The page's own <c>OnLoaded</c> runs again on
    /// the way back, and it reads this to skip the probe suite rather than re-entering this probe.
    /// </summary>
    public static bool Navigated { get; private set; }

    /// <summary>
    /// False for the <c>navigate-keep</c> mode, the control: nothing releases the handler, so the two navigation
    /// runs differ in the release alone. Read by the page, which then turns off BOTH releases — its bridge's, and
    /// MAUI's own <c>HandlerDisconnectPolicy.Automatic</c>, which released the handler on a page swap with the
    /// bridge's turned off (measured, API 36).
    /// </summary>
    public static bool ReleasesHandler => Mode(_ => { }) != "navigate-keep";

    private static string SwitchFile => Path.Combine(FileSystem.CacheDirectory, "handler-release");

    /// <summary>
    /// The launch's mode, or null. 🔴 TWO TRIGGERS, and the reason is <c>PageProbe.ServeDocumentFromDisk</c>'s:
    /// <c>simctl</c> passes an environment variable to the app it launches and <c>adb</c> has no equivalent, so
    /// an env-var-only switch would make this iOS-only. A FILE works on both.
    /// </summary>
    private static string? Mode(Action<string> log)
    {
        var mode = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HANDLER_RELEASE");
        if (!string.IsNullOrWhiteSpace(mode)) return mode.Trim();
        try { return File.Exists(SwitchFile) ? File.ReadAllText(SwitchFile).Trim() : null; }
        catch (Exception ex)
        {
            log($"HANDLER: could not read {SwitchFile} ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Evaluate, but never wait for ever.
    ///
    /// <para>
    /// 🔴 <b>MEASURED, and it is why this helper exists: an evaluation against a webview whose handler has
    /// been disconnected NEVER COMPLETES.</b> It does not throw and it does not return null — MAUI's
    /// command mapper has no platform view to hand the script to, so the task simply stays pending. The
    /// first run of this probe hung there with the app alive and healthy, and reported nothing at all.
    /// </para>
    /// <para>
    /// ⚠ <b>A timeout and a failure are DIFFERENT ANSWERS</b>, so they are returned separately.
    /// <c>PageProbe.EvaluateAsync</c> already turns a failed evaluation into <c>null</c>, and collapsing
    /// "it refused" into "it never answered" would hide exactly the distinction this probe is for.
    /// </para>
    /// <para>
    /// ⚠ The abandoned task stays pending — nothing here can cancel it. Acceptable in a probe that is
    /// about to tear the webview down anyway; it would not be acceptable in shipped code.
    /// </para>
    /// </summary>
    private static async Task<(string? Value, bool TimedOut)> EvaluateWithin(
        HybridWebView webView, string script, TimeSpan budget)
    {
        var evaluation = PageProbe.EvaluateAsync(webView, script);
        var finished = await Task.WhenAny(evaluation, Task.Delay(budget)).ConfigureAwait(false);
        return ReferenceEquals(finished, evaluation)
            ? (await evaluation.ConfigureAwait(false), false)
            : (null, true);
    }

    /// <summary>
    /// Run it, if this launch asked. Returns the verdict line; the caller logs it.
    /// </summary>
    /// <param name="webView">The live webview — the one the bridge released.</param>
    /// <param name="host">The layout it sits in, so it can be taken out and put back.</param>
    /// <param name="log">Sink for the step-by-step, which is most of the value when the verdict is bad.</param>
    /// <param name="page">The page holding the webview, for the <c>navigate</c> mode.</param>
    public static async Task<string> RunAsync(HybridWebView webView, Layout? host, Action<string> log, Page? page = null)
    {
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(log);

        var mode = Mode(log);

        // Say what it saw, always — "not asked" and "asked, and the value never arrived" are otherwise the
        // same silence, and the second is the likely one (the value has to survive a launcher that is not
        // this process's shell).
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "HANDLER: SKIPPED — this launch did not ask. Set SHENORA_SAMPLE_HANDLER_RELEASE=1 (re-parent), "
                 + $"=navigate or =navigate-keep, or write that to {SwitchFile}.";
        }

        if (host is null) return "HANDLER: FAIL — the webview has no layout to leave and re-enter";

        // 1. THE CONTROL. Without it a dead page after the release reads as the release's fault when it may
        //    have been dead already — the "the app had never launched" mistake, one probe over.
        var before = await EvaluateWithin(
            webView, $"(window.{Stamp} = 'alive', window.{Stamp})", TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        if (before.Value != "alive")
        {
            return "HANDLER: FAIL — the page did not answer BEFORE anything was released "
                 + $"({(before.TimedOut ? "it never answered" : $"got '{before.Value ?? "a refusal"}'")}), "
                 + "so this run can say nothing about the release";
        }
        log("HANDLER: the page answers and is stamped — starting");

        if (mode is "navigate" or "navigate-keep")
            return await NavigateAsync(webView, page, log, expectRelease: mode == "navigate").ConfigureAwait(false);

        // 2. Release it exactly as the bridge's Dispose does.
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            webView.Handler?.DisconnectHandler();
            log("HANDLER: DisconnectHandler() called on the live webview");
        }).ConfigureAwait(false);

        var whileGone = await EvaluateWithin(webView, $"window.{Stamp} || 'no-stamp'", TimeSpan.FromSeconds(5))
            .ConfigureAwait(false);
        log("HANDLER: with the handler gone the page "
            + (whileGone.TimedOut
                ? "NEVER ANSWERS — the evaluation hangs rather than failing"
                : $"answers '{whileGone.Value ?? "a refusal"}'"));

        // 3. Bring the view back the way a navigation would — out of the tree and in again, which is what
        //    makes MAUI build a new handler.
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var index = host.Children.IndexOf(webView);
                host.Children.Remove(webView);
                host.Children.Insert(index < 0 ? host.Children.Count : index, webView);
                log($"HANDLER: the view left the layout and returned at index {index}");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 🔴 NOT A VERDICT ABOUT THE PLATFORM — the probe ran out of road, and saying otherwise would
            // be the worst kind of diagnostic (`probe-diagnostics`). Measured 2026-09-04:
            // `InvalidOperationException: MauiContext should have been set on parent`. Re-adding a
            // handler-less view to its layout is NOT what a navigation does; MAUI unloads and reloads a
            // PAGE, and the view keeps its parent throughout. So this shortcut cannot answer the question
            // it was written for — the `navigate` mode is the mechanic that can.
            return $"HANDLER: INCONCLUSIVE — re-parenting is not the navigation mechanic and it threw "
                 + $"{ex.GetType().Name}: {ex.Message}. What IS measured above: the release does not crash "
                 + "the app, and an evaluation against the released view hangs for ever.";
        }

        return await VerdictAsync(webView, log).ConfigureAwait(false);
    }

    /// <summary>
    /// The mechanic a navigation really uses: the WINDOW's page goes away and comes back, the same page
    /// instance, so MAUI raises <c>Unloaded</c> and <c>Loaded</c> on it while the webview keeps its parent.
    /// Nothing here releases the handler: the page's bridge does on <c>Unloaded</c>, and MAUI does on its own.
    /// </summary>
    private static async Task<string> NavigateAsync(HybridWebView webView, Page? page, Action<string> log, bool expectRelease)
    {
        log(expectRelease
            ? "HANDLER: navigate — the bridge releases the handler on unload (the default)"
            : "HANDLER: navigate-keep — the CONTROL: the bridge keeps the handler on unload");
        if (page is null) return "HANDLER: FAIL — the navigate mode needs the page, and none was passed";

        Window? window = null;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (page.Window is not { } current || !ReferenceEquals(current.Page, page)) return;
            window = current;
            Navigated = true;
            current.Page = new ContentPage { Content = new Label { Text = "away — the navigation probe" } };
            log("HANDLER: the window's page swapped AWAY");
        }).ConfigureAwait(false);
        if (window is null) return "HANDLER: FAIL — the page is not the window's page, so there is nothing to navigate from";

        // Long enough for the page's Unloaded to have run and the handler to be gone.
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var released = await MainThread.InvokeOnMainThreadAsync(() => webView.Handler is null).ConfigureAwait(false);
        log($"HANDLER: the handler is {(released ? "RELEASED" : "STILL CONNECTED")} while away");

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            window.Page = page;
            log("HANDLER: the window's page swapped BACK");
        }).ConfigureAwait(false);

        var verdict = await VerdictAsync(webView, log).ConfigureAwait(false);
        // A run whose release did not match its mode measured something else than it says.
        if (released != expectRelease)
        {
            return $"HANDLER: INCONCLUSIVE — the handler was {(released ? "released" : "kept")} in a run meant to "
                 + $"{(expectRelease ? "release" : "keep")} it. ({verdict})";
        }
        return expectRelease ? verdict : $"HANDLER: CONTROL — {verdict}";
    }

    /// <summary>Poll the returned view for the stamp and say which of the three outcomes it was.</summary>
    private static async Task<string> VerdictAsync(HybridWebView webView, Action<string> log)
    {
        // MAUI realizes on the next layout pass, and the platform webview then has to navigate before it
        // can answer anything. Poll rather than sleeping once — a fixed sleep either wastes the run or
        // reports a verdict about a page that had not finished loading.
        string? after = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var poll = await EvaluateWithin(webView, $"window.{Stamp} || 'no-stamp'", TimeSpan.FromSeconds(3))
                .ConfigureAwait(false);
            if (poll.Value is not null)
            {
                after = poll.Value;
                log($"HANDLER: answered on attempt {attempt} with '{after}'");
                break;
            }
            log($"HANDLER: attempt {attempt} — {(poll.TimedOut ? "no answer" : "refused")}");
        }

        return after switch
        {
            // ⚠ Outcomes only. Which release caused one is a comparison between arms, never one run's verdict:
            // an earlier text blamed the release for a reload the control then showed with nothing released.
            "alive" => "HANDLER: PASS — the view came back and kept its document",
            "no-stamp" => "HANDLER: PARTIAL — the view came back but its document RELOADED (the stamp is gone)",
            null => "HANDLER: FAIL — the view never answered again",
            _ => $"HANDLER: FAIL — unexpected answer '{after}'",
        };
    }
}
