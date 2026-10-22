using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Microsoft.Maui.ApplicationModel;
using Activity = global::Android.App.Activity;
using Intent = global::Android.Content.Intent;

namespace Shenora.Android;

/// <summary>
/// Routes an activity result back to an in-flight await, across Activity RECREATION.
/// <para>
/// 🔴 <b>The adopter must forward one line</b> from their <c>MainActivity.OnActivityResult</c> to
/// <see cref="Deliver"/> (<c>docs/guides/mobile.md</c>). AndroidX's <see cref="ActivityResultRegistry"/>
/// would need no wiring, but a MAUI activity does not round-trip AndroidX instance state, so its restored
/// request-code map is empty and an arriving result falls through unseen — measured, see
/// <c>.claude/knowledge/mobile-shells.md</c>. The framework's own routing survives, so this relay owns its
/// request codes over <c>StartActivityForResult</c>.
/// </para>
/// <para>
/// A result the app did NOT forward still ends the request: Android delivers <c>onActivityResult</c> before
/// <c>onResume</c>, so a request still unanswered when its launching activity resumes after pausing lost its
/// result. So does a launcher that FINISHES while the launched activity is up, since nothing can then receive
/// the result. The kit's own callers then fail with a message saying which; any other callback receives the
/// contract's cancelled result. ⚠ A launch from an activity that is not resumed at the time is not watched
/// this way, since no pause follows it: it waits for <see cref="Deliver"/> as before.
/// </para>
/// <para>
/// ⚠ PROCESS death is the boundary no mechanism survives: the awaiting task dies with the process, so the
/// caller's cancellation token is the only honest escape past it.
/// </para>
/// </summary>
public static class ActivityResultRelay
{
    // FragmentActivity reserves the upper 16 bits of a request code, so the relay's range must stay
    // under 0xFFFF; 0x5E00 ("Shenora") plus a small window keeps it clear of Essentials' codes.
    private const int FirstRequestCode = 0x5E00;
    private const int RequestCodeWindow = 0x0100;

    /// <summary>What a kit caller fails with when the launcher resumed and its result was never forwarded.</summary>
    private static string NotForwarded(Activity launcher) =>
        $"The activity returned, but its result never reached ActivityResultRelay.Deliver. Forward " +
        $"{launcher.GetType().Name}.OnActivityResult to it (docs/guides/mobile.md).";

    /// <summary>What a kit caller fails with when the launcher finished first.</summary>
    private static string LauncherFinished(Activity launcher) =>
        $"{launcher.GetType().Name} closed while the activity it started was open, so nothing can receive its result.";

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> InFlight = [];
    private static int _next;
    private static bool _watching;

    /// <summary>One launched request. Mutable fields are read and written under <see cref="Gate"/>.</summary>
    private sealed class Entry(ActivityResultContract contract, IActivityResultCallback callback, Activity launcher)
    {
        public ActivityResultContract Contract { get; } = contract;
        public IActivityResultCallback Callback { get; } = callback;

        /// <summary>
        /// The activity the result is delivered to: the INSTANCE that launched, since another instance of the
        /// same type (one an explicit intent stacked over the picker, measured) is not returning from it. Moved
        /// to the next instance of that type created after it is destroyed, which is a recreation.
        /// </summary>
        public Activity Launcher = launcher;

        /// <summary>The launcher was destroyed; the next instance of its type created takes its place.</summary>
        public bool Recreating;

        /// <summary>The launcher paused after the launch — the started activity covered it.</summary>
        public bool Left;

        /// <summary>A result was delivered, or its loss reported.</summary>
        public bool Answered;
    }

