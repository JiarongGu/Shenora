using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
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
/// 🔴 <b>THE CALLBACK IS ENABLED ONLY WHILE A PAGE IS ACTUALLY INTERCEPTING</b>, tracked off
/// <see cref="BackNavigation.InterceptingChanged"/>. An always-enabled callback would be simpler and is
/// wrong twice: every press would enter managed code and re-enter the dispatcher to fall through, and —
/// worse — an enabled <c>OnBackPressedCallback</c> that is not an animation callback tells Android the
/// app handles back, which SUPPRESSES the predictive-back gesture app-wide on an API 33+ target. Off,
/// the platform never calls us at all, which is what makes "an app that never intercepts pays nothing"
/// literally true instead of nearly true.
/// <para>
/// ⚠ <b>The press is SYNCHRONOUS and the page's answer is not.</b> Android calls
/// <c>HandleOnBackPressed</c> on the UI thread and takes no return value — the decision is
/// <c>isEnabled()</c>, which is exactly why this toggles it. So there is nowhere to await a round trip:
/// the enabled callback CONSUMES the press, asks the page, and on a decline disables itself and
/// re-issues <c>OnBackPressed()</c>, which then falls through to the next enabled callback or to the
/// activity's own behaviour. It cannot re-enter this one, because this one is disabled while it runs.
/// </para>
/// </remarks>
public sealed class MobileBackNavigation : IDisposable
{
    private readonly BackNavigation _back;
    private readonly ILogger? _log;
    private bool _disposed;

    /// <summary>The raiser currently owning the process's back dispatcher — see the constructor.</summary>
    private static MobileBackNavigation? _live;

    /// <param name="back">The coordinator. Register it with <c>AddShenoraBackNavigation</c>.</param>
    /// <param name="log">Optional diagnostics.</param>
    public MobileBackNavigation(BackNavigation back, ILogger? log = null)
    {
        _back = back ?? throw new ArgumentNullException(nameof(back));
        _log = log;

        // 🔴 ONE RAISER PER PROCESS, because there is one back dispatcher per process. MEASURED on an
        // emulator: without this the attach count grew 1 → 3 → 5 across three configuration changes.
        // A recreation builds a new page whose constructor makes a new raiser, while the PREVIOUS page's
        // raiser is still subscribed to the static activity-state signal and re-attaches to the same new
        // activity — so two callbacks end up on one dispatcher, which this type's own remarks forbid.
        // The old page's Unloaded does not reliably run first, so the newcomer displaces the incumbent.
        Interlocked.Exchange(ref _live, this)?.Dispose();

        _back.InterceptingChanged += OnInterceptingChanged;
#if ANDROID
        // 🔴 SELF-DRIVEN, because a configuration change builds a NEW activity with a new dispatcher and
        // the callback is registered at runtime rather than restored from saved state — so it would stay
        // on the dead one and back would silently revert to the platform default. Asking the adopter to
        // re-attach is a rule where a mechanism exists; `MobileSafeArea` re-attaches itself off
        // `HandlerChanged` for the same reason.
        Platform.ActivityStateChanged += OnActivityStateChanged;
#endif
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
    /// Attach to the CURRENT activity, replacing any previous attachment. Idempotent.
    /// <para>
    /// ⚠ Called for you — on construction and again whenever the platform reports a new activity — so an
    /// app normally never needs this. It stays public for the case the kit cannot see: an activity this
    /// object was built before.
    /// </para>
    /// </summary>
    public void Attach()
    {
        if (_disposed) return;
#if ANDROID
        var activity = Platform.CurrentActivity;
        if (activity is not AndroidX.Activity.ComponentActivity component)
        {
            // MauiAppCompatActivity IS a ComponentActivity, so this means no activity is up yet — which
            // is normal during startup, and the state hook above will bring us back.
            Log("back: no current activity yet — will attach when one appears.");
            return;
        }
        if (ReferenceEquals(component, _attachedTo)) return;

        // Replacing rather than stacking: two live callbacks would both claim the same press.
        Detach();
        _attachedTo = component;
        _callback = new BackCallback(this) { Enabled = _back.Intercepting };
        component.OnBackPressedDispatcher.AddCallback(component, _callback);
        Log($"back: attached to the activity's back dispatcher (enabled={_back.Intercepting})");
#else
        Log("back: this shell has no system back gesture — nothing to attach.");
#endif
    }

    private void OnInterceptingChanged(object? sender, EventArgs e)
    {
#if ANDROID
        // The coordinator is driven from the IPC dispatch thread; `Enabled` belongs to the UI thread.
        var enabled = _back.Intercepting;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed || _callback is null) return;
            _callback.Enabled = enabled;
        });
