using Microsoft.Extensions.Logging;

namespace Shenora.Windows;

/// <summary>
/// Write a diagnostic without letting the app's logger become the failure it was reporting.
/// <para>
/// AN <c>ILogger</c> IS APP CODE. The package's own rule — no app-supplied callback runs unguarded
/// inside a WebView2/WinForms event handler or a posted UI-thread body — applies to it exactly as it
/// applies to <c>OnLoading</c> or a request filter, and the logging added in P5.5 H4.7 did not honour
/// it. A throwing or disposed logger (a file sink whose handle went away, a scope-captured provider
/// after shutdown) then lands wherever the call happens to sit, and three of those places turn a log
/// line into a real failure:
/// </para>
/// <list type="bullet">
/// <item><b>Inside the instance-creation catch</b> the throw escaped before
/// <c>TrySetException</c> ran, so the lease's task NEVER completed — a permanently hung caller
/// holding a capacity permit, from a log statement.</item>
/// <item><b>Inside the return-to-pool body</b> it escaped before <c>_capacity.Release()</c>, leaking
/// the permit for the process lifetime — and as an unhandled UI-thread exception besides.</item>
/// <item><b>Inside a WebView2 event handler</b> (<c>NewWindowRequested</c>,
/// <c>PermissionRequested</c>, <c>ProcessFailed</c>) there is no caller on the stack at all, so it is
/// an unhandled UI-thread exception — the crash dialog under the family bootstrap.</item>
/// </list>
/// <para>
/// So every diagnostic in the SESSIONS stack goes through here — all 21 of them, and the scope is the
/// point: the rest of <c>Shenora.Windows</c> logs through <see cref="Shenora.AppCallback.Log"/>, which
/// takes a rendered <c>Func&lt;string&gt;</c>. This overloadful exists beside it because a session's
/// diagnostics are STRUCTURED — <c>{Kind}</c>, <c>{ExitCode}</c>, <c>{Uri}</c> — and a pre-rendered
/// string throws those fields away before any sink can index them.
/// </para>
/// <para>
/// ⚠ <b>It is not a second owner of the guarding policy</b>, which would be the real smell: the body
/// below delegates to <see cref="Shenora.AppCallback.Run"/>, so there is still exactly one place that
/// decides what happens when app code throws. Swallowing is the only correct answer here: the
/// alternative to a lost log line must never be a lost session.
/// </para>
/// </summary>
internal static class SessionLog
{
    /// <summary>
    /// Run <paramref name="write"/> against <paramref name="log"/> if there is one, swallowing
    /// anything it throws. Skips the call entirely when logging is off, so the common
    /// (null-logger) case allocates nothing beyond the closure the caller already built.
    /// </summary>
    internal static void Try(ILogger? log, Action<ILogger> write)
    {
        if (log is null) return;
        // Through the ONE owner of this policy (Shenora.AppCallback) rather than a local
        // try/catch: an app logger is an app callback, and there is exactly one correct behaviour for
        // one of those escaping into a UI-thread event handler. No onError sink — a diagnostic that
        // fails is a lost line, and there is nowhere left to report it to: the reporter just failed.
        Shenora.AppCallback.Run(() => write(log));
    }
}
