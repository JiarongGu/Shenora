using Microsoft.Extensions.Logging;

namespace Shenora.Windows;

/// <summary>
/// Write a diagnostic without letting the app's logger become the failure it was reporting. An
/// <c>ILogger</c> is APP CODE, so it gets the same guard as any other app callback in this package.
/// <para>
/// 🔴 <b>Every diagnostic in the SESSIONS stack goes through here.</b> A throwing or disposed logger (a
/// file sink whose handle went away, a scope-captured provider after shutdown) escapes wherever the call
/// happens to sit: before a <c>TrySetException</c> the lease's task never completes; before a
/// <c>_capacity.Release()</c> the permit leaks for the process lifetime; and inside a WebView2 event
/// there is no caller on the stack at all, so it is an unhandled UI-thread exception.
/// </para>
/// <para>
/// It exists beside <see cref="Shenora.AppCallback.Log"/> — which takes a rendered
/// <c>Func&lt;string&gt;</c> — because a session's diagnostics are STRUCTURED (<c>{Kind}</c>,
/// <c>{ExitCode}</c>, <c>{Uri}</c>) and a pre-rendered string throws those fields away before any sink
/// can index them. The body delegates to <see cref="Shenora.AppCallback.Run"/>, so there is still
/// exactly one owner of the guarding policy.
/// </para>
/// </summary>
internal static class SessionLog
{
    /// <summary>
    /// Run <paramref name="write"/> against <paramref name="log"/> if there is one, swallowing anything
    /// it throws. Skips the call entirely when logging is off, so the common (null-logger) case
    /// allocates nothing beyond the closure the caller already built.
    /// </summary>
    internal static void Try(ILogger? log, Action<ILogger> write)
    {
        if (log is null) return;
        // Through the ONE owner of this policy rather than a local try/catch. No onError sink — a
        // diagnostic that fails is a lost line, and the reporter is what just failed.
        Shenora.AppCallback.Run(() => write(log));
    }
}
