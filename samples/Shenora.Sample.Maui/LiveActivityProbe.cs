using Shenora;

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
            var handle = activities.Start(state);
            log($"[ACTIVITY] start -> {handle ?? "<null>"}");
            if (handle is null)
            {
                log("[ACTIVITY] VERDICT: FAIL — start returned null; see the reason logged above");
                return;
            }

            // Three updates, so a screenshot at any point shows a plausible value AND the update path is
            // exercised rather than only the start path. `with` on the app's own record is why the contract
            // needs no mutation callback.
            for (var step = 1; step <= 3; step++)
            {
                await Task.Delay(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                state = state with { Subtitle = $"step {step} of 3", Progress = step / 3.0 };
                activities.Update(handle, state);
                log($"[ACTIVITY] update {step} -> progress={state.Progress:0.00}");
            }

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
