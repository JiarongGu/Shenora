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
/// <param name="cancellationToken">The caller's lifetime — see <see cref="IMessageDispatcher.DispatchAsync"/>.</param>
public delegate Task<IpcResponse?> MessageMiddleware(IpcRequest request, Func<Task<IpcResponse?>> next,
                                                     CancellationToken cancellationToken);

/// <summary>
/// The middleware-pipeline dispatcher routing IPC requests to module handlers/facades. Compose at
/// startup in the order error handler → logging → app middleware → module facades
/// (<c>docs/design/ipc.md</c>), then feed requests in through <see cref="DispatchAsync"/> (transports)
/// or <see cref="SendAsync"/> (services/plugins). Dispatch is thread-safe and the pipeline is (re)built
/// lazily on first use after a change.
/// <para>
/// No <c>ConfigureAwait(false)</c> anywhere: the pipeline preserves the caller's synchronization
/// context, so a handler reached on the UI thread — even after an async fall-through — resumes there.
/// </para>
/// </summary>
public sealed class MessageDispatcher : IMessageDispatcher, IModuleRegistry
{
    private readonly ILogger<MessageDispatcher> _logger;
    private readonly IIpcRequestTracker? _requests;

    // Mutated by Use() and read by every dispatch, concurrently and by design — see Pipeline.
    private readonly object _pipelineLock = new();
    private volatile MessageMiddleware[] _middlewares = [];
    private volatile Func<IpcRequest, CancellationToken, Task<IpcResponse?>>? _pipeline;
    // Claimed modules → the middleware installed for each; case-insensitive because routing is. Under
    // _pipelineLock so it moves in step with the pipeline it describes while requests are in flight.
    private readonly Dictionary<string, MessageMiddleware> _mappedModules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The logger is optional so composition works without <c>AddLogging</c>.</summary>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="requests">
    /// Request tracking (<c>UseMessageDispatcher</c> supplies it). Null dispatches untracked: no
    /// <see cref="IpcRequestEvents.Updated"/>, no <c>LIST</c> entries, no cancellable token.
    /// </param>
    public MessageDispatcher(ILogger<MessageDispatcher>? logger = null, IIpcRequestTracker? requests = null)
    {
        _logger = logger ?? NullLogger<MessageDispatcher>.Instance;
        _requests = requests;
    }

    /// <summary>
    /// The composed pipeline, rebuilt on first use after any <see cref="Use"/>.
    /// 🔴 <b>Late mapping is a SUPPORTED pattern</b> — the WinForms host maps its window facades after the
    /// form exists — so this must be thread-safe: copy-on-write middleware list, volatile field,
    /// invalidate-then-rebuild under one lock. Without those, a dispatch reads an already-cached pipeline
    /// and answers <see cref="IpcErrorCodes.NoHandler"/> for a route that is by then registered.
    /// </summary>
    private Func<IpcRequest, CancellationToken, Task<IpcResponse?>> Pipeline
    {
        get
        {
            if (_pipeline is { } built) return built;
            lock (_pipelineLock)
            {
                return _pipeline ??= BuildPipeline(_middlewares);
            }
        }
    }

