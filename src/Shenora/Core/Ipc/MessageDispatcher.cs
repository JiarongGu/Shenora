using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Core.Ipc;

/// <summary>
/// A middleware in the dispatch pipeline: handle the request and return a response, or return
/// null (typically by awaiting <paramref name="next"/>) to let the rest of the pipeline try.
/// </summary>
/// <param name="request">The request travelling the pipeline.</param>
/// <param name="next">Invokes the remaining pipeline; returns null when nothing handled it.</param>
/// <param name="cancellationToken">
/// The caller's lifetime — see <see cref="IMessageDispatcher.DispatchAsync"/>. Middleware that
/// awaits anything should observe it and pass it on; middleware that only inspects and forwards can
/// ignore it, since <paramref name="next"/> already carries it.
/// </param>
public delegate Task<IpcResponse?> MessageMiddleware(IpcRequest request, Func<Task<IpcResponse?>> next,
                                                     CancellationToken cancellationToken);

/// <summary>
/// The middleware-pipeline dispatcher routing IPC requests to module handlers/facades, ported
/// from the primary desktop sibling. Compose at startup in the family's proven order — error
/// handler → logging → app middleware → module facades (design contract §5) — then feed client
/// requests in through <see cref="DispatchAsync"/> (transports) or <see cref="SendAsync"/>
/// (services/plugins). Registration is meant for composition time and is not synchronized;
/// dispatch is thread-safe and the pipeline is (re)built lazily on first use after a change.
///
/// The pipeline deliberately does NOT use <c>ConfigureAwait(false)</c>: it preserves the
/// caller's synchronization context end-to-end, so when the transport dispatches on the UI
/// thread every handler's synchronous segment runs there too (design §5's async-interleaving
/// threading model) — including handlers reached AFTER an asynchronous fall-through.
/// </summary>
public sealed class MessageDispatcher : IMessageDispatcher, IModuleRegistry
{
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly IIpcRequestTracker? _requests;

    // The middleware list and the built pipeline are mutated by Use() and read by every dispatch,
    // concurrently and by design (P5.5 H6). See Use() and Pipeline for why this shape.
    private readonly object _pipelineLock = new();
    private volatile MessageMiddleware[] _middlewares = [];
    private volatile Func<IpcRequest, CancellationToken, Task<IpcResponse?>>? _pipeline;
    // Claimed modules → the middleware installed for each. Case-insensitive because routing is.
    // Guarded by _pipelineLock rather than a concurrent dictionary: LATE MAPPING is supported, so
    // this is written while requests are in flight, and it must move in step with the pipeline it
    // describes. It maps to the MIDDLEWARE, not just the name, because that reference is the only
    // thing that makes release possible — see IModuleRegistry.
    private readonly Dictionary<string, MessageMiddleware> _mappedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The logger is optional so composition works without <c>AddLogging</c>.</summary>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="requests">
    /// Request tracking. Optional, because composing IPC over a bare <c>ServiceCollection</c> with no
    /// <c>IEventBus</c> behind it is a legitimate shape — but <c>AddMessageDispatcher</c> supplies it,
    /// so every composed app has it. Null means requests are dispatched untracked: no
    /// <see cref="IpcRequestEvents.Updated"/>, no <c>LIST</c> entries, no cancellable token.
    /// </param>
    public MessageDispatcher(ILogger<MessageDispatcher>? logger = null, IIpcRequestTracker? requests = null)
    {
        _logger = logger ?? NullLogger<MessageDispatcher>.Instance;
        _requests = requests;
    }