#endif
    }

#if ANDROID
    private BackCallback? _callback;
    private object? _attachedTo;

    private void OnActivityStateChanged(object? sender, ActivityStateChangedEventArgs e)
    {
        // Created is too early for the dispatcher to be useful and Resumed is the first point the new
        // activity is certainly the current one; Attach() no-ops when it is the same instance.
        if (e.State is ActivityState.Resumed) Attach();
    }

    private void Detach()
    {
        if (_callback is null) return;
        try
        {
            _callback.Remove();
            // ⚠ DISPOSE ONLY AFTER A SUCCESSFUL REMOVE. If Remove throws for any reason other than
            // "already gone", the Java dispatcher still holds this callback — and a disposed peer whose
            // HandleOnBackPressed is then invoked throws out of a JNI-invoked override with nothing
            // managed above it, which is the process death this file exists to avoid. Leaking a callback
            // the platform still owns is strictly better than that.
            _callback.Dispose();

            // ⚠ Logged so ATTACH and DETACH can be counted against each other. An attach LINE is not a
            // live callback — that mistake made a working fix look inert, because disposing the previous
            // raiser removes its callback long after its line was printed.
            Log("back: detached from the previous activity's back dispatcher");
        }
        catch (Exception ex)
        {
            Log($"back: could not remove the previous callback ({ex.GetType().Name}); leaving it undisposed.");
        }
        _callback = null;
        _attachedTo = null;
    }

    /// <summary>
    /// The dispatcher callback. Enabled only while a page is intercepting — see the class remarks for
    /// why that is not merely an optimisation.
    /// </summary>
    private sealed class BackCallback(MobileBackNavigation owner)
        : AndroidX.Activity.OnBackPressedCallback(false)
    {
        public override void HandleOnBackPressed() => owner.OnPressed(this);
    }

    private async void OnPressed(BackCallback callback)
    {
        try
        {
            if (await _back.PressAsync().ConfigureAwait(true)) return;

            // 🔴 RE-CHECK AFTER THE AWAIT. The page can unload while a press is waiting — the coordinator
            // is a DI singleton and is NOT disposed with this object, so the wait runs to its timeout and
            // resumes here against a callback that Detach has already disposed. Touching it then throws,
            // the catch below swallows it, and the press is never re-issued — i.e. back does NOTHING,
            // which is the one outcome this design refuses.
            if (_disposed || !ReferenceEquals(callback, _callback)) return;

            // The page declined (or nobody answered). Step out of the way and re-issue, so whatever
            // WOULD have happened still happens — another callback, the fragment stack, or finishing.
            if (Platform.CurrentActivity is not AndroidX.Activity.ComponentActivity component) return;

            callback.Enabled = false;
            try { component.OnBackPressedDispatcher.OnBackPressed(); }
            // ⚠ Restore to what the coordinator says NOW, not unconditionally to true: the page may have
            // released interception while this press was in flight.
            finally { if (!_disposed) callback.Enabled = _back.Intercepting; }
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
        Interlocked.CompareExchange(ref _live, null, this);
        _back.InterceptingChanged -= OnInterceptingChanged;
#if ANDROID
        Platform.ActivityStateChanged -= OnActivityStateChanged;
        Detach();
#endif
    }
}
