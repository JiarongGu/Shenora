using Microsoft.Extensions.Logging;

namespace Shenora;

/// <summary>
/// Invoke APP-SUPPLIED code from a place where an escaping exception is fatal and uncatchable — a
/// UI-thread event handler, a timer tick, a posted delegate, a dispose path. In all of those there is
/// no caller left on the stack to catch anything.
/// <para>
/// <b>The rule this type owns:</b> <i>no app callback runs unguarded inside a WebView2/WinForms event
/// handler</i>. SWALLOWING IS THE POLICY — the alternative to losing the callback's exception is losing
/// the operation, the window, or the process. Callers that can report get an <c>onError</c> hook,
/// itself guarded: a failure reporter that throws must not become the crash it was reporting.
/// </para>
/// <para>
/// Public because its consumers are in OTHER packages (<c>Shenora.Windows</c>, and
/// <c>Shenora.Android</c>/<c>Shenora.iOS</c> via <c>Shenora.Mobile</c>) and a <c>ProjectReference</c>
/// does not grant <c>internal</c> access (D19/D20).
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
    /// Report sink for whatever <paramref name="work"/> threw. Guarded — a throw here is swallowed too.
    /// Null = report nowhere.
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
    /// <see cref="Run"/> for an ASYNC body: await it, swallow whatever it throws (after offering it to
    /// <paramref name="onError"/>), and return whether it completed — the shape a fire-and-forget UI
    /// post needs.
    /// <para>
    /// ⚠ <b><c>ConfigureAwait(true)</c> is LOAD-BEARING here and must not be "corrected".</b> Every
    /// caller of this overload is already ON the UI thread and the continuation has to stay there: a
    /// body that touches a control after resuming on a thread-pool thread is the cross-thread failure
    /// the dispatcher exists to prevent. Opposite polarity from the rest of the kit's library code.
    /// </para>
    /// </summary>
    /// <param name="work">The app-supplied async work to invoke.</param>
    /// <param name="onError">Report sink, guarded exactly as in <see cref="Run"/>. Null = report nowhere.</param>
    public static async Task<bool> RunAsync(Func<Task> work, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            await work().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Report(onError, ex);
            return false;
        }
    }

    /// <summary>
    /// Run <paramref name="work"/> and return its result, or <paramref name="fallback"/> if it threw —
    /// for a callback whose ANSWER the kit needs (a predicate, a policy decision).
    /// </summary>
    /// <param name="work">The app-supplied work to invoke.</param>
    /// <param name="fallback">
    /// The value to use when <paramref name="work"/> throws. A POLICY choice: for a block/deny
    /// predicate, decide at the call site whether failing open or closed is correct there.
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
    /// Write a diagnostic to an app-supplied sink — GUARDED and LAZY.
    /// <para>
    /// <b>An <see cref="ILogger"/> IS an app callback</b>, so it obeys the rule above. Several of these
    /// sinks sit INSIDE a <c>catch</c> that exists to stop a failure escaping, so a throwing sink would
    /// defeat the very guard it is reporting from.
    /// </para>
    /// <para>
    /// <paramref name="message"/> is a <see cref="Func{TResult}"/> so the guard covers BUILDING the
    /// message as well as writing it: several call sites interpolate WebView2/COM properties that throw
    /// once the underlying object is gone. Laziness also honours <see cref="ILogger.IsEnabled"/>, so a
    /// disabled level costs nothing at all.
    /// </para>
    /// <para>
    /// ⚠ <b>ONE shape, and it takes <see cref="ILogger"/>.</b> This used to take
    /// <c>Action&lt;string&gt;</c>, which cannot carry a level, an event id, structured fields or the
    /// EXCEPTION OBJECT — so every diagnostic reporting a caught failure had to interpolate it into a
    /// string, losing its type, its stack and its inner chain, which is the identity a diagnostic exists
    /// to preserve.
    /// </para>
    /// </summary>
    /// <param name="sink">The app's logger. Null = do nothing, and do not build the message.</param>
    /// <param name="message">Builds the line to write. Invoked inside the guard.</param>
    /// <param name="level">
    /// Severity. Null (the default) picks it from <paramref name="exception"/>: <see cref="LogLevel.Debug"/>
    /// for a plain trace, <see cref="LogLevel.Warning"/> when a failure is being reported.
    /// <para>
    /// ⚠ <b>That default is a POLICY and it lives here, once.</b> Warning is what "something unexpected
    /// happened and we carried on" means, which is every swallowed failure in this kit — and it is the
    /// level that survives an app's default <c>Information</c> filter, so a caught failure stays visible
    /// while ordinary tracing does not. Every type's own <c>Log</c> helper would otherwise spell the rule
    /// out again and they would drift apart.
    /// </para>
    /// </param>
    /// <param name="exception">The failure being reported, when there is one.</param>
    public static void Log(ILogger? sink, Func<string> message,
                           LogLevel? level = null, Exception? exception = null)
    {
        if (sink is null) return;
        ArgumentNullException.ThrowIfNull(message);
        var severity = level ?? (exception is null ? LogLevel.Debug : LogLevel.Warning);
        // IsEnabled is itself app code behind an interface, so it goes inside the guard too.
        Run(() =>
        {
            if (!sink.IsEnabled(severity)) return;
#pragma warning disable CA2254 // the kit's diagnostics are composed strings, not templates
            sink.Log(severity, exception, message());
#pragma warning restore CA2254
        });
    }

    /// <summary>
    /// An <see cref="ILogger"/> that writes each formatted line to <paramref name="write"/> — for an app
    /// whose sink IS a delegate (a console, a text box, a probe's transcript) and that has no logging
    /// infrastructure to hand.
    /// <para>
    /// ⚠ <b>Not a substitute for a real provider.</b> It reports every level as enabled and keeps no
    /// scopes, so an app already building an <see cref="ILoggerFactory"/> should pass its own logger and
    /// get filtering, categories and structured fields — all of which this necessarily flattens to text.
    /// </para>
    /// <para>
    /// The EXCEPTION is appended to the line rather than dropped: a delegate sink cannot carry one
    /// alongside the message, and losing it would give back exactly the identity — type, stack, inner
    /// chain — that taking <see cref="ILogger"/> here exists to preserve.
    /// </para>
    /// </summary>
    /// <param name="write">Where a formatted line goes. Called on whatever thread logged.</param>
    /// <remarks>
    /// A factory rather than a public class, so the adapter's shape stays off the SemVer surface while the
    /// capability is reachable — the same reason <c>SegmentEngine.Default</c> is one.
    /// </remarks>
    public static ILogger Logger(Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return new DelegateLogger(write);
    }

    private sealed class DelegateLogger(Action<string> write) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        // Nothing to scope to in a flat text sink, and null is not allowed by the contract.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var line = formatter(state, exception);
            write(exception is null ? line : $"{line}{System.Environment.NewLine}{exception}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
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
            // A failure reporter that throws must not become the crash it reports.
        }
    }
}
