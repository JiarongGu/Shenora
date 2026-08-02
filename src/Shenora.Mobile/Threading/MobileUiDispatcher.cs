using Microsoft.Maui.Dispatching;
using Shenora.Core;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI <see cref="IUiDispatcher"/> — the ONE place UI-thread marshalling semantics live on this
/// shell, mirroring <c>Shenora.WinForms.WinFormsUiDispatcher</c> member for member. The invariants
/// are the CONTRACT, not the platform, so they are kept identically here: never a blocking marshal
/// off the UI thread, the body guarded on both the inline and the posted path, a false return only
/// when there is nowhere to post, and the awaitable overloads observing their token.
/// <para>
/// The one real difference is <see cref="State"/>. WinForms has three genuinely distinct states
/// because a control exists before its handle does; MAUI's <see cref="IDispatcher"/> has no
/// pre-realized phase to observe, so this reports <see cref="UiTargetState.Ready"/> whenever the
/// dispatcher exists. <see cref="UiTargetState.NotReady"/> is therefore unreachable here — recorded
/// rather than faked, because a caller branching on it should know it will not see it on this shell.
/// </para>
/// </summary>
public sealed class MobileUiDispatcher : IUiDispatcher
{
    private readonly IDispatcher _dispatcher;
    private readonly Action<Exception>? _onPostFailure;

    /// <param name="dispatcher">The MAUI dispatcher work is marshalled to.</param>
    /// <param name="onPostFailure">
    /// Reports an exception thrown by a <see cref="Post(Action)"/> body — there is no caller to
    /// observe it. Null = swallow (still never crashes the UI thread).
    /// </param>
    public MobileUiDispatcher(IDispatcher dispatcher, Action<Exception>? onPostFailure = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _onPostFailure = onPostFailure;
    }

    /// <inheritdoc />
    public UiTargetState State => UiTargetState.Ready;

    /// <inheritdoc />
    public bool IsOnUiThread => !_dispatcher.IsDispatchRequired;

    /// <inheritdoc />
    public bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (!_dispatcher.IsDispatchRequired)
        {
            RunGuarded(work);
            return true;
        }

        try
        {
            // Dispatch is the non-blocking post. Never DispatchAsync().Wait() — a blocking marshal
            // off the UI thread is the measured application hang the WinForms owner also refuses.
            return _dispatcher.Dispatch(() => RunGuarded(work));
        }
        catch (Exception)
        {
            // The window went down between the check and the post — tearing down, not an error.
            return false;
        }
    }

    /// <inheritdoc />
    public bool Post(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // The cast to Action is LOAD-BEARING (the WinForms owner carries the same comment for the
        // same reason): without it the lambda body is an expression of type Task, the compiler infers
        // Func<Task>, and this method calls ITSELF — unbounded recursion, and a StackOverflow is
        // uncatchable, so the host aborts with nothing to point at.
        return Post((Action)(() => { _ = RunGuardedAsync(work); }));
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync<bool>(() => { work(); return Task.FromResult(true); }, cancellationToken);
    }

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync<bool>(async () => { await work().ConfigureAwait(true); return true; }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunAsync()
        {
            try { tcs.TrySetResult(await work().ConfigureAwait(true)); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        if (!_dispatcher.IsDispatchRequired)
        {
            _ = RunAsync();
        }
        else if (!_dispatcher.Dispatch(() => _ = RunAsync()))
        {
            // Refused the work: nothing will ever run, so fail rather than hand back a task that
            // never completes — the same "never a task that simply never completes" rule the
            // interface states for a Gone target.
            return Task.FromException<T>(new ObjectDisposedException(nameof(IDispatcher),
                "The MAUI dispatcher refused the work — the window is gone."));
        }

        // WaitAsync, not a bare await: the token must be observable even when the UI thread never
        // gets around to running the body.
        return cancellationToken.CanBeCanceled ? tcs.Task.WaitAsync(cancellationToken) : tcs.Task;
    }

    /// <inheritdoc />
    public async Task<T> InvokeOrDefaultAsync<T>(Func<Task<T>> work, T fallback,
                                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        try { return await InvokeAsync(work, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { return fallback; }
    }

    private void RunGuarded(Action work)
    {
        try { work(); }
        catch (Exception ex) { Report(ex); }
    }

    private async Task RunGuardedAsync(Func<Task> work)
    {
        try { await work().ConfigureAwait(true); }
        catch (Exception ex) { Report(ex); }
    }

    private void Report(Exception ex)
    {
        try { _onPostFailure?.Invoke(ex); }
        catch (Exception) { /* a failure reporter that throws must not become the crash it reports */ }
    }
}