    /// <summary>
    /// Launch <paramref name="contract"/> from <paramref name="activity"/> and route its result to
    /// <paramref name="callback"/> through <see cref="Deliver"/>. Returns the request code; release it
    /// with <see cref="Complete"/> on every exit path. ⚠ Call on the main thread.
    /// </summary>
    public static int Begin(ComponentActivity activity, ActivityResultContract contract,
                            Java.Lang.Object? input, IActivityResultCallback callback)
    {
        int requestCode;
        lock (Gate)
        {
            // A full window has no free code, and the search below would never end — on the main thread.
            if (InFlight.Count >= RequestCodeWindow)
                throw new InvalidOperationException($"{RequestCodeWindow} activity requests are already in flight.");
            // A rotating window; an in-flight code is skipped.
            do { requestCode = FirstRequestCode + (_next++ % RequestCodeWindow); }
            while (InFlight.ContainsKey(requestCode));
            InFlight[requestCode] = new Entry(contract, callback, activity);

            if (!_watching)
            {
                _watching = true;
                Platform.ActivityStateChanged += OnActivityStateChanged;
            }
        }
        // Outside the lock — this starts a real Intent. The FRAMEWORK call, not AndroidX's launcher,
        // which would route the result back through the registry.
        try
        {
            activity.StartActivityForResult(contract.CreateIntent(activity, input), requestCode);
        }
        catch
        {
            // The caller never got the code to release: kept, each failed launch took one for good.
            lock (Gate) InFlight.Remove(requestCode);
            throw;
        }
        return requestCode;
    }

    /// <summary>
    /// Hand a result from <c>MainActivity.OnActivityResult</c> to whichever request it answers. True when
    /// it was this relay's. ⚠ Call <c>base.OnActivityResult</c> either way.
    /// </summary>
    public static bool Deliver(int requestCode, int resultCode, Intent? data)
    {
        Entry? entry;
        lock (Gate)
        {
            if (!InFlight.TryGetValue(requestCode, out entry)) return false;
            entry.Answered = true;
        }
        // Parsed by the CONTRACT — a null Uri for a backed-out picker, not a raw resultCode.
        entry.Callback.OnActivityResult(entry.Contract.ParseResult(resultCode, data));
        return true;
    }

    /// <summary>
    /// Follow each request's launcher, and report the loss of a request still unanswered when it resumes after
    /// pausing. Any other activity resuming — another of the app's, or another instance of the launcher's type —
    /// is not the launcher's return.
    /// </summary>
    private static void OnActivityStateChanged(object? sender, ActivityStateChangedEventArgs e)
    {
        if (e.Activity is not { } activity) return;

        List<(Entry Entry, string Why)>? lost = null;
        lock (Gate)
        {
            foreach (var entry in InFlight.Values)
            {
                if (entry.Answered) continue;
                if (entry.Recreating)
                {
                    if (e.State is not ActivityState.Created || activity.GetType() != entry.Launcher.GetType()) continue;
                    entry.Launcher = activity;
                    entry.Recreating = false;
                    continue;
                }
                if (!activity.Equals(entry.Launcher)) continue;
                switch (e.State)
                {
                    // Finishing for good: nothing can receive the result. Otherwise it is a recreation (a
                    // configuration change, or the system reclaiming it), and a new instance takes its place.
                    case ActivityState.Destroyed when activity.IsFinishing && !activity.IsChangingConfigurations:
                        entry.Answered = true;
                        (lost ??= []).Add((entry, LauncherFinished(activity)));
                        break;
                    case ActivityState.Destroyed:
                        entry.Recreating = true;
                        break;
                    case ActivityState.Paused:
                        entry.Left = true;
                        break;
                    case ActivityState.Resumed when entry.Left:
                        entry.Answered = true;
                        (lost ??= []).Add((entry, NotForwarded(activity)));
                        break;
                }
            }
        }

        // Outside the lock: a callback may complete a task whose continuation calls back in.
        foreach (var (entry, why) in lost ?? [])
        {
            AppCallback.Run(() =>
            {
                if (entry.Callback is ILostResultCallback kit) kit.OnResultLost(why);
                else entry.Callback.OnActivityResult(
                    entry.Contract.ParseResult((int)global::Android.App.Result.Canceled, null));
            });
        }
    }

    /// <summary>Forget the request. Idempotent; safe from any thread.</summary>
    public static void Complete(int requestCode)
    {
        lock (Gate) InFlight.Remove(requestCode);
    }
}

/// <summary>A kit callback that fails its caller when its result can no longer arrive.</summary>
internal interface ILostResultCallback
{
    /// <summary>The result will never be delivered; <paramref name="why"/> says why, for the host's log.</summary>
    void OnResultLost(string why);
}
