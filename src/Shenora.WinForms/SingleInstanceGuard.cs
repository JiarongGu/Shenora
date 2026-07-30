using System.Runtime.InteropServices;

namespace Shenora.WinForms;

/// <summary>
/// Enforces ONE running instance per scope (normally the install directory). Desktop apps in
/// this family are not multi-instance-safe: single-writer databases, in-process file-operation
/// planners, and the WebView2 user-data folder's OS lock all corrupt under a second instance.
///
/// A second launch calls <see cref="TryAcquire()"/> (false), then <see cref="BroadcastActivate"/>
/// and exits; the running instance's main form catches the registered window message (compare
/// <c>m.Msg</c> to <see cref="ActivateMessageId"/> in <c>WndProc</c>) and brings itself to the
/// foreground. Keyed by the scope so DISTINCT installs run side-by-side.
///
/// Acquire FIRST at startup — before anything that takes an OS lock (e.g. the WebView2
/// environment prewarm).
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // Held for the process lifetime; the OS releases it when the process exits (normal quit or an
    // updater-triggered restart), so a relaunched instance re-acquires cleanly.
    private Mutex? _mutex;

    /// <param name="applicationName">Stable app identifier — prefixes the mutex/message names.</param>
    /// <param name="scope">
    /// What "one instance" is scoped to — normally the install directory (so two installs may run
    /// side-by-side). Null/empty scopes the guard to the application name alone.
    /// </param>
    public SingleInstanceGuard(string applicationName, string? scope = null)
    {
        ApplicationName = applicationName;
        var key = ChannelKey(scope);
        MutexName = $"Local\\{applicationName}.instance.{key}";
        ActivateMessageName = $"{applicationName}.activate.{key}";
    }

    /// <summary>The stable app identifier the channel names are derived from.</summary>
    public string ApplicationName { get; }

    /// <summary>The per-scope mutex name (exposed for tests/diagnostics).</summary>
    public string MutexName { get; }

    /// <summary>The per-scope registered-window-message name (exposed for tests/diagnostics).</summary>
    public string ActivateMessageName { get; }

    /// <summary>
    /// Registered window message the running instance listens for. Per-scope, so activating one
    /// install never foregrounds another. 0 until <see cref="TryAcquire()"/> runs.
    /// </summary>
    public uint ActivateMessageId { get; private set; }

    /// <summary>
    /// Stable key for a scope value. Normalized case-insensitive + trailing-separator-insensitive
    /// so <c>C:\App</c>, <c>C:\App\</c> and <c>c:\app</c> collapse to one instance. FNV-1a keeps
    /// it to hex chars (a raw path is not a valid mutex/message name).
    /// </summary>
    public static string ChannelKey(string? scope)
    {
        var norm = (scope ?? string.Empty).TrimEnd('\\', '/').ToLowerInvariant();
        uint h = 2166136261; // FNV-1a 32-bit — deterministic, no crypto needed
        foreach (var c in norm)
        {
            h ^= c;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    /// <summary>
    /// True = this is the first/only instance (mutex now held for the process lifetime).
    /// False = another instance owns this scope; call <see cref="BroadcastActivate"/> and exit.
    /// A mutex failure fails OPEN (true) — an OS hiccup must never block a legitimate launch.
    /// </summary>
    public bool TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Like <see cref="TryAcquire()"/>, but when the scope is still owned, waits up to
    /// <paramref name="waitForPredecessor"/> for the owner to let go. This is the
    /// restart-through-relaunch handoff (family-proven with a 25 s budget): a relaunch started by
    /// the outgoing instance (<c>--restarted</c>) overlaps its predecessor's graceful shutdown,
    /// which can legitimately spend many seconds draining before the mutex releases — a genuine
    /// double-launch keeps the instant zero-wait answer. A predecessor that DIED holding the mutex
    /// surfaces as <see cref="AbandonedMutexException"/> — the mutex is ours then.
    ///
    /// The contract is CROSS-PROCESS (the only scenario that exists in production — one guard per
    /// process, owned by the runner): an OS mutex is per-thread reentrant, so a second guard
    /// acquired on the SAME thread would falsely succeed. Don't do that; in-process tests must
    /// hold from a dedicated thread, as a second process would.
    /// </summary>
    public bool TryAcquire(TimeSpan waitForPredecessor)
    {
        ActivateMessageId = RegisterWindowMessage(ActivateMessageName);

        // IDEMPOTENT (P5.5 H2). A second call used to overwrite _mutex with a fresh handle, leaking the
        // first one — and because an OS mutex is per-thread REENTRANT, the second WaitOne(0) succeeds on
        // the same thread even when this process is the owner. So a retry would report "I own it" while
        // Release/Dispose could then only ever let go of one of the two handles: the mutex stayed held
        // after shutdown, and the fast `--restarted` handoff (which waits for the predecessor to let go)
        // timed out against a corpse. Already holding it IS success.
        if (_mutex is not null) return true;

        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName);
            bool owned;
            try
            {
                owned = _mutex.WaitOne(TimeSpan.Zero) ||
                        (waitForPredecessor > TimeSpan.Zero && _mutex.WaitOne(waitForPredecessor));
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous instance exited without releasing — the mutex is ours now
            }
            if (!owned)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
            return true;
        }
        catch
        {
            return true; // fail open
        }
    }

    /// <summary>Second instance → tell the running instance to come to the foreground.</summary>
    public void BroadcastActivate()
    {
        if (ActivateMessageId != 0)
        {
            PostMessage(HWND_BROADCAST, ActivateMessageId, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Release the scope. Call on the ACQUIRING thread, LAST in shutdown — an explicit release
    /// (not just a closed handle) is what lets a <c>--restarted</c> relaunch waiting in
    /// <see cref="TryAcquire(TimeSpan)"/> proceed immediately instead of via abandonment.
    /// </summary>
    public void Dispose()
    {
        // ReleaseMutex throws when called off the owning thread or when never acquired — both
        // fine to swallow here (the OS still cleans up at process exit).
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xffff);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
