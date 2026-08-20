using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
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

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> InFlight = [];
    private static int _next;

    private sealed record Entry(ActivityResultContract Contract, IActivityResultCallback Callback);

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
            // A rotating window; an in-flight code is skipped.
            do { requestCode = FirstRequestCode + (_next++ % RequestCodeWindow); }
            while (InFlight.ContainsKey(requestCode));
            InFlight[requestCode] = new Entry(contract, callback);
        }
        // Outside the lock — this starts a real Intent. The FRAMEWORK call, not AndroidX's launcher,
        // which would route the result back through the registry.
        activity.StartActivityForResult(contract.CreateIntent(activity, input), requestCode);
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
        }
        // Parsed by the CONTRACT — a null Uri for a backed-out picker, not a raw resultCode.
        entry.Callback.OnActivityResult(entry.Contract.ParseResult(resultCode, data));
        return true;
    }

    /// <summary>Forget the request. Idempotent; safe from any thread.</summary>
    public static void Complete(int requestCode)
    {
        lock (Gate) InFlight.Remove(requestCode);
    }
}
