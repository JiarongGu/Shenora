namespace Shenora.Core.Shell;

/// <summary>
/// The shell-independent half of <see cref="IUiDispatcher"/>: everything about marshalling that is the
/// CONTRACT rather than the platform, implemented once.
///
/// <para>
/// 🔴 <b>A mirror is a rule that must be applied twice.</b> These invariants belong to the contract, so
/// stating them once here is what stops two shells drifting apart on ordering, guarding, cancellation or
/// failure shape — the same collapse <see cref="AppCallback"/> is.
/// </para>
///
/// <para>
/// <b>What a shell still owns</b> — three hooks, and nothing else: <see cref="State"/>,
/// <see cref="IsOnUiThread"/>, and <see cref="TryPost"/>. Everything a caller can observe about ordering,
/// guarding, cancellation and failure shape is decided here.
/// </para>
///
/// <para>
/// ⚠ <b>Public because the implementations live in OTHER assemblies</b> (<c>Shenora.Windows</c>, and
/// <c>Shenora.Android</c>/<c>Shenora.iOS</c> through the shared <c>Shenora.Mobile</c> source), and a
/// <c>ProjectReference</c> does not grant <c>protected</c> access across one — the same D19/D20 placement
/// argument that makes <see cref="AppCallback"/> and <c>WinFormsUiDispatcher</c> public.
/// </para>
/// </summary>
public abstract class UiDispatcherBase : IUiDispatcher
{
    private readonly Action<Exception>? _onPostFailure;

    /// <param name="onPostFailure">
    /// Reports an exception thrown by a <see cref="Post(Action)"/> body — there is no caller left to
    /// observe it. Null = swallow (still never crashes the UI thread).
    /// </param>
    protected UiDispatcherBase(Action<Exception>? onPostFailure = null) => _onPostFailure = onPostFailure;

    /// <inheritdoc />
    public abstract UiTargetState State { get; }

    /// <inheritdoc />
    public abstract bool IsOnUiThread { get; }

    /// <summary>
    /// Post <paramref name="work"/> to the UI thread WITHOUT running it inline and without guarding it —
    /// both are this base's job. Return false when the platform refused it.
    /// <para>
    /// ⚠ <b>Never throw.</b> A platform that throws on a torn-down target must catch it and report it
    /// through <paramref name="failure"/>, because the two callers want different things from it:
    /// <see cref="Post(Action)"/> answers a plain false (the target went down between the state check and
    /// the post — tearing down, not an error), while <see cref="InvokeAsync{T}(Func{Task{T}}, CancellationToken)"/>
    /// faults its task with it, so a caller awaiting a marshalled call learns what actually happened
    /// rather than a synthesized stand-in.
    /// </para>
    /// </summary>
    /// <param name="work">The already-guarded delegate to marshal.</param>
    /// <param name="failure">
    /// What the platform refused with, or null when it refused without one. Only read when this returns
    /// false.
    /// </param>
    protected abstract bool TryPost(Action work, out Exception? failure);

    /// <summary>
    /// What an <see cref="ObjectDisposedException"/> from here names. Defaults to this dispatcher's own
    /// type; a shell whose target is a distinct object (a WinForms <c>Control</c>) overrides it so the
    /// message names the thing that actually went away.
    /// </summary>
    protected virtual string TargetName => GetType().Name;

    /// <inheritdoc />
    public bool Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (State != UiTargetState.Ready) return false;

        if (IsOnUiThread)
        {
            RunGuarded(work);
            return true;
        }

        return TryPost(() => RunGuarded(work), out _);
    }

    /// <inheritdoc />
    public bool Post(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        // The async body is awaited INSIDE a guarded async local function, so this is never a bare
        // `BeginInvoke(async …)` / `Dispatch(async …)` — that shape drops the returned task and makes any
        // fault an unobservable UI-thread crash.
        //
        // 🔴 The cast to Action is LOAD-BEARING. Written as `Post(() => _ = RunGuardedAsync(work))` the
        // lambda body is an EXPRESSION of type Task, so the compiler infers Func<Task> and this method
        // calls ITSELF — unbounded recursion, and a StackOverflowException is uncatchable, so the host
        // aborts with nothing to point at.
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

        // Fail with the state's own meaning rather than hanging: a caller can retry a NotReady target but
        // never a Gone one. A shell whose target has no pre-realized phase simply never reports NotReady.
        switch (State)
        {
            case UiTargetState.Gone:
                return Task.FromException<T>(new ObjectDisposedException(TargetName,
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

        if (IsOnUiThread)
        {
            _ = RunAsync();
        }
        else if (!TryPost(() => _ = RunAsync(), out var failure))
        {
            // Refused the work: nothing will ever run, so fail rather than hand back a task that never
            // completes — the "never a task that simply never completes" rule the interface states for a
            // Gone target. The platform's own exception when it had one, so the caller sees what happened.
            return Task.FromException<T>(failure ?? new ObjectDisposedException(TargetName,
                "The UI thread refused the work — the target is gone."));
        }

        // WaitAsync, not a bare await: the token must be observable even when the UI thread never gets
        // around to running the body.
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

    // Guarding an app callback, and guarding the SINK that reports its failure, are one rule with one
    // owner. `AppCallback.RunAsync` keeps the `ConfigureAwait(true)` this path requires, and says why.
    private void RunGuarded(Action work) => AppCallback.Run(work, _onPostFailure);

    private Task RunGuardedAsync(Func<Task> work) => AppCallback.RunAsync(work, _onPostFailure);
}
