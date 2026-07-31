using Microsoft.Extensions.Logging;

namespace Shenora.Ipc;

/// <summary>
/// The world a route runs in: which module it speaks for, where it logs, and how it EMITS.
/// <para>
/// This exists because the module contract carried the request path and not the event path
/// (D23): <c>Shenora.Ipc</c> had zero references to <see cref="Shenora.Core.IEventBus"/> while
/// the kit's own <c>DropZoneManager</c> took one as a REQUIRED option, so every app re-agreed
/// the module/type/scope conventions by hand. Publishing is the default gesture here, not a
/// wiring exercise.
/// </para>
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// The owning module — the same string as <see cref="IModuleFacade.ModuleName"/>, supplied by
    /// the kit. A route can therefore never emit under a module it does not own, which is exactly
    /// what a hand-typed literal in every emit call allowed.
    /// </summary>
    string Module { get; }

    /// <summary>The facade's logger (never null — <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> when unconfigured).</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Emit an event on the host bus under <see cref="Module"/>. Fire-and-forget by design:
    /// <see cref="Shenora.Core.IEventBus.Emit(string, string, object?, string?)"/> guarantees a
    /// subscriber cannot fault the caller.
    /// </summary>
    void Publish(string type, object? payload = null, string? scope = null);
}