    /// <summary>
    /// The composed pipeline, rebuilt on first use after any <see cref="Use"/>.
    /// <para>
    /// This used to be a <c>Lazy</c> field reassigned by <see cref="Use"/>, over a mutable
    /// <c>List&lt;MessageMiddleware&gt;</c>, with no synchronization anywhere — and late mapping is a
    /// SUPPORTED, documented pattern here (the WinForms host maps its window facades after the form
    /// exists), so "configure then serve" is not a safe assumption. Two things went wrong under
    /// concurrency: a dispatch could read the OLD <c>Lazy</c> — already built, already cached — and
    /// answer <see cref="IpcErrorCodes.NoHandler"/> for a route that was by then registered; and a
    /// build enumerating the list while <c>Add</c> grew it is a plain data race. Now the list is
    /// copy-on-write so a build always sees an immutable snapshot, the field is volatile so a reader
    /// cannot see a stale pipeline after the swap, and invalidate-then-rebuild happens under one lock.
    /// </para>
    /// </summary>
    private Func<IpcRequest, CancellationToken, Task<IpcResponse?>> Pipeline
    {
        get
        {
            // Fast path: no lock once built, which is the overwhelmingly common case.
            if (_pipeline is { } built) return built;
            lock (_pipelineLock)
            {
                return _pipeline ??= BuildPipeline(_middlewares);
            }
        }
    }

