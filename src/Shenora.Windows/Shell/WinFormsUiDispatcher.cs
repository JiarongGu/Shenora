using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// The WinForms <see cref="IUiDispatcher"/> — the ONE place UI-thread marshalling semantics live.
/// Constructed PER CONTROL (a form, a WebView2 control, a secondary window's own form), because
/// different targets run different message pumps.
/// <para>
/// Public so an app can construct one for a <see cref="Control"/> the kit never sees (D19/D20).
/// </para>
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
    /// <remarks>A <see cref="Control"/> exists before its handle does — hence three states, not a bool.</remarks>
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
    /// thread — a measured application hang. A throw here means the window went down between the state
    /// check and the post; it is handed back rather than swallowed.
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
/// resolved LAZILY per call. The service provider is built BEFORE the runner creates the main form, so
/// a dispatcher captured at registration time captures null; and the runner never CLEARS the
/// registration, so after shutdown the form is still reachable but disposed
/// (<see cref="UiTargetState.Gone"/> is a real outcome here).
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
