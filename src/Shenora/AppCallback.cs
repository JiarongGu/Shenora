namespace Shenora;

/// <summary>
/// Invoke APP-SUPPLIED code from a place where an escaping exception is not merely an error but a
/// fatal, uncatchable one — a UI-thread event handler, a timer tick, a posted delegate, a dispose
/// path. In all of those there is no caller left on the stack to catch anything.
/// <para>
/// This exists because the kit's own rule — <i>no app callback runs unguarded inside a
/// WebView2/WinForms event handler</i> — has been broken repeatedly and expensively, in ways that
/// share nothing but this shape:
/// </para>
/// <list type="bullet">
/// <item>A throwing <c>OnLoading</c> escaped an <c>async void</c> handler, so the login window's
/// <c>Finish()</c> never ran and the foreground controller then cancelled EVERY close including
/// <c>Application.Exit</c> — one app callback bricked the app.</item>
/// <item>A throwing <c>ILogger</c> escaped before a <c>TrySetException</c>, so a pool lease's task
/// never completed: a hung caller still holding its capacity permit, caused by a log statement.</item>
/// <item>A throwing renderer-crash handler ran at the exact moment things were already going wrong,
/// taking down the recovery that was supposed to follow it.</item>
/// </list>
/// <para>
/// SWALLOWING IS THE POLICY, deliberately. At these sites the alternative to losing the callback's
/// exception is losing the operation, the window, or the process. Callers that can report get an
/// <c>onError</c> hook — itself guarded, because a failure reporter that throws must not become the
/// crash it was reporting (the lesson already encoded in <c>WinFormsUiDispatcher.Report</c>).
/// </para>
/// <para>
/// Public rather than internal because its consumers are in OTHER packages
/// (<c>Shenora.Windows</c>, and <c>Shenora.Android</c>/<c>Shenora.iOS</c> via the shared
/// <c>Shenora.Mobile</c> source) and a <c>ProjectReference</c> does not
/// grant <c>internal</c> access — the D19/D20 placement law: the policy is portable, so it belongs in
/// <c>Shenora</c> with ONE owner, not copied per package. Apps may use it for the same reason
/// against their own extension points.
/// </para>
/// </summary>
public static class AppCallback
{
    /// <summary>
    /// Run <paramref name="work"/> and return true if it completed. An exception is swallowed (after
    /// <paramref name="onError"/> is offered it) and false is returned — never rethrown.
    /// </summary>
    /// <param name="work">The app-supplied work to invoke.</param>
    /// <param name="onError">
    /// Optional report sink for whatever <paramref name="work"/> threw. Guarded: if this throws too,
    /// it is swallowed. Null = report nowhere.
    /// </param>
    public static bool Run(Action work, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            work();
            return true;
        }
        catch (Exception ex)
        {
            Report(onError, ex);
            return false;
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> and return its result, or <paramref name="fallback"/> if it threw.
    /// For a callback whose ANSWER the kit needs (a predicate, a policy decision) — a throwing app
    /// filter must resolve to an explicit default rather than propagating.
    /// </summary>
    /// <param name="work">The app-supplied work to invoke.</param>
    /// <param name="fallback">
    /// The value to use when <paramref name="work"/> throws. Choose it as a POLICY, not a
    /// convenience: for a block/deny predicate, decide at the call site whether failing open or
    /// closed is correct there, and say why.
    /// </param>
    /// <param name="onError">Optional report sink; guarded, as in <see cref="Run"/>.</param>
    public static T RunOrDefault<T>(Func<T> work, T fallback, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            Report(onError, ex);
            return fallback;
        }
    }

    /// <summary>
    /// Write a diagnostic to an app-supplied sink — GUARDED and LAZY. The one shape every diagnostic
    /// in this kit uses, and the reason it is here rather than copied per type.
    /// <para>
    /// <b>An <c>ILogger</c> or an <c>Action&lt;string&gt;</c> IS an app callback</b>, so it obeys the
    /// rule above: these sinks are invoked from places with no caller left to catch anything — a
    /// WebView2 event handler, a timer tick, a fire-and-forget body — and several sit INSIDE a
    /// <c>catch</c> that exists to stop a failure escaping, so a throwing sink defeats the very guard
    /// it is reporting from. That has been paid for: a throwing sink once landed before a
    /// <c>TrySetException</c> (a pool lease hung forever holding its permit) and before a
    /// <c>Release()</c> (a permit leaked for the process lifetime).
    /// </para>
    /// <para>
    /// <paramref name="message"/> is a <see cref="Func{TResult}"/> because the guard has to cover
    /// BUILDING the message as well as writing it — several call sites interpolate WebView2/COM
    /// properties that throw once the underlying object is gone, and interpolation at the call site
    /// would happen outside the guard. It also makes the message free when no sink is configured,
    /// which matters on the IPC hot path.
    /// </para>
    /// <para>
    /// Collapsed here in the 0.2.0 cleanup from FIVE byte-identical private copies
    /// (<c>WebViewHost</c>, <c>WebViewIpcBridge</c>, <c>EmbeddedResourceProvider</c>,
    /// <c>NotificationPump</c>, <c>OperationRegistry</c>) — the same "N copies of the rule that must
    /// never be broken" shape this package's own neighbours already learned from
    /// <c>IpcErrorMapping</c>, where a fifth copy of the error boundary was one paste away from
    /// leaking a filesystem path to the page.
    /// </para>
    /// </summary>
    /// <param name="sink">The app's diagnostic sink. Null = do nothing, and do not build the message.</param>
    /// <param name="message">Builds the line to write. Invoked inside the guard.</param>
    public static void Log(Action<string>? sink, Func<string> message)
    {
        if (sink is null) return;
        ArgumentNullException.ThrowIfNull(message);
        Run(() => sink(message()));
    }

    private static void Report(Action<Exception>? onError, Exception error)
    {
        if (onError is null) return;
        try
        {
            onError(error);
        }
        catch (Exception)
        {
            // A failure reporter that throws must not become the crash it reports. There is nowhere
            // left to escalate to — the escalation path is what just failed.
        }
    }
}
