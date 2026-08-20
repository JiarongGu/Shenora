using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>What <see cref="SingleInstanceGuard.TryAcquire()"/> found.</summary>
public enum SingleInstanceResult
{
    /// <summary>This process owns the scope — carry on starting.</summary>
    Acquired,

    /// <summary>Another live instance owns it. Broadcast and exit; do not start.</summary>
    AlreadyRunning,

    /// <summary>
    /// The OS refused to answer and the guard FAILED OPEN — start, but single-instance is not enforced
    /// for this run. Distinct from <see cref="Acquired"/>: both mean "keep starting", but an app whose
    /// reason for being single-instance is a single-writer store may want to refuse or degrade.
    /// </summary>
    Unverified,
}

/// <summary>
/// Enforces ONE running instance per scope (normally the install directory), so distinct installs run
/// side-by-side. A second launch calls <see cref="TryAcquire()"/>
/// (<see cref="SingleInstanceResult.AlreadyRunning"/>), then <see cref="BroadcastActivate"/>, and exits;
/// the running instance catches the registered window message (compare <c>m.Msg</c> to
/// <see cref="ActivateMessageId"/>) and brings itself to the foreground.
/// <para>
/// ⚠ Acquire FIRST at startup, before anything that takes an OS lock (the WebView2 environment prewarm).
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // Held for the process lifetime; the OS releases it at process exit either way.
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
    /// Registered window message the running instance listens for. Per-scope, so activating one install
    /// never foregrounds another.
    /// <para>
    /// ⚠ <b>Null means TWO different things:</b> <see cref="TryAcquire()"/> has not run yet, or
    /// <c>RegisterWindowMessage</c> FAILED (a session out of atom-table space — rare and real) — in which
    /// case activation silently cannot work while single instance itself is unaffected, the mutex being
    /// the real guard. The host logs a warning for the second case; a caller reading this directly gets
    /// no such help.
    /// </para>
    /// </summary>
    public uint? ActivateMessageId { get; private set; }

    /// <summary>
    /// Stable key for a scope value. Normalized case- and trailing-separator-insensitive so
    /// <c>C:\App</c>, <c>C:\App\</c> and <c>c:\app</c> collapse to one instance; hashed to hex because a
    /// raw path is not a valid mutex/message name.
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
    /// Acquire the scope with no wait. A mutex failure fails OPEN — an OS hiccup must never block a
    /// legitimate launch.
    /// <para>
    /// ⚠ Abandonment recovery (a predecessor that DIED holding the mutex) is BEST-EFFORT here: some
    /// kernels leave a window between the owning thread ending and the abandoned bit flipping, in which
    /// this reports "already running" against a corpse. Where recovery must be reliable — the
    /// <c>--restarted</c> handoff, or any relaunch overlapping a shutdown — use
    /// <see cref="TryAcquire(TimeSpan)"/>.
    /// </para>
    /// </summary>
    public SingleInstanceResult TryAcquire() => TryAcquire(TimeSpan.Zero);

    /// <summary>
    /// Like <see cref="TryAcquire()"/>, but waits up to <paramref name="waitForPredecessor"/> for a
    /// current owner to let go — the <c>--restarted</c> handoff, where a relaunch overlaps its
    /// predecessor's graceful shutdown (which can legitimately drain for many seconds) while a genuine
    /// double-launch keeps the instant zero-wait answer. The blocking wait also closes the
    /// abandonment-timing race described on <see cref="TryAcquire()"/>.
    /// <para>
    /// ⚠ The contract is CROSS-PROCESS. An OS mutex is per-thread reentrant, so a second guard acquired
    /// on the SAME thread falsely succeeds — in-process tests must hold from a dedicated thread, as a
    /// second process would.
    /// </para>
    /// </summary>
    public SingleInstanceResult TryAcquire(TimeSpan waitForPredecessor)
    {
        // 0 is RegisterWindowMessage's failure answer; null says so rather than overloading it.
        var id = RegisterWindowMessage(ActivateMessageName);
        ActivateMessageId = id == 0 ? null : id;

        // 🔴 IDEMPOTENT: already holding it IS success. An OS mutex is per-thread REENTRANT, so taking a
        // second handle succeeds on the same thread even when this process is the owner — and Dispose
        // could then release only one, leaving the mutex held after shutdown and timing the `--restarted`
        // handoff out against a corpse.
        if (_mutex is not null) return SingleInstanceResult.Acquired;

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
                return SingleInstanceResult.AlreadyRunning;
            }
            return SingleInstanceResult.Acquired;
        }
        catch
        {
            // FAIL OPEN, and SAY SO — refusing to start because the OS would not answer is the worse
            // trade for most apps, but it is a different fact from owning the scope.
            return SingleInstanceResult.Unverified;
        }
    }

    /// <summary>Second instance → tell the running instance to come to the foreground.</summary>
    public void BroadcastActivate()
    {
        if (ActivateMessageId is { } id)
        {
            PostMessage(HWND_BROADCAST, id, IntPtr.Zero, IntPtr.Zero);
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
