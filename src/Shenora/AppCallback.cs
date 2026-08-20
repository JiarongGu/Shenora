using Microsoft.Extensions.Logging;

namespace Shenora;

/// <summary>
/// Invoke APP-SUPPLIED code from a place where an escaping exception is fatal and uncatchable — a
/// UI-thread event handler, a timer tick, a posted delegate, a dispose path.
/// <para>
/// 🔴 <b>No app callback runs unguarded inside a WebView2/WinForms event handler, and SWALLOWING IS THE
/// POLICY</b> — there is no caller left on the stack, so the alternative to losing the callback's
/// exception is losing the operation, the window, or the process. Callers that can report get an
/// <c>onError</c> hook, itself guarded.
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
    /// <paramref name="onError"/>), and return whether it completed.
    /// <para>
    /// ⚠ <b><c>ConfigureAwait(true)</c> is LOAD-BEARING here and must not be "corrected"</b> — opposite
    /// polarity from the rest of the kit's library code. Every caller is already ON the UI thread, and a
    /// body that touches a control after resuming on a thread-pool thread is the cross-thread failure the
    /// dispatcher exists to prevent.
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
    /// Write a diagnostic to an app-supplied sink — GUARDED and LAZY, because an <see cref="ILogger"/>
    /// IS an app callback and several of these sinks sit INSIDE a <c>catch</c> that exists to stop a
    /// failure escaping.
    /// </summary>
    /// <param name="sink">The app's logger. Null = do nothing, and do not build the message.</param>
    /// <param name="message">
    /// Builds the line to write, INSIDE the guard: call sites interpolate WebView2/COM properties that
    /// throw once the underlying object is gone.
    /// </param>
    /// <param name="level">
    /// Severity. Null (the default) picks it from <paramref name="exception"/>: <see cref="LogLevel.Debug"/>
    /// for a plain trace, <see cref="LogLevel.Warning"/> when a failure is being reported — the level that
    /// survives an app's default <c>Information</c> filter.
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
    /// whose sink IS a delegate (a console, a text box, a probe's transcript).
    /// <para>
    /// ⚠ <b>Not a substitute for a real provider:</b> it reports every level as enabled, keeps no scopes,
    /// and flattens the exception into the line.
    /// </para>
    /// </summary>
    /// <param name="write">Where a formatted line goes. Called on whatever thread logged.</param>
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
