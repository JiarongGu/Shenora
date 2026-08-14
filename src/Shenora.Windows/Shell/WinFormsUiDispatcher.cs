using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// The WinForms <see cref="IUiDispatcher"/> — the ONE place UI-thread marshalling semantics live.
/// Constructed PER CONTROL (a form, a WebView2 control, a secondary window's own form), because
/// different targets run different message pumps.
/// <para>
/// Public rather than internal on purpose: an app hosting its OWN <see cref="Control"/> — a form the
/// kit never sees — constructs one for it, which is the whole point of a per-control dispatcher. It is
/// the <see cref="IUiDispatcher"/> seam's Windows implementation and earns its keep (D19/D20).
/// </para>
/// <para>
/// ⚠ This paragraph argued the access level from a PACKAGE BOUNDARY until 2026-08-10, and read
/// <i>"Shenora.Windows and Shenora.Windows consume it across the package boundary"</i> — the same name
/// on both sides. D37 merged those two packages into one on 2026-08-02 and the repo-wide sweep rewrote
/// both halves of the sentence to the survivor, leaving prose that was grammatical, shipped in the
/// nupkg, and nonsense exactly where a reader goes to learn why the type is public. The rule for this
/// is in <c>phase-workflow.md</c>; what let it survive is that <c>self-rename-scan</c> read only
/// <c>.md</c> files, so the highest-stakes prose in the repo was the one place it never looked.
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
/// <see cref="UiDispatcherBase.Post(Action)"/> must not start throwing to its caller merely because
/// that caller happened to already be on the UI thread.</item>
/// <item><b>The awaitable overloads observe their cancellation token</b>, so a wedged UI thread
/// can't hold a caller (and its pooled resources) forever.</item>
/// </list>
/// </summary>
public sealed class WinFormsUiDispatcher : UiDispatcherBase
{
    private readonly Control _owner;

    /// <param name="owner">The control whose UI thread work is marshalled to.</param>
    /// <param name="onPostFailure">
    /// Reports an exception thrown by a <see cref="UiDispatcherBase.Post(Action)"/> body — there is no
    /// caller to observe it. Null = swallow (still never crashes the UI thread).
    /// </param>
    public WinFormsUiDispatcher(Control owner, Action<Exception>? onPostFailure = null)
        : base(onPostFailure)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc />
    /// <remarks>
    /// THREE states because a <see cref="Control"/> genuinely has three: it exists before its handle does.
    /// This is the distinction <see cref="UiTargetState"/> was created for.
    /// </remarks>
    public override UiTargetState State =>
        _owner.IsDisposed ? UiTargetState.Gone
        : !_owner.IsHandleCreated ? UiTargetState.NotReady
        : UiTargetState.Ready;

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 <see cref="Control.IsHandleCreated"/> BEFORE <see cref="Control.InvokeRequired"/> — via
    /// <see cref="State"/>. Pre-handle, <c>InvokeRequired</c> LIES (it reports false on a worker thread),
    /// so testing it alone mistakes "no handle yet" for "already on the UI thread".
    /// </remarks>
    public override bool IsOnUiThread => State == UiTargetState.Ready && !_owner.InvokeRequired;

    /// <summary>The name in an <see cref="ObjectDisposedException"/> is the CONTROL's, not the dispatcher's.</summary>
    protected override string TargetName => _owner.GetType().Name;

    /// <inheritdoc />
    /// <remarks>
    /// Non-blocking <see cref="Control.BeginInvoke(Delegate)"/>, never a blocking <c>Invoke</c> off the UI
    /// thread — a measured application hang in this family. A throw here means the window went down
    /// between the state check and the post; it is handed back rather than swallowed, because
    /// <see cref="UiDispatcherBase.InvokeAsync{T}(Func{Task{T}}, CancellationToken)"/> faults its task
    /// with the real reason while <see cref="UiDispatcherBase.Post(Action)"/> only wants the false.
    /// </remarks>
    protected override bool TryPost(Action work, out Exception? failure)
    {
        try
        {
            _owner.BeginInvoke(work);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
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