    /// <summary>
    /// Run a request through the pipeline. 🔴 <b>Never throws and never returns null:</b> unhandled →
    /// <see cref="IpcErrorCodes.NoHandler"/>, an escaped <see cref="ShenoraException"/> → its structured
    /// error, anything else → <see cref="IpcErrorCodes.UnknownError"/> with the details kept host-side.
    /// <para>
    /// 🔴 <b>This is also where a request is TRACKED, and the dispatch boundary is the only place that
    /// can be</b> — the only one that sees every module and every outcome (D63).
    /// </para>
    /// </summary>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IIpcRequestScope? scope = null;
        try
        {
            // INSIDE the try, so the catch maps it to OPERATION_CANCELLED like any other cancel. Before
            // Begin, so a dead-on-arrival request stays untracked.
            cancellationToken.ThrowIfCancellationRequested();

            scope = BeginTracking(request, cancellationToken);

            // The scope's token is what CANCEL targets — the caller's lifetime is LINKED into it.
            var response = await RunTracked(request, scope, cancellationToken);

            if (response is null)
            {
                _logger.LogWarning("No handler for {Module}/{Type}", request.Module, request.Type);
                response = IpcResponse.CreateError(request.Id, IpcErrorCodes.NoHandler, parameters:
                    new Dictionary<string, string> { ["module"] = request.Module, ["type"] = request.Type });
            }

            // A structured failure the pipeline RETURNED rather than threw, recorded with the same error
            // the client gets so the in-flight list and the response never disagree.
            if (!response.Success && response.Error is { } failure) FailTracking(scope, failure);

            return response;
        }
        catch (Exception ex)
        {
            var response = IpcErrorMapping.ToErrorResponse(request, ex, _logger, "dispatching");
            if (response.Error is { } failure) FailTracking(scope, failure);
            return response;
        }
        finally
        {
            // Completes the request unless Fail above or a CANCEL already finished it (both idempotent).
            // ⚠ The response is fully BUILT before this runs (D66): tracking never delays the answer.
            EndTracking(scope);
        }
    }

    /// <summary>
    /// 🔴 <b>Tracking is BOOKKEEPING and must never decide a request's fate.</b>
    /// <see cref="IIpcRequestTracker"/> is a PUBLIC seam an app may implement, and these three calls sit
    /// on the one boundary the kit promises never throws — so a faulty tracker costs its own bookkeeping
    /// and nothing else: the request still runs, still answers, and the failure is logged host-side.
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
    /// report progress without having been handed anything (<see cref="IpcRequestScopeAccessor"/>).
    /// </summary>
    private async Task<IpcResponse?> RunTracked(IpcRequest request, IIpcRequestScope? scope,
                                                CancellationToken cancellationToken)
    {
        // ⚠ The caller's own token, not CancellationToken.None — which would silently disable
        // cancellation for every untracked host.
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
    /// Send a programmatic request and get typed response data. A failed response is rethrown as its
    /// structured <see cref="ShenoraException"/>.
    /// </summary>
    public async Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                                       CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(module, type, scope, payload, cancellationToken);

        if (!response.Success)
        {
            var error = response.Error;
            throw new ShenoraException(error?.Code ?? IpcErrorCodes.UnknownError, error?.Parameters, error?.Message);
        }

        return ConvertData<T>(response.Data);
    }

    /// <summary>
    /// Response data can be the requested type already (in-process handler), a <see cref="JsonElement"/>,
    /// or any other live object (converted via a JSON round-trip). ⚠ Conversions use the WIRE options —
    /// serializer defaults would break camelCase JSON mapping back into PascalCase members.
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
    /// Declared twice because C# allows no covariant return: the interface member returns
    /// <see cref="IMessageDispatcher"/>, this one the concrete type so fluent chains keep it.
    /// </remarks>
    IMessageDispatcher IMessageDispatcher.Use(MessageMiddleware middleware) => Use(middleware);

    /// <inheritdoc cref="IMessageDispatcher.Use"/>
    public MessageDispatcher Use(MessageMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        lock (_pipelineLock)
        {
            // Copy-on-write so a build already enumerating the previous array is unaffected; both writes
            // under the lock, so no reader sees the new middleware with the old pipeline.
            _middlewares = [.. _middlewares, middleware];
            _pipeline = null;
        }
        return this;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> MappedModules
    {
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

        // Built outside the lock, but check-and-install must be ATOMIC: two threads claiming the same
        // name concurrently is the plug-in case this seam exists for.
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

            // Rebuild WITHOUT that one entry, preserving every other position: the relative order of
            // error handler, logging, app middleware and scoped router is load-bearing. A dispatch in
            // flight holds a pipeline SNAPSHOT and completes against the old chain.
            _middlewares = [.. _middlewares.Where(m => !ReferenceEquals(m, middleware))];
            _pipeline = null;
        }
        return true;
    }

    /// <summary>
    /// The dispatcher's own logger — what <see cref="MessageDispatcherExtensions.UseLogging"/> and
    /// <see cref="MessageDispatcherExtensions.UseErrorHandler"/> use when the caller passes none.
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

/// <summary>Route table for one module, used by
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
    /// Map an async route; the result is wrapped in a success response. The handler receives the caller's
    /// lifetime token (<see cref="IMessageDispatcher.DispatchAsync"/>).
    /// </summary>
    public ModuleRouteBuilder RouteAsync(string type, Func<IpcRequest, CancellationToken, Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _dispatcher.UseRoute(_module, type, async (request, ct) =>
            IpcResponse.CreateSuccess(request.Id, await handler(request, ct)));
        return this;
    }
}
