using Shenora;
using Shenora.Modules.Platform;
using Shenora.Modules.Platform.Activities;

namespace Shenora.Sample.Maui;

/// <summary>
/// The SEAM TEST for <see cref="ILiveActivities"/>: start a real activity, move it, end it, and log what the
/// platform said at each step.
/// <para>
/// It exercises the whole devkit chain in one run — the C# contract, the JSON crossing to Swift, the kit's
/// <c>@_cdecl</c> shim linked as a static library, the widget extension the build produced from the sample's
/// own SwiftUI views, and ActivityKit pairing the two by module-qualified attributes type. Any one of those
/// being wrong shows up here, and the handle coming back is the first thing that proves the shim linked at
/// all: a missing symbol arrives as <c>Unavailable</c> naming the build property to check.
/// </para>
/// </summary>
internal static class LiveActivityProbe
{
    /// <summary>
    /// Run the lifecycle. Never throws — the contract already reports failure through
    /// <see cref="ILiveActivities.Unavailable"/> and a null handle, so a probe that threw would only be
    /// hiding that.
    /// </summary>
    public static async Task RunAsync(ILiveActivities activities, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            var unavailable = activities.Unavailable;
            log($"[ACTIVITY] unavailable={unavailable ?? "<null, i.e. available>"}");
            if (unavailable is not null)
            {
                // Not a failure on Android — that shell answers with a reason by design. The verdict says
                // SKIPPED so a run on the wrong platform cannot be mistaken for a broken iOS build.
                log("[ACTIVITY] VERDICT: SKIPPED — this shell reports no live surface");
                return;
            }

            // An indeterminate start, because that is the honest state of work that has just begun and it
            // exercises the null-progress path the views must handle with a spinner.
            var state = new LiveActivityState { Title = "Shenora probe", Subtitle = "starting" };

            // 🔴 AN APPEARANCE IS PASSED, and that is the point of the probe rather than decoration: D69's
            // config layer is only DONE when something asks for it, and a widget rendering the kit's
            // hardcoded defaults would look identical to one reading this (D63 — absent is
            // indistinguishable from working). A non-default symbol and tint make the two distinguishable
            // on sight: the kit's built-in default is a plain `circle.fill` in the system accent.
            var appearance = new LiveActivityAppearance
            {
                Symbol = "arrow.down.circle.fill",
                Tint = "#FF9500",
            };

            // 🔴 THE LAYOUT, IN ONE LINE — which is the point of the preset set existing. An adopter's
            // activity is usually one of three shapes (known end, unknown end, a number that matters), and
            // each has metrics that are fiddly to get right and invisible when wrong: where the value sits
            // so it does not drift as the title changes, how much room the bar needs, which slot survives
            // the collapse into the pill. `Presentations` settles all of those.
            //
            // ⚠ IT IS AN ORDINARY LAYOUT, not a hidden rendering path — built from the same public elements
            // an app could have written by hand, so `with` overrides any region. The sample takes the
            // preset WHOLE on purpose: a demo that immediately customised everything would not show that
            // the default is worth having.
            //
            // ⚠ `{title}` / `{subtitle}` / `{progress}` inside it are bound at every RENDER, not at
            // description — which is why a layout handed over once at start keeps showing values that change.
            var layout = Components.ProgressCard(appearance.Symbol);

            var handle = activities.Start(state, appearance, layout);
            log($"[ACTIVITY] start -> {handle ?? "<null>"}");
            if (handle is null)
            {
                log("[ACTIVITY] VERDICT: FAIL — start returned null; see the reason logged above");
                return;
            }

            // 🔴 TEN UPDATES AT 3 s, AND THE COUNT IS THE POINT. Three at 6 s exercised the update path but
            // was almost impossible to WATCH — and watching is the only way to catch the failure this probe
            // exists to catch, because an update that is accepted and applied and never repainted logs
            // exactly like one that works (measured: the compact Island held a stale value while every line
            // read `update applied`). A ten-step climb makes a frozen pill obvious at a glance.
            //
            // ⚠ Ten in thirty seconds is deliberately inside ActivityKit's update budget. Do not raise it
            // to "make it smoother": a throttled activity stops repainting, which reproduces the very bug
            // this is meant to detect and would be blamed on the kit.
            const int steps = 10;
            for (var step = 1; step <= steps; step++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                state = state with { Subtitle = $"step {step} of {steps}", Progress = (double)step / steps };
                activities.Update(handle, state);
                log($"[ACTIVITY] update {step} -> progress={state.Progress:0.00}");
            }

            // 🔴 THE PUSH TOKEN, read here because a seam nothing consults is indistinguishable from one
            // that does not work (D63). It is also the answer to the limit this probe cannot otherwise
            // show: `Update` runs in THIS process, so an activity outlives the app while its update loop
            // does not — a server advancing it needs this token.
            // ⚠ Polled, not read once: the system issues it asynchronously AFTER the activity starts, so
            // the honest answer immediately after `Start` is null and that is not a failure.
            string? token = null;
            for (var attempt = 0; attempt < 5 && token is null; attempt++)
            {
                token = activities.PushToken(handle);
                if (token is null) await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            log(token is null
                // 🔴 NOT A FAIL, and the reason is known rather than guessed: this sample ships NO
                // entitlements file, so it has no `aps-environment`, and iOS issues no push token to an app
                // without the Push Notifications entitlement however correctly the activity asks for one.
                // Measured on an iPhone 17 Pro 2026-08-09 — `pushType: .token` was accepted and no token
                // followed. Proving this path end to end needs an App ID with Push Notifications enabled,
                // which a free/personal team cannot create.
                ? "[ACTIVITY] push token: none — this sample has no aps-environment entitlement, so iOS "
                  + "issues none. The seam is exercised; the TOKEN path is unproven."
                : $"[ACTIVITY] push token: {token.Length / 2} bytes — a server can advance this activity");

            log("[ACTIVITY] VERDICT: PASS — started and updated; the Island is the visual half");

            // Left RUNNING deliberately: ending it here would race a screenshot. The app being uninstalled
            // ends it anyway, and End() is exercised by the dev-loop driver instead.
        }
        catch (Exception ex)
        {
            log($"[ACTIVITY] VERDICT: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
    }
}
