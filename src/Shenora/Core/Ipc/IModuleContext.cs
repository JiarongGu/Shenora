using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Core.Ipc;

/// <summary>
/// The world ONE REQUEST runs in: which module it speaks for, which request it is, where it logs, and
/// how it EMITS.
/// <para>
/// 🔴 <b>PER REQUEST since D66 — it used to be built once per module and reused.</b> That is what lets
/// <see cref="Report"/> take no id: the context already knows which request it belongs to, because there
/// is only one identity now. The type's own name said "the world a route runs in" the whole time; it
/// simply was not true yet.
/// </para>
/// <para>
/// <b>What went, and why nothing replaced it:</b> <c>Start(OperationOptions)</c> and
/// <c>Run(options, work)</c>. They minted a SECOND identity — a fresh <c>Guid</c> unrelated to the request
/// that caused it — carrying a <c>Kind</c>, a <c>Scope</c> and a <c>StartedAt</c> the request already had,
/// leaving the page to correlate the two. Nothing in the repo ever called either of them.
/// </para>
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
    /// The id of the request being handled — <see cref="IpcRequest.Id"/>, the one identity this request
    /// has anywhere: in the response, in every progress snapshot, and in a cancel that targets it.
    /// </summary>
    string RequestId { get; }

    /// <summary>
    /// Report progress on THIS request — the <c>progress</c> event of the XHR model, keyed by the request
    /// id automatically.
    /// <para>
    /// ⚠ <b>Nothing to declare, and usually nothing to see.</b> A request that finishes inside the grace
    /// period (<see cref="IpcRequestTrackerOptions.GracePeriod"/>, 50 ms) emits NOTHING — not this, not a
    /// running snapshot, not a completion. The values are still KEPT, so the first snapshot of a request
    /// that does outlive the window carries the latest of them. Calling this on a fast path is therefore
    /// free rather than wasteful, which is what makes "report progress everywhere" a safe habit.
    /// </para>
    /// </summary>
    /// <param name="progress">How far along, in the app's own unit. Null leaves the last value.</param>
    /// <param name="detail">Optional human-facing label, i18n-ready. Null leaves the last one.</param>
    void Report(IpcProgress? progress = null, IpcLabel? detail = null);
}
