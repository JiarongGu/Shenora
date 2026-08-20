namespace Shenora.Core.Shell;

/// <summary>
/// State of an <see cref="IUiDispatcher"/>'s target. 🔴 THREE states, not a bool: "not created yet" and
/// "gone" demand different caller behaviour, and a single availability flag re-breaks two fixes earned
/// the hard way — a pre-handle post that recursed without end, and one that created a window handle on
/// the wrong thread and killed its message pump.
/// </summary>
public enum UiTargetState
{
    /// <summary>The target exists but is not realized yet — nothing to marshal TO, and it is not dead.</summary>
    NotReady,

    /// <summary>Realized and usable.</summary>
    Ready,

    /// <summary>Disposed or torn down. Never becomes usable again.</summary>
    Gone,
}

/// <summary>
/// The ONE UI-thread marshalling seam. Its invariants are in
/// <c>.claude/knowledge/webview2-hosting.md</c>.
/// <para>
/// Portable on purpose: an app service that needs "run this on the UI thread" depends on this interface,
/// not on WinForms. The Windows implementation is constructed PER CONTROL — auxiliary browser sessions
/// marshal to their anchor form and secondary windows run their own message pumps. ⚠ <c>Shenora</c>
/// registers NO default: a silent no-op default would swallow UI work in a host that forgot to provide
/// one.
/// </para>
/// </summary>
public interface IUiDispatcher
{
    /// <summary>The target's current state. Callers with their own pre-handle policy branch on this.</summary>
    UiTargetState State { get; }

    /// <summary><see cref="State"/> is <see cref="UiTargetState.Ready"/> AND the caller is already on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Run <paramref name="work"/> on the UI thread: inline when already there, otherwise a
    /// NON-BLOCKING post (never a blocking invoke off the UI thread — that deadlock is a measured
    /// application hang in this family). Never throws, including from the inline path.
    /// <para>
    /// TRUE when the work ran or was posted; FALSE only when <see cref="State"/> is not
    /// <see cref="UiTargetState.Ready"/>, which the CALLER decides what to do about. ⚠ A false return
    /// NEVER means the work failed: a posted body's failure happens after this returns.
    /// </para>
    /// </summary>
    bool Post(Action work);

    /// <summary>
    /// <see cref="Post(Action)"/> for an async body, so no caller hand-rolls a fire-and-forget async
    /// post — that is an <c>async void</c> continuation on the UI thread whose exceptions are
    /// unobservable.
    /// </summary>
    bool Post(Func<Task> work);

    /// <summary>
    /// Run on the UI thread and await completion. Faults with <see cref="ObjectDisposedException"/>
    /// when the target is <see cref="UiTargetState.Gone"/> and <see cref="InvalidOperationException"/>
    /// when it is <see cref="UiTargetState.NotReady"/> — never a task that simply never completes. The
    /// returned task observes <paramref name="cancellationToken"/> even when the UI thread is wedged.
    /// </summary>
    Task InvokeAsync(Action work, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="InvokeAsync(Action, CancellationToken)"/>
    Task InvokeAsync(Func<Task> work, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="InvokeAsync(Action, CancellationToken)"/>
    Task<T> InvokeAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default);

    /// <summary>
    /// Never faults: returns <paramref name="fallback"/> if the body throws, the target is not
    /// <see cref="UiTargetState.Ready"/>, or the wait is cancelled. For paths whose contract is that
    /// one bad message must not fault the whole session (the co-browse input path is exactly this).
    /// </summary>
    Task<T> InvokeOrDefaultAsync<T>(Func<Task<T>> work, T fallback, CancellationToken cancellationToken = default);
}
