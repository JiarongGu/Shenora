using Shenora;

namespace Shenora.Windows;

/// <summary>
/// The WinForms <see cref="IUiDispatcher"/> — the ONE place UI-thread marshalling semantics live.
/// Constructed PER CONTROL (a form, a WebView2 control, a secondary window's own form), because
/// different targets run different message pumps.
/// <para>
/// Public rather than internal on purpose: <c>Shenora.Windows</c> and
/// <c>Shenora.Windows</c> consume it across the package boundary, and a
/// <c>ProjectReference</c> does not grant <c>internal</c> access — so hiding it would mean
/// <c>InternalsVisibleTo</c> for two packages, or a linked source file compiled into both. It is the
/// seam's Windows implementation and earns its keep (D19/D20).
/// </para>
/// <para>
/// Every rule below is an invariant with an incident behind it, not a preference — see
/// <c>.claude/knowledge/webview2-hosting.md</c>:
/// </para>
/// <list type="number">
/// <item><b><see cref="Control.IsHandleCreated"/> BEFORE <see cref="Control.InvokeRequired"/>.</b>
/// Pre-handle, <c>InvokeRequired</c> LIES — it reports false on a worker thread, so a naive check
/// mistakes "no handle yet" for "already on the UI thread" and runs the work off-thread.</item>
/// <item><b>Non-blocking <see cref="Control.BeginInvoke(Delegate)"/>, never a blocking
/// <c>Invoke</c> off the UI thread</b> (a measured application hang).</item>
/// <item><b>The body is guarded, on the posted AND the inline path.</b> An exception from a posted
/// delegate has no caller on its stack, so it becomes an unhandled UI-thread exception; and
/// <see cref="Post(Action)"/> must not start throwing to its caller merely because that caller
/// happened to already be on the UI thread.</item>
/// <item><b>The awaitable overloads observe their cancellation token</b>, so a wedged UI thread
/// can't hold a caller (and its pooled resources) forever.</item>
/// </list>
/// </summary>
public sealed class WinFormsUiDispatcher : IUiDispatcher
{
    private readonly Control _owner;
    private readonly Action<Exception>? _onPostFailure;

    /// <param name="owner">The control whose UI thread work is marshalled to.</param>
    /// <param name="onPostFailure">
    /// Reports an exception thrown by a <see cref="Post(Action)"/> body — there is no caller to
    /// observe it. Null = swallow (still never crashes the UI thread).
    /// </param>
    public WinFormsUiDispatcher(Control owner, Action<Exception>? onPostFailure = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _onPostFailure = onPostFailure;
    }

    /// <inheritdoc />
    public UiTargetState State =>
        _owner.IsDisposed ? UiTargetState.Gone
        : !_owner.IsHandleCreated ? UiTargetState.NotReady
        : UiTargetState.Ready;

    /// <inheritdoc />
    public bool IsOnUiThread => State == UiTargetState.Ready && !_owner.InvokeRequired;

    /// <inheritdoc />
    public bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (State != UiTargetState.Ready) return false;

        if (!_owner.InvokeRequired)
        {
            RunGuarded(work);
            return true;
        }

        try
        {
            _owner.BeginInvoke(new Action(() => RunGuarded(work)));
            return true;
        }
        catch (Exception)
        {
            // The window went down between the state check and the post — tearing down, not an error.
            return false;
        }
    }

    /// <inheritdoc />
    public bool Post(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // The async body is awaited INSIDE a guarded async local function, so this is never a bare
        // `BeginInvoke(async …)` — that shape drops the returned task and makes any fault an
        // unobservable UI-thread crash.
        //
        // The cast to Action is LOAD-BEARING, not decoration. Written as
        // `Post(() => _ = RunGuardedAsync(work))` the lambda body is an EXPRESSION of type Task, so
        // the compiler infers Func<Task> and this method calls ITSELF — unbounded recursion, and a
        // StackOverflowException is uncatchable, so the whole test host aborts with no failing test
        // to point at. (Caught by this file's own async-post test, which is why it exists.)
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

        // Fail with the state's own meaning rather than hanging: a caller can retry a NotReady target
        // but never a Gone one.
        switch (State)
        {
            case UiTargetState.Gone:
                return Task.FromException<T>(new ObjectDisposedException(_owner.GetType().Name,
                    "The UI target has been disposed."));
            case UiTargetState.NotReady:
                return Task.FromException<T>(new InvalidOperationException(
                    "The UI target has no handle yet — nothing to marshal to."));
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunAsync()
        {
            try { tcs.TrySetResult(await work().ConfigureAwait(true)); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }

        if (!_owner.InvokeRequired)
        {
            _ = RunAsync();
        }
        else
        {
            try
            {
                _owner.BeginInvoke(new Action(() => _ = RunAsync()));
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
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

/// <summary>
/// The DI-registered <see cref="IUiDispatcher"/>: dispatches to the application's main window,
/// resolved LAZILY. Internal because only <c>UseWindows</c> constructs it — it needs no
/// cross-package reach, so it stays off the public surface.
/// <para>
/// Lazy resolution is required, not stylistic: the service provider is built BEFORE the runner
/// creates the main form, so a dispatcher captured at registration time would capture null. And the
/// runner never CLEARS the registration, so after shutdown the main form is still reachable but
/// disposed — which is why state is derived per call and <see cref="UiTargetState.Gone"/> is a real
/// outcome here, not a theoretical one.
/// </para>
/// </summary>
internal sealed class MainFormUiDispatcher(IFormInteraction interaction) : IUiDispatcher
{
    private Control? _cachedForm;
    private WinFormsUiDispatcher? _cached;

    private IUiDispatcher? Current
    {
        get
        {
            var form = interaction.GetMainForm();
            if (form is null) return null;
            if (!ReferenceEquals(form, _cachedForm))
            {
                _cachedForm = form;
                _cached = new WinFormsUiDispatcher(form);
            }
            return _cached;
        }
    }

    /// <inheritdoc />
    public UiTargetState State => Current?.State ?? UiTargetState.NotReady;

    /// <inheritdoc />
    public bool IsOnUiThread => Current?.IsOnUiThread ?? false;

    /// <inheritdoc />
    public bool Post(Action work) => Current?.Post(work) ?? false;

    /// <inheritdoc />
    public bool Post(Func<Task> work) => Current?.Post(work) ?? false;

    /// <inheritdoc />
    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default) =>
        Current?.InvokeAsync(work, cancellationToken) ?? NoMainForm<bool>();

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default) =>
        Current?.InvokeAsync(work, cancellationToken) ?? NoMainForm<bool>();

    /// <inheritdoc />
    public Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default) =>
        Current?.InvokeAsync(work, cancellationToken) ?? NoMainForm<T>();

    /// <inheritdoc />
    public Task<T> InvokeOrDefaultAsync<T>(Func<Task<T>> work, T fallback,
                                           CancellationToken cancellationToken = default) =>
        Current?.InvokeOrDefaultAsync(work, fallback, cancellationToken) ?? Task.FromResult(fallback);

    private static Task<T> NoMainForm<T>() => Task.FromException<T>(
        new InvalidOperationException("No main window is registered yet — nothing to marshal to."));
}
