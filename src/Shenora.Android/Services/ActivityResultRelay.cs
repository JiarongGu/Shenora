using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Intent = global::Android.Content.Intent;

namespace Shenora.Android;

/// <summary>
/// Routes an activity result back to an in-flight await — across Activity RECREATION, which is the
/// case that decides the whole design.
/// <para>
/// 🔴 <b>Why not <see cref="ActivityResultRegistry"/>:</b> its recreation story rests on AndroidX
/// instance state, and a MAUI activity does not round-trip it — measured on a device, the recreated
/// activity's bundle carried the framework sections (<c>android:viewHierarchyState</c>,
/// <c>android:fragments</c>) and no <c>androidx.lifecycle.BundlableSavedStateRegistry.key</c> at all.
/// So a registry registration dies with its activity, the restored request-code map is empty, and the
/// arriving result falls through to the legacy path unseen. What provably survives recreation is the
/// FRAMEWORK's own routing: <c>Activity.OnActivityResult</c> fired on the recreated instance with the
/// original request code. This relay therefore owns its request codes and receives results through
/// <see cref="Deliver"/> — one documented override in the app's MainActivity (see
/// <c>docs/ADOPTION.md</c>), which is the price of surviving what the registry cannot.
/// </para>
/// <para>
/// ⚠ What no mechanism survives is PROCESS death: the awaiting task dies with the process, so a
/// restored result would answer a question no code is asking. The caller's cancellation token is the
/// honest escape past that boundary.
/// </para>
/// </summary>
public static class ActivityResultRelay
{
    // FragmentActivity reserves the upper 16 bits of a request code, so the relay's range must stay
    // under 0xFFFF; 0x5E00 ("Shenora") plus a small window keeps it clear of Essentials' codes.
    private const int FirstRequestCode = 0x5E00;
    private const int RequestCodeWindow = 0x0100;

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> InFlight = [];
    private static int _next;

    private sealed record Entry(ActivityResultContract Contract, IActivityResultCallback Callback);

    /// <summary>
    /// Launch <paramref name="contract"/> from <paramref name="activity"/> and route its result to
    /// <paramref name="callback"/> — through <see cref="Deliver"/>, so a recreated activity still
    /// reaches it. Returns the request code; release it with <see cref="Complete"/> on every exit
    /// path. Call on the main thread.
    /// </summary>
    public static int Begin(ComponentActivity activity, ActivityResultContract contract,
                            Java.Lang.Object? input, IActivityResultCallback callback)
    {
        int requestCode;
        lock (Gate)
        {
            // A tiny rotating window: an in-flight code is skipped, and 256 concurrent pickers is
            // not a real workload.
            do { requestCode = FirstRequestCode + (_next++ % RequestCodeWindow); }
            while (InFlight.ContainsKey(requestCode));
            InFlight[requestCode] = new Entry(contract, callback);
        }
        // Outside the lock — this starts a real Intent. The FRAMEWORK call, deliberately: the
        // AndroidX launcher would route the result through the registry this type exists to avoid.
        activity.StartActivityForResult(contract.CreateIntent(activity, input), requestCode);
        return requestCode;
    }

    /// <summary>
    /// Hand a result from <c>MainActivity.OnActivityResult</c> to whichever request it answers.
    /// True when it was this relay's; false means it belongs to someone else — either way, call
    /// <c>base.OnActivityResult</c> too.
    /// </summary>
    public static bool Deliver(int requestCode, int resultCode, Intent? data)
    {
        Entry? entry;
        lock (Gate)
        {
            if (!InFlight.TryGetValue(requestCode, out entry)) return false;
        }
        // Parsed by the CONTRACT, so the callback sees exactly what the registry would have handed
        // it (a null Uri for a backed-out picker, not a raw resultCode to re-interpret).
        entry.Callback.OnActivityResult(entry.Contract.ParseResult(resultCode, data));
        return true;
    }

    /// <summary>Forget the request. Idempotent; safe from any thread.</summary>
    public static void Complete(int requestCode)
    {
        lock (Gate) InFlight.Remove(requestCode);
    }
}
