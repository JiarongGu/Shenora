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
internal sealed class CefUiDispatcher : IUiDispatcher
{
    private readonly Func<Action, bool> _post;
    private readonly Func<bool> _onUiThread;
    private readonly ILogger? _log;
    private int _state = (int)UiTargetState.NotReady;

    public CefUiDispatcher(Func<Action, bool> post, Func<bool> onUiThread, ILogger? log = null)
    {
        _post = post;
        _onUiThread = onUiThread;
        _log = log;
    }

    public UiTargetState State => (UiTargetState)Volatile.Read(ref _state);

    public bool IsOnUiThread => State == UiTargetState.Ready && _onUiThread();

    /// <summary>CEF's context exists: work can run. Called on the UI thread.</summary>
    public void MarkReady() => Interlocked.CompareExchange(ref _state, (int)UiTargetState.Ready, (int)UiTargetState.NotReady);

    /// <summary>Shutdown has begun: nothing posted from here on would ever run.</summary>
    public void MarkGone() => Volatile.Write(ref _state, (int)UiTargetState.Gone);

    public bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (State != UiTargetState.Ready) return false;
        if (_onUiThread())
        {
            Guarded(work);
            return true;
        }
        // CEF refuses a task once its UI thread is gone; that answer is Gone, not a failed body.
        return _post(() => Guarded(work)) || MarkGoneAndRefuse();
    }

    public bool Post(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Post(() => Observe(work));
    }

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(() => { work(); return Task.FromResult(true); }, cancellationToken);
    }

    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(async () => { await work(); return true; }, cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        switch (State)
        {
            case UiTargetState.Gone: return Task.FromException<T>(new ObjectDisposedException(nameof(CefUiDispatcher), "CEF's UI thread has shut down."));
            case UiTargetState.NotReady: return Task.FromException<T>(new InvalidOperationException("CEF's UI thread is not running yet."));
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Body()
        {
            try
            {
                var task = work();
                task.ContinueWith(t =>
                {
                    if (t.IsCanceled) done.TrySetCanceled();
                    else if (t.IsFaulted) done.TrySetException(t.Exception!.InnerExceptions);
                    else done.TrySetResult(t.Result);
                }, TaskScheduler.Default);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }

        if (_onUiThread()) Body();
        else if (!_post(Body)) { MarkGone(); done.TrySetException(new ObjectDisposedException(nameof(CefUiDispatcher), "CEF's UI thread has shut down.")); }
        // Observes the token even when the UI thread is wedged, rather than a task that never completes.
        return done.Task.WaitAsync(cancellationToken);
    }

    public async Task<T> InvokeOrDefaultAsync<T>(Func<Task<T>> work, T fallback, CancellationToken cancellationToken = default)
    {
        try { return await InvokeAsync(work, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] UI work fell back to its default", LogLevel.Debug, ex);
            return fallback;
        }
    }

    private bool MarkGoneAndRefuse()
    {
        MarkGone();
        return false;
    }

    private void Guarded(Action work) =>
        AppCallback.Run(work, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] Posted UI work failed", LogLevel.Warning, ex));

    /// <summary>An async body's fault is logged, never an unobserved task.</summary>
    private void Observe(Func<Task> work)
    {
        Task task;
        try { task = work(); }
        catch (Exception ex) { AppCallback.Log(_log, () => "[Shenora.Chromium] Posted UI work failed", LogLevel.Warning, ex); return; }
        task.ContinueWith(t => AppCallback.Log(_log, () => "[Shenora.Chromium] Posted UI work failed", LogLevel.Warning, t.Exception),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
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
        // Refused only once shutdown has begun. Running it here instead would touch CEF off its thread after
        // shutdown; dropping it costs a task that never finishes in a process that is ending.
        if (!dispatcher.Post(() => d(state)))
            AppCallback.Log(log, () => "[Shenora.Chromium] A continuation arrived after shutdown began and was dropped", LogLevel.Debug);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (!dispatcher.IsOnUiThread) throw new NotSupportedException("Send off CEF's UI thread would block it.");
        d(state);
    }

    public override SynchronizationContext CreateCopy() => this;
}
