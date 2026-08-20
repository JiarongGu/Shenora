namespace Shenora.Core.Ipc;

/// <summary>
/// The tracking scope of the request currently being dispatched on this logical call, so a module can
/// reach it WITHOUT being handed it. The dispatcher sets it around the pipeline
/// (<see cref="MessageDispatcher.DispatchAsync"/>), so even a third-party module written to the plain
/// <see cref="IIpcModule"/> contract gets progress reporting. A route reaches it through
/// <see cref="IModuleContext"/>, which captures it ONCE at construction.
/// <para>
/// ⚠ <b>Nothing begins a scope for a request raised by calling another module's
/// <see cref="IIpcModule.HandleMessageAsync"/> DIRECTLY rather than through the dispatcher</b>, so the
/// OUTER request's scope is still ambient on that call. Read through <see cref="For"/>.
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
    /// The current scope only if it belongs to <paramref name="requestId"/>, else null. ⚠ Without the id
    /// match, a call outside the dispatch path picks up whatever request is in flight on that call and
    /// reports progress against it.
    /// </summary>
    internal static IIpcRequestScope? For(string requestId) =>
        Current is { } scope && string.Equals(scope.RequestId, requestId, StringComparison.Ordinal)
            ? scope
            : null;
}