    /// <summary>
    /// Run a request through the pipeline. Never throws and never returns null: an unhandled
    /// request becomes a structured <see cref="IpcErrorCodes.NoHandler"/> error, an escaped
    /// <see cref="OperationException"/> becomes its structured error, and any other exception
    /// becomes <see cref="IpcErrorCodes.UnknownError"/> with the details kept host-side — raw
    /// exceptions never cross the bridge (design contract §5; the source leaked
    /// <c>ex.Message</c> here).
    /// <para>
    /// 🔴 <b>This is also where a request is TRACKED, and the dispatch boundary is the only honest
    /// place for it.</b> Two reasons, and the second is the one that was got wrong: every request
    /// passes here regardless of how its module was written (<see cref="ModuleBase"/>, a bare
    /// <see cref="IIpcModule"/>, an ad-hoc <c>MapRoute</c> lambda), and the OUTCOME is known here and
    /// nowhere else — one <see cref="IpcResponse"/> carries success, an app's structured failure, a
    /// cancellation and <see cref="IpcErrorCodes.NoHandler"/> alike. Tracking used to start inside
    /// <see cref="ModuleBase"/>, which could see neither: it covered only its own subclasses, and its
    /// <c>catch</c> returned an error response while the scope disposed as
    /// <see cref="IpcRequestState.Completed"/> — so <see cref="IpcRequestState.Failed"/> was
    /// unreachable in practice and a failed request reported success to the page.
    /// </para>
    /// </summary>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IIpcRequestScope? scope = null;
        try
        {
            // Thrown INSIDE the try on purpose: the catch below maps it, so an already-cancelled
            // token produces the same structured OPERATION_CANCELLED that a handler's own
            // cancellation does. The boundary still never throws to its caller, and the transport
            // has one code to render rather than two shapes for one outcome.
            // Before Begin, so a request that was dead on arrival is never tracked at all.
            cancellationToken.ThrowIfCancellationRequested();

            // Publishes NOTHING yet: the page hears about this request only if it outlives the grace
            // period (D66), so the fast path — nearly every request — never reaches the wire at all.
            scope = BeginTracking(request, cancellationToken);

            // The scope's token is what CANCEL targets, so it is the one the whole pipeline must
            // observe; the caller's own lifetime is LINKED into it, not replaced by it.
            var response = await RunTracked(request, scope, cancellationToken);

            if (response is null)
            {
                _logger.LogWarning("No handler for {Module}/{Type}", request.Module, request.Type);
                response = IpcResponse.CreateError(request.Id, IpcErrorCodes.NoHandler, parameters:
                    new Dictionary<string, string> { ["module"] = request.Module, ["type"] = request.Type });
            }

            // A structured failure the pipeline RETURNED rather than threw — an app's own
            // OperationException already mapped by UseErrorHandler, or the NO_HANDLER above. Recorded
            // with the same error the client gets, so the in-flight list and the response never
            // disagree about why something failed.
            if (!response.Success && response.Error is { } failure) FailTracking(scope, failure);

            return response;
        }
        catch (Exception ex)
        {
            // One owner for the error boundary (P5.5 H4.5) — see IpcErrorMapping for why four copies
            // of this rule was a leak waiting to happen.
            var response = IpcErrorMapping.ToErrorResponse(request, ex, _logger, "dispatching");
            if (response.Error is { } failure) FailTracking(scope, failure);
            return response;
        }
        finally
        {
            // Completes the request unless something already finished it — Fail above, or a CANCEL
            // that transitioned the entry directly. Both the scope and the tracker are idempotent
            // about that, which is what makes an unconditional end here correct.
            //
            // ⚠ The response is fully BUILT before this runs, which is the ordering D66 insists on:
            // the grace period suppresses notifications and must never delay the answer.
            EndTracking(scope);
        }
    }

    /// <summary>
    /// 🔴 <b>Tracking is BOOKKEEPING and must never decide a request's fate.</b>
    /// <see cref="IIpcRequestTracker"/> is a PUBLIC seam — an app may supply its own — and these three
    /// calls sit on the one boundary the whole kit promises never throws, which every transport relies on
    /// (<c>WebViewIpcBridge</c> would take an unhandled exception on the UI thread). So a faulty tracker
    /// costs its own bookkeeping and nothing else: the request still runs, still answers, and the failure
    /// is logged host-side.
    /// <para>
    /// Written as three small methods rather than one guarded delegate on purpose — this is the IPC hot
    /// path and a closure per call would allocate for every request in the app.
    /// </para>
    /// <para>
    /// ⚠ <b><see cref="IModuleContext.Report"/> is deliberately NOT guarded this way.</b> It runs inside
    /// the module's own error boundary, so a throwing tracker there degrades to one failed request rather
    /// than a broken transport — and swallowing it would hide a genuinely broken tracker from the app that
    /// supplied it. Guard the BOUNDARY, not every call.
    /// </para>
    /// </summary>
    private IIpcRequestScope? BeginTracking(IpcRequest request, CancellationToken cancellationToken)
    {
        if (_requests is null) return null;
        try
        {
            return _requests.Begin(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // Untracked rather than failed: losing the in-flight entry is a diagnostic loss, and failing
            // the request would turn it into an outage.
            _logger.LogError(ex, "Request tracking failed to begin for {Module}/{Type}",
                             request.Module, request.Type);
            return null;
        }
    }

    /// <inheritdoc cref="BeginTracking"/>
    private void FailTracking(IIpcRequestScope? scope, IpcError error)
    {
        if (scope is null) return;
        try { scope.Fail(error); }
        catch (Exception ex) { _logger.LogError(ex, "Request tracking failed to record a failure"); }
    }

    /// <inheritdoc cref="BeginTracking"/>
    private void EndTracking(IIpcRequestScope? scope)
    {
        if (scope is null) return;
        try { scope.Dispose(); }
        catch (Exception ex) { _logger.LogError(ex, "Request tracking failed to end a request"); }
    }

    /// <summary>
    /// Run the pipeline with <paramref name="scope"/> as the ambient request scope, so a module can
    /// report progress without having been handed anything (see <see cref="IpcRequestScopeAccessor"/>).
    /// <para>
    /// The <c>finally</c> restore is deliberate but NOT what makes this safe — an async method's builder
    /// already restores the caller's ExecutionContext, so the write cannot escape upward. See
    /// <see cref="IpcRequestScopeAccessor"/> for what genuinely needs guarding (a route calling another
    /// module directly) and how that was measured rather than reasoned about.
    /// </para>
    /// </summary>
    private async Task<IpcResponse?> RunTracked(IpcRequest request, IIpcRequestScope? scope,
                                                CancellationToken cancellationToken)
    {
        // No tracker composed: the caller's own token is still the pipeline's, exactly as before. Passing
        // CancellationToken.None here would silently disable cancellation for every untracked host.
        if (scope is null) return await Pipeline(request, cancellationToken);

        var previous = IpcRequestScopeAccessor.Current;
        IpcRequestScopeAccessor.Current = scope;
        try
        {
            return await Pipeline(request, scope.CancellationToken);
        }
        finally
        {
            IpcRequestScopeAccessor.Current = previous;
        }
    }

    /// <inheritdoc />
    public async Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentException.ThrowIfNullOrEmpty(type);

        var request = new IpcRequest
        {
            Module = module,
            Type = type,
            Scope = scope,
            Payload = payload is null ? null : IpcJson.SerializeToElement(payload),
        };

        _logger.LogTrace("Programmatic send: {Module}/{Type} (scope: {Scope})", module, type, scope ?? "none");
        return await DispatchAsync(request, cancellationToken);
    }

    /// <summary>
    /// Send a programmatic request and get typed response data. DEVIATION from the source
    /// (which threw <c>InvalidOperationException</c> with a flattened message): a failed
    /// response is rethrown as its structured <see cref="OperationException"/>, so a
    /// programmatic caller sees exactly the failure a client would.
    /// </summary>
    public async Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                                       CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(module, type, scope, payload, cancellationToken);

        if (!response.Success)
        {
            var error = response.Error;
            throw new OperationException(error?.Code ?? IpcErrorCodes.UnknownError, error?.Parameters, error?.Message);
        }

        return ConvertData<T>(response.Data);
    }

    /// <summary>
    /// Response data can be the requested type already (in-process handler), a
    /// <see cref="JsonElement"/>, or any other live object (converted via a JSON round-trip).
    /// Conversions use the wire options — the source used serializer defaults here, which would
    /// have broken camelCase JSON mapping back into PascalCase members.
    /// </summary>
    private static T? ConvertData<T>(object? data)
    {
        if (data is null)
            return default;
        if (data is T typed)
            return typed;
        try
        {
            if (data is JsonElement element)
                return element.Deserialize<T>(IpcJson.Options);
            return IpcJson.Deserialize<T>(IpcJson.Serialize(data));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to convert response data to {typeof(T).Name}.", ex);
        }
    }

    /// <summary>
    /// Append a middleware. The pipeline rebuilds lazily before the next dispatch, and is safe to call
    /// while dispatches are in flight — late mapping is a supported pattern (see <see cref="Pipeline"/>).
    /// </summary>
    /// <remarks>
    /// Declared twice on purpose: the interface member returns <see cref="IMessageDispatcher"/> so every
    /// helper in <see cref="MessageDispatcherExtensions"/> composes on the interface, while this one
    /// returns the concrete type so existing fluent chains off <c>new MessageDispatcher()</c> keep their
    /// precise type. C# does not allow a covariant return when implementing an interface, hence the
    /// explicit implementation below rather than one method serving both.
    /// </remarks>
    IMessageDispatcher IMessageDispatcher.Use(MessageMiddleware middleware) => Use(middleware);

    /// <inheritdoc cref="IMessageDispatcher.Use"/>
    public MessageDispatcher Use(MessageMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        lock (_pipelineLock)
        {
            // Copy-on-write, so a build already enumerating the previous array is unaffected; and both
            // writes happen under the lock, so the next reader cannot see the new middleware with the
            // old pipeline still cached.
            _middlewares = [.. _middlewares, middleware];
            _pipeline = null;
        }
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> MappedModules
    {
        // A snapshot under the lock: late mapping means this can be read while another thread maps.
        get { lock (_pipelineLock) { return _mappedModules.Keys.ToArray(); } }
    }

    /// <inheritdoc />
    public bool IsModuleMapped(string module)
    {
        if (string.IsNullOrEmpty(module)) return false;
        lock (_pipelineLock) { return _mappedModules.ContainsKey(module); }
    }

    /// <inheritdoc />
    public bool TryClaimModule(IIpcModule facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentException.ThrowIfNullOrEmpty(facade.ModuleName);

        // Built OUTSIDE the lock, but the check-and-install must be atomic: two threads claiming the
        // same name concurrently is exactly the plug-in case this seam exists for.
        var middleware = MessageDispatcherExtensions.ModuleMiddleware(facade.ModuleName,
            async (request, ct) => await facade.HandleMessageAsync(request, ct));

        lock (_pipelineLock)
        {
            if (_mappedModules.ContainsKey(facade.ModuleName)) return false;
            _mappedModules[facade.ModuleName] = middleware;
            _middlewares = [.. _middlewares, middleware];
            _pipeline = null;
        }
        return true;
    }

    /// <inheritdoc />
    public bool TryReleaseModule(string module)
    {
        if (string.IsNullOrEmpty(module)) return false;
        lock (_pipelineLock)
        {
            if (!_mappedModules.Remove(module, out var middleware)) return false;

            // Rebuild WITHOUT that one entry, preserving the order of everything else exactly. This
            // is why release is a registry operation and not a generic "remove a middleware": the
            // relative order of the error handler, logging, app middleware and the scoped router is
            // load-bearing (design §5), and only the module's own entry may move.
            //
            // A dispatch already in flight holds a pipeline SNAPSHOT and completes against the old
            // chain — the same contract late mapping has always had.
            _middlewares = [.. _middlewares.Where(m => !ReferenceEquals(m, middleware))];
            _pipeline = null;
        }
        return true;
    }

    /// <summary>
    /// The default logger for <see cref="MessageDispatcherExtensions.UseLogging"/> and
    /// <see cref="MessageDispatcherExtensions.UseErrorHandler"/> when the caller passes none — the
    /// dispatcher's own, so a pipeline composed without an explicit logger still reports through the
    /// same sink it always did.
    /// </summary>
    internal ILogger Logger => _logger;

    /// <summary>Compose an IMMUTABLE snapshot — never the live field, or this races <see cref="Use"/>.</summary>
    private static Func<IpcRequest, CancellationToken, Task<IpcResponse?>> BuildPipeline(MessageMiddleware[] middlewares)
    {
        // Terminal: nothing handled the request.
        Func<IpcRequest, CancellationToken, Task<IpcResponse?>> pipeline =
            (_, _) => Task.FromResult<IpcResponse?>(null);

        // Compose in reverse so middlewares run in registration order.
        for (var i = middlewares.Length - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var next = pipeline;
            pipeline = (request, ct) => middleware(request, () => next(request, ct), ct);
        }

        return pipeline;
    }
}

