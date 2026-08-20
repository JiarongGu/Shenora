using Microsoft.Extensions.Logging;

namespace Shenora.Core.Ipc;

/// <summary>
/// The world ONE REQUEST runs in: which module it speaks for, which request it is, where it logs, how it
/// EMITS and how it reports progress. Built per request (D66), which is what lets
/// <see cref="Report"/> take no id.
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// The owning module — the same string as <see cref="IIpcModule.ModuleName"/>, supplied by the kit,
    /// so a route can never emit under a module it does not own.
    /// </summary>
    string Module { get; }

    /// <summary>The facade's logger (never null — <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger"/> when unconfigured).</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Emit an event on the host bus under <see cref="Module"/>. Fire-and-forget — a subscriber cannot
    /// fault the caller.
    /// </summary>
    void Publish(string type, object? payload = null, string? scope = null);

    /// <summary>
    /// The id of the request being handled — <see cref="IpcRequest.Id"/>, the one identity it has
    /// anywhere: in the response, in every progress snapshot, and in a cancel targeting it.
    /// </summary>
    string RequestId { get; }

    /// <summary>
    /// Report progress on THIS request, keyed by its id automatically.
    /// <para>
    /// ⚠ <b>Usually nothing to see.</b> A request that finishes inside
    /// <see cref="IpcRequestTrackerOptions.GracePeriod"/> emits NOTHING — not this, not a running
    /// snapshot, not a completion. The values are still KEPT, so the first snapshot of a request that
    /// does outlive the window carries the latest of them.
    /// </para>
    /// </summary>
    /// <param name="progress">How far along, in the app's own unit. Null leaves the last value.</param>
    /// <param name="detail">Optional human-facing label, i18n-ready. Null leaves the last one.</param>
    void Report(IpcProgress? progress = null, IpcLabel? detail = null);
}
