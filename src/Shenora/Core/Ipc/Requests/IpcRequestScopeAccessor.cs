namespace Shenora.Core.Ipc;

/// <summary>
/// The tracking scope of the request currently being dispatched on this logical call, so a module can
/// reach it WITHOUT being handed it. The dispatcher sets it around the pipeline
/// (<see cref="MessageDispatcher.DispatchAsync"/>) and a module reads it, so even a third-party module
/// written to the plain <see cref="IIpcModule"/> contract gets progress reporting.
/// <para>
/// ⚠ <b>Nothing begins a scope for a request raised by calling another module's
/// <see cref="IIpcModule.HandleMessageAsync"/> DIRECTLY rather than through the dispatcher</b>, so the
/// OUTER request's scope is still ambient on that call. Read through <see cref="For"/>.
/// </para>
/// <para>
/// Internal: a route reaches its request through <see cref="IModuleContext"/>, which captures the scope
/// ONCE at construction, so work handed off to the background keeps reporting against the request that
/// started it rather than an ambient that has moved on.
/// </para>
/// </summary>
internal static class IpcRequestScopeAccessor
{
    private static readonly AsyncLocal<IIpcRequestScope?> Slot = new();

    /// <summary>The scope of the request being dispatched on this logical call, or null outside dispatch.</summary>
    internal static IIpcRequestScope? Current
    {
        get => Slot.Value;
        set => Slot.Value = value;
    }

    /// <summary>
    /// The current scope only if it belongs to <paramref name="requestId"/>, else null.
    /// <see cref="IIpcModule.HandleMessageAsync"/> is public and callable outside the dispatch path, where
    /// an unmatched read would pick up whatever request happened to be in flight on that call and report
    /// progress against it.
    /// </summary>
    internal static IIpcRequestScope? For(string requestId) =>
        Current is { } scope && string.Equals(scope.RequestId, requestId, StringComparison.Ordinal)
            ? scope
            : null;
}
