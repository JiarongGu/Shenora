using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;

namespace Shenora.Mobile;

/// <summary>
/// Raises the platform's back gesture into <see cref="BackNavigation"/>, and does the platform's own
/// thing when the page declines it.
/// <para>
/// 🔴 <b>Android only, and its absence there is not a missing feature but a broken app.</b> Unhandled,
/// back finishes the activity from whatever screen the user is on — so a user two levels into a MAUI app
/// is dumped to the home screen. iOS has no system back gesture to raise, so this is inert there and
/// says so once; a page learns which it is on from
/// <see cref="Core.Shell.ShellCapability.BackNavigation"/> rather than by sniffing the platform.
/// </para>
/// </summary>
/// <remarks>
/// ⚠ <b>The press is SYNCHRONOUS and the page's answer is not</b>, which is the whole difficulty and the
/// reason this is not three lines. Android calls the callback on the UI thread and takes its return as
/// the decision, so there is nowhere to await a round trip to the page. The escape is to consume the
/// press with an ENABLED callback, ask, and — if the page declines — disable the callback and re-issue
/// <c>OnBackPressed()</c>, which now falls through to whatever would have happened.
/// </remarks>
public sealed class MobileBackNavigation : IDisposable
{
    private readonly BackNavigation _back;
    private readonly ILogger? _log;
    private bool _disposed;

    /// <param name="back">The coordinator. Register it with <c>AddShenoraBackNavigation</c>.</param>
    /// <param name="log">Optional diagnostics.</param>
    public MobileBackNavigation(BackNavigation back, ILogger? log = null)
    {
        _back = back ?? throw new ArgumentNullException(nameof(back));
        _log = log;
        Attach();
    }

    /// <summary>
    /// True when this shell actually has a back gesture to offer. False on iOS, where nothing will ever
    /// be raised — the honest answer to advertise as
    /// <see cref="Core.Shell.ShellCapability.BackNavigation"/>.
    /// </summary>
    public static bool IsSupported =>
#if ANDROID
        true;
#else
        false;
#endif

    /// <summary>
    /// Re-attach to the CURRENT activity. Call after a recreation.
    /// <para>
    /// 🔴 An Android configuration change destroys the activity and builds a new one, with a new
    /// dispatcher — and unlike an activity RESULT, a back callback is not restored from saved state
    /// (there is none to restore; it is registered at runtime). So the callback lives on the dead
    /// activity and back silently reverts to the platform default. This is idempotent, so calling it on
    /// every start is the simplest correct thing.
    /// </para>
    /// </summary>
    public void Attach()
    {
        if (_disposed) return;
#if ANDROID
        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
        if (activity is not AndroidX.Activity.ComponentActivity component)
        {
            // MauiAppCompatActivity IS a ComponentActivity, so this means the activity is not up yet.
            Log("back: no current activity to attach to yet — call Attach() again once one exists.");
            return;
        }

        // Replacing rather than stacking: two live callbacks would both consume the same press and the
        // second would never see it.
        Detach();
        _callback = new BackCallback(this);
        component.OnBackPressedDispatcher.AddCallback(component, _callback);
        Log("back: attached to the activity's back dispatcher");
#else
        Log("back: this shell has no system back gesture — nothing to attach.");
#endif
    }

#if ANDROID
    private BackCallback? _callback;

    private void Detach()
    {
        if (_callback is null) return;
        // The activity may already be gone, and a teardown path must not throw.
        try { _callback.Remove(); } catch (Exception) { /* already gone */ }
        _callback.Dispose();
        _callback = null;
    }

    /// <summary>
    /// The dispatcher callback. Enabled always — the coordinator's own fast path is what makes a press
    /// cheap when no page is intercepting, and keeping this enabled means there is exactly one place
    /// that decides.
    /// </summary>
    private sealed class BackCallback(MobileBackNavigation owner)
        : AndroidX.Activity.OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => owner.OnPressed(this);
    }

    private async void OnPressed(BackCallback callback)
    {
        try
        {
            if (await _back.PressAsync().ConfigureAwait(true)) return;

            // The page declined (or nobody answered). Step out of the way and re-issue, so whatever
            // WOULD have happened still happens — another callback, the fragment stack, or finishing.
            // ⚠ Re-enable afterwards or back is handled once and then never again.
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not AndroidX.Activity.ComponentActivity component) return;

            callback.Enabled = false;
            try { component.OnBackPressedDispatcher.OnBackPressed(); }
            finally { callback.Enabled = true; }
        }
        catch (Exception ex)
        {
            // `async void`: an escape here is an unhandled managed exception on the UI thread, which on
            // Android crosses JNI and kills the process — the failure this shell spends forty lines
            // elsewhere avoiding. A back press is never worth that.
            Log($"back: the press failed ({ex.GetType().Name}: {ex.Message}); it was swallowed.");
        }
    }
#endif

    private void Log(string message) => AppCallback.Log(_log, () => $"[Shenora.Mobile] {message}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#if ANDROID
        Detach();
#endif
    }
}
