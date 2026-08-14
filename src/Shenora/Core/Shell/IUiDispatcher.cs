namespace Shenora.Core.Shell;

/// <summary>
/// State of an <see cref="IUiDispatcher"/>'s target. THREE states, not a bool: "not created yet" and
/// "gone" demand different caller behaviour, and collapsing them is how a single availability flag
/// re-breaks fixes that were earned the hard way (a pre-handle post that recursed without end; a
/// pre-handle post that created a window handle on the wrong thread and killed its message pump).
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
/// The ONE UI-thread marshalling seam. Specified in the design contract's package table from the
/// start and never built until P5.5, which is why the pattern ended up hand-rolled 14 times across
/// three packages with five mutually incompatible pre-handle policies — and why two of those copies
/// carried real defects (see <c>docs/DECISIONS.md</c> D19/D20, and
/// <c>.claude/knowledge/webview2-hosting.md</c> for the four invariants this owner keeps).
/// <para>
/// Portable on purpose: an app service that needs "run this on the UI thread" depends on this
/// interface, not on WinForms, so the same logic runs on another shell. The Windows implementation is
/// <c>Shenora.Windows.WinFormsUiDispatcher</c>, constructed PER CONTROL — auxiliary browser sessions
/// marshal to their anchor form and secondary windows run their own message pumps, so one
/// application-wide dispatcher would be wrong for both. <c>Shenora</c> deliberately registers NO
/// default: it has no UI thread to dispatch to, and a silent no-op default would swallow UI work in a
/// host that forgot to provide one.
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
    /// application hang in this family).
    /// <para>
    /// Returns TRUE when the work ran or was posted. Returns FALSE only when
    /// <see cref="State"/> is not <see cref="UiTargetState.Ready"/> — the CALLER decides what that
    /// means for it (drop and log, defer behind a flag, or apply directly); inspect
    /// <see cref="State"/> to tell "not ready yet" from "gone". A false return NEVER means the work
    /// failed: a posted body's failure happens after this returns, by definition.
    /// </para>
    /// <para>Never throws — including from the inline path.</para>
    /// </summary>
    bool Post(Action work);

    /// <summary>
    /// <see cref="Post(Action)"/> for an async body. This overload exists so no caller ever
    /// hand-rolls a fire-and-forget async post: that is an <c>async void</c> continuation on the UI
    /// thread whose exceptions are unobservable, which this codebase has already paid for once.
    /// </summary>
    bool Post(Func<Task> work);

    /// <summary>
    /// Run on the UI thread and await completion. Faults with <see cref="ObjectDisposedException"/>
    /// when the target is <see cref="UiTargetState.Gone"/> and <see cref="InvalidOperationException"/>
    /// when it is <see cref="UiTargetState.NotReady"/> — never a task that simply never completes.
    /// The returned task observes <paramref name="cancellationToken"/>: an operation that accepts a
    /// token and then ignores it cannot be cancelled when the UI thread is wedged, which turns a slow
    /// call into a permanently leaked resource.
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
