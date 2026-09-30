using Microsoft.Extensions.Logging;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The Chromium shell's <see cref="IUiDispatcher"/>: CEF's UI thread, which under the Views shell IS the
/// main thread running CEF's message loop. <see cref="UiTargetState.NotReady"/> until CEF's context exists,
/// <see cref="UiTargetState.Ready"/> while it runs, <see cref="UiTargetState.Gone"/> once shutdown begins.
/// <para>
/// The two primitives, "post to the UI thread" and "am I on it", are injected so the state machine is
/// testable without CEF; the runner passes <c>cef_post_task</c> and <c>cef_currently_on</c>.
/// </para>
/// </summary>
internal sealed class CefUiDispatcher : UiDispatcherBase
{
    private readonly Func<Action, bool> _post;
    private readonly Func<bool> _onUiThread;
    private readonly Action<Exception> _failed;
    private int _state = (int)UiTargetState.NotReady;

    // A posted body that throws is an unhandled UI-thread exception, as WinForms' Application.ThreadException is: the
    // loop goes on, the log has it, and so does the app's OnUnhandledException.
    public CefUiDispatcher(Func<Action, bool> post, Func<bool> onUiThread, ILogger? log = null)
        : this(post, onUiThread, ex =>
        {
            AppCallback.Log(log, () => "[Shenora.Chromium] Posted UI work failed", LogLevel.Warning, ex);
            ChromiumUnhandledExceptions.Report(ex, UnhandledExceptionSource.UiThread, isTerminating: false);
        })
    {
    }

    private CefUiDispatcher(Func<Action, bool> post, Func<bool> onUiThread, Action<Exception> failed)
        : base(failed)
    {
        _post = post;
        _onUiThread = onUiThread;
        _failed = failed;
    }

    public override UiTargetState State => (UiTargetState)Volatile.Read(ref _state);

    public override bool IsOnUiThread => State == UiTargetState.Ready && _onUiThread();

    protected override string TargetName => "CEF's UI thread";

    /// <summary>CEF's context exists: work can run. Called on the UI thread.</summary>
    public void MarkReady() => Interlocked.CompareExchange(ref _state, (int)UiTargetState.Ready, (int)UiTargetState.NotReady);

    /// <summary>Shutdown has begun: nothing posted from here on would ever run.</summary>
    public void MarkGone() => Volatile.Write(ref _state, (int)UiTargetState.Gone);

    // CEF refuses a task once its UI thread is gone; that answer is Gone, not a failed body.
    protected override bool TryPost(Action work, out Exception? failure)
    {
        failure = null;
        if (_post(work)) return true;
        MarkGone();
        return false;
    }

    /// <summary>
    /// Queue work behind what the UI thread is doing, guarded, even when called ON it: unlike
    /// <see cref="UiDispatcherBase.Post(Action)"/>, which runs there inline. False once the thread is not running.
    /// </summary>
    public bool Queue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return State == UiTargetState.Ready && TryPost(() => AppCallback.Run(work, _failed), out _);
    }
}

/// <summary>
/// Continuations back onto CEF's UI thread. CEF raises everything there with NO synchronization context,
/// so without this the kit's context-preserving dispatch (a route's awaits resume where it started) has
/// no UI thread to preserve. The runner installs it on the main thread before CEF's loop starts.
/// </summary>
internal sealed class CefUiContext(CefUiDispatcher dispatcher, ILogger? log = null) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        // Queued even when called on the UI thread: a post runs after its caller, so Task.Yield yields and a
        // continuation never re-enters the code that scheduled it. Refused only once shutdown has begun; running it
        // here instead would touch CEF off its thread after shutdown, and dropping it costs a task that never
        // finishes in a process that is ending.
        if (!dispatcher.Queue(() => d(state)))
            AppCallback.Log(log, () => "[Shenora.Chromium] A continuation arrived after shutdown began and was dropped", LogLevel.Debug);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (!dispatcher.IsOnUiThread) throw new NotSupportedException("Send off CEF's UI thread would block it.");
        d(state);
    }

    public override SynchronizationContext CreateCopy() => this;
}
