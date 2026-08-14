using Microsoft.Maui.Dispatching;
using Shenora;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI <see cref="IUiDispatcher"/> — the ONE place UI-thread marshalling semantics live on this
/// shell, mirroring <c>Shenora.Windows.WinFormsUiDispatcher</c> member for member. The invariants
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
public sealed class MobileUiDispatcher : UiDispatcherBase
{
    private readonly IDispatcher _dispatcher;

    /// <param name="dispatcher">The MAUI dispatcher work is marshalled to.</param>
    /// <param name="onPostFailure">
    /// Reports an exception thrown by a <see cref="UiDispatcherBase.Post(Action)"/> body — there is no
    /// caller to observe it. Null = swallow (still never crashes the UI thread).
    /// </param>
    public MobileUiDispatcher(IDispatcher dispatcher, Action<Exception>? onPostFailure = null)
        : base(onPostFailure)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }


    /// <inheritdoc />
    /// <remarks>
    /// 🔴 The one real difference from the WinForms shell, and why <see cref="UiTargetState"/> is not a
    /// bool. A WinForms <c>Control</c> exists before its handle does; MAUI's <see cref="IDispatcher"/> has
    /// no pre-realized phase to observe, so this reports <see cref="UiTargetState.Ready"/> whenever the
    /// dispatcher exists and <see cref="UiTargetState.NotReady"/> is unreachable here — recorded rather
    /// than faked, because a caller branching on it should know it will not see it on this shell.
    /// </remarks>
    public override UiTargetState State => UiTargetState.Ready;

    /// <inheritdoc />
    public override bool IsOnUiThread => !_dispatcher.IsDispatchRequired;

    /// <inheritdoc />
    /// <remarks>
    /// <c>Dispatch</c> is the non-blocking post. Never <c>DispatchAsync().Wait()</c> — a blocking marshal
    /// off the UI thread is the measured application hang the WinForms owner also refuses.
    /// <para>
    /// It answers false rather than throwing when it refuses, so the refusal is turned into an exception
    /// here — the base needs one to fault an awaited <c>InvokeAsync</c> with, and "the dispatcher said no"
    /// is exactly a gone target.
    /// </para>
    /// </remarks>
    protected override bool TryPost(Action work, out Exception? failure)
    {
        try
        {
            if (_dispatcher.Dispatch(work))
            {
                failure = null;
                return true;
            }

            failure = new ObjectDisposedException(nameof(IDispatcher),
                "The MAUI dispatcher refused the work — the window is gone.");
            return false;
        }
        catch (Exception ex)
        {
            // The window went down between the check and the post — tearing down, not an error.
            failure = ex;
            return false;
        }
    }
}
