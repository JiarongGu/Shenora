using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// The world a route runs in: which module it speaks for, where it logs, and how it EMITS.
/// <para>
/// This exists because the module contract carried the request path and not the event path
/// (D23): <c>Shenora.Ipc</c> had zero references to <see cref="Shenora.Core.Events.IEventBus"/> while
/// the kit's own <c>DropZoneManager</c> took one as a REQUIRED option, so every app re-agreed
/// the module/type/scope conventions by hand. Publishing is the default gesture here, not a
/// wiring exercise.
/// </para>
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// The owning module — the same string as <see cref="IIpcModule.ModuleName"/>, supplied by
    /// the kit. A route can therefore never emit under a module it does not own, which is exactly
    /// what a hand-typed literal in every emit call allowed.
    /// </summary>
    string Module { get; }

    /// <summary>The facade's logger (never null — <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> when unconfigured).</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Emit an event on the host bus under <see cref="Module"/>. Fire-and-forget by design:
    /// <see cref="Shenora.Core.Events.IEventBus.Emit(string, string, object?, string?)"/> guarantees a
    /// subscriber cannot fault the caller.
    /// </summary>
    void Publish(string type, object? payload = null, string? scope = null);

    /// <summary>
    /// Start a tracked operation owned by this module and get its handle — for work whose lifecycle
    /// does not match <see cref="Run"/> 1:1 (a start outside the background body, several failure
    /// branches, a resumable session). This is the real primitive; <see cref="Run"/> is the sugar.
    /// </summary>
    IOperation Start(OperationOptions options);

    /// <summary>
    /// Start the operation, hand <paramref name="work"/> OFF to the background, and finish it:
    /// <c>Complete</c> on success, <c>Cancel</c> on <see cref="OperationCanceledException"/>,
    /// <c>Fail</c> otherwise. Returns the operation id IMMEDIATELY — a route that awaits long work
    /// blocks the dispatch, and the dispatch is on the UI thread.
    /// <para>
    /// The work gets the OPERATION's token, never the request's: work handed off outlives the
    /// request, and capturing the request token kills it the moment the page navigates.
    /// </para>
    /// <para>
    /// <b>Waiting by returning</b> (§5A.3): <paramref name="work"/> can call <c>op.Wait(reason)</c>
    /// and then simply RETURN instead of throwing — <c>Run</c> only implicitly completes the
    /// operation when it is STILL <see cref="OperationStatus.Running"/> once <paramref name="work"/>
    /// finishes, so a body that waited and returned is left exactly as
    /// <see cref="OperationStatus.Waiting"/>, with no live body watching it. Resuming it from there is
    /// the APP's job — the same handle's <c>op.Resume()</c> if the app kept a reference, or its own
    /// restart path otherwise. This is deliberate: completing it here anyway would be a third lie
    /// alongside "keep it Running" and "Fail it", the two lies §5A.2 exists to remove.
    /// </para>
    /// </summary>
    string Run(OperationOptions options, Func<IOperation, CancellationToken, Task> work);
}