/// <summary>
/// Route table for one module, used by
/// <see cref="MessageDispatcherExtensions.MapModule(IMessageDispatcher, string, Action{ModuleRouteBuilder})"/>.
/// </summary>
public sealed class ModuleRouteBuilder
{
    private readonly IMessageDispatcher _dispatcher;
    private readonly string _module;

    internal ModuleRouteBuilder(IMessageDispatcher dispatcher, string module)
    {
        _dispatcher = dispatcher;
        _module = module;
    }

    /// <summary>Map a synchronous route; the result is wrapped in a success response.</summary>
    public ModuleRouteBuilder Route(string type, Func<IpcRequest, object?> handler)
    {
        _dispatcher.MapRoute(_module, type, handler);
        return this;
    }

    /// <summary>
    /// Map an async route; the result is wrapped in a success response. The handler receives the
    /// caller's lifetime token (see <see cref="IMessageDispatcher.DispatchAsync"/>) — ignore it for
    /// quick work, observe it for anything that awaits.
    /// </summary>
    public ModuleRouteBuilder RouteAsync(string type, Func<IpcRequest, CancellationToken, Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _dispatcher.UseRoute(_module, type, async (request, ct) =>
            IpcResponse.CreateSuccess(request.Id, await handler(request, ct)));
        return this;
    }
}
