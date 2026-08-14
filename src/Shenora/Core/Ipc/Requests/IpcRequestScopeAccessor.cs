namespace Shenora.Core.Ipc;

/// <summary>
/// The tracking scope of the request currently being dispatched on this logical call, so a module can
/// reach it WITHOUT being handed it.
///
/// <para>
/// 🔴 <b>This exists because the explicit version silently did nothing for a whole release.</b> Tracking
/// used to start in <see cref="ModuleBase"/>, from an <see cref="IIpcRequestTracker"/> the FACADE had to
/// inject and forward through <c>base(logger, events, requests)</c>. Not one facade in the kit did —
/// <c>FileDialogModule</c>, <c>MediaPlayerModule</c>, <c>WindowCommandModule</c>, <c>DropZoneModule</c> and
/// <c>IpcRequestsModule</c> all passed <c>base(logger)</c> — so <c>Begin</c> was never called anywhere,
/// <c>LIST</c> always answered empty and <c>CANCEL</c> always answered false. Nothing threw, nothing
/// logged, and every test passed, because the tests drove the tracker directly. D63's class exactly:
/// ABSENT is indistinguishable from working.
/// </para>
/// <para>
/// A wiring obligation nobody can forget beats one everybody did. The dispatcher sets this around the
/// pipeline — see <see cref="MessageDispatcher.DispatchAsync"/> — and a module reads it, so a third-party
/// module written to the plain <see cref="IIpcModule"/> contract gets progress reporting for free.
/// </para>
/// <para>
/// <b>What the async machinery already guarantees, measured rather than assumed:</b> an
/// <see cref="AsyncLocal{T}"/> written inside an async method does NOT escape to its caller.
/// <c>AsyncTaskMethodBuilder.Start</c> saves the ExecutionContext and restores it when the state machine
/// first returns — whether it ran to completion or suspended — so the scope set around the pipeline is
/// invisible above it either way. The explicit restore in <c>MessageDispatcher.RunTracked</c> is kept for
/// the nesting case and to state the intent, but it is belt-and-braces, not the thing that makes this
/// safe. (Believed the opposite while writing this, and the sabotage run said otherwise.)
/// </para>
/// <para>
/// ⚠ <b>What is NOT guaranteed, and is exactly what <see cref="For"/> exists for:</b> a route calling
/// another module's <see cref="IIpcModule.HandleMessageAsync"/> DIRECTLY, rather than through the
/// dispatcher. Nothing begins a scope for that inner request, and the OUTER request's scope is genuinely
/// ambient on that call — so an unguarded read attributes the inner module's progress to the outer
/// request. Pinned by
/// <c>IpcRequestDispatchTests.A_module_invoked_directly_from_inside_a_route_does_not_report_against_the_outer_request</c>.
/// </para>
/// <para>
/// INTERNAL on purpose. The sanctioned way for a route to reach its request is
/// <see cref="IModuleContext"/>, which captures the scope ONCE at construction — so work a route hands
/// off to the background keeps reporting against the right request instead of reading an ambient that has
/// long since moved on. Making this public would ship the footgun the context exists to remove.
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
    /// The current scope only if it belongs to <paramref name="requestId"/>.
    /// <para>
    /// The guard is not paranoia: <see cref="IIpcModule.HandleMessageAsync"/> is public and callable
    /// directly — every facade unit test does exactly that — so a module invoked OUTSIDE the dispatch
    /// path could otherwise pick up whatever request happened to be in flight on that call and report
    /// progress against it. Matching the id makes the wrong-request case impossible rather than unlikely.
    /// </para>
    /// </summary>
    internal static IIpcRequestScope? For(string requestId) =>
        Current is { } scope && string.Equals(scope.RequestId, requestId, StringComparison.Ordinal)
            ? scope
            : null;
}
