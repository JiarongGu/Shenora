using Microsoft.Maui.Dispatching;
using Shenora;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI <see cref="IUiDispatcher"/>: the three platform hooks over
/// <see cref="UiDispatcherBase"/>, which owns everything a caller can observe.
/// </summary>
public sealed class MobileUiDispatcher : UiDispatcherBase
{
    private readonly IDispatcher _dispatcher;

    /// <param name="dispatcher">The MAUI dispatcher work is marshalled to.</param>
    /// <param name="onPostFailure">
    /// Reports an exception thrown by a <see cref="UiDispatcherBase.Post(Action)"/> body — there is no
    /// caller to observe it. Null = swallow.
    /// </param>
    public MobileUiDispatcher(IDispatcher dispatcher, Action<Exception>? onPostFailure = null)
        : base(onPostFailure)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }


    /// <inheritdoc />
    /// <remarks>
    /// MAUI's <see cref="IDispatcher"/> has no pre-realized phase to observe, so this is always
    /// <see cref="UiTargetState.Ready"/> — a caller branching on <see cref="UiTargetState.NotReady"/> will
    /// never see it on this shell.
    /// </remarks>
    public override UiTargetState State => UiTargetState.Ready;

    /// <inheritdoc />
    public override bool IsOnUiThread => !_dispatcher.IsDispatchRequired;

    /// <inheritdoc />
    /// <remarks>
    /// <c>Dispatch</c> is the non-blocking post. ⚠ Never <c>DispatchAsync().Wait()</c> — a blocking marshal
    /// off the UI thread hangs the application. It answers false rather than throwing, so the refusal is
    /// turned into an exception here for the base to fault an awaited call with.
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
