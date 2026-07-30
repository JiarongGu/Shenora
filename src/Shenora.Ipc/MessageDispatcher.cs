using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.Ipc;

/// <summary>
/// A middleware in the dispatch pipeline: handle the request and return a response, or return
/// null (typically by awaiting <paramref name="next"/>) to let the rest of the pipeline try.
/// </summary>
/// <param name="request">The request travelling the pipeline.</param>
/// <param name="next">Invokes the remaining pipeline; returns null when nothing handled it.</param>
public delegate Task<IpcResponse?> MessageMiddleware(IpcRequest request, Func<Task<IpcResponse?>> next);

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
public sealed class MessageDispatcher : IMessageDispatcher
{
    private readonly ILogger<MessageDispatcher> _logger;

    // The middleware list and the built pipeline are mutated by Use() and read by every dispatch,
    // concurrently and by design (P5.5 H6). See Use() and Pipeline for why this shape.
    private readonly object _pipelineLock = new();
    private volatile MessageMiddleware[] _middlewares = [];
    private volatile Func<IpcRequest, Task<IpcResponse?>>? _pipeline;

    /// <summary>The logger is optional so composition works without <c>AddLogging</c>.</summary>
    public MessageDispatcher(ILogger<MessageDispatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<MessageDispatcher>.Instance;
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
    private Func<IpcRequest, Task<IpcResponse?>> Pipeline
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
    /// </summary>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await Pipeline(request);
            if (response is not null)
                return response;

            _logger.LogWarning("No handler for {Module}/{Type}", request.Module, request.Type);
            return IpcResponse.CreateError(request.Id, IpcErrorCodes.NoHandler, parameters:
                new Dictionary<string, string> { ["module"] = request.Module, ["type"] = request.Type });
        }
        catch (Exception ex)
        {
            // One owner for the error boundary (P5.5 H4.5) — see IpcErrorMapping for why four copies
            // of this rule was a leak waiting to happen.
            return IpcErrorMapping.ToErrorResponse(request, ex, _logger, "dispatching");
        }
    }

    /// <inheritdoc />
    public async Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null)
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
        return await DispatchAsync(request);
    }

    /// <summary>
    /// Send a programmatic request and get typed response data. DEVIATION from the source
    /// (which threw <c>InvalidOperationException</c> with a flattened message): a failed
    /// response is rethrown as its structured <see cref="OperationException"/>, so a
    /// programmatic caller sees exactly the failure a client would.
    /// </summary>
    public async Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null)
    {
        var response = await SendAsync(module, type, scope, payload);

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

    /// <summary>
    /// Give every request for <paramref name="module"/> (case-insensitive) to
    /// <paramref name="handler"/>; a null result falls through to the rest of the pipeline.
    /// </summary>
    public MessageDispatcher UseModule(string module, Func<IpcRequest, Task<IpcResponse?>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentNullException.ThrowIfNull(handler);
        return Use(async (request, next) =>
        {
            if (string.Equals(request.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                var response = await handler(request);
                if (response is not null)
                    return response;
            }
            return await next();
        });
    }

    /// <summary>Give requests matching module + type (both case-insensitive) to <paramref name="handler"/>.</summary>
    public MessageDispatcher UseRoute(string module, string type, Func<IpcRequest, Task<IpcResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(handler);
        return Use(async (request, next) =>
        {
            if (string.Equals(request.Module, module, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                return await handler(request);
            }
            return await next();
        });
    }

    /// <summary>Middleware that logs every request and its outcome.</summary>
    public MessageDispatcher UseLogging()
    {
        return Use(async (request, next) =>
        {
            _logger.LogDebug("Processing {Module}/{Type}", request.Module, request.Type);
            var response = await next();
            if (response is { Success: true })
                _logger.LogDebug("Success {Module}/{Type}", request.Module, request.Type);
            else if (response is { Success: false })
                _logger.LogWarning("Failed {Module}/{Type}: [{Code}]", request.Module, request.Type,
                    response.Error?.Code);
            return response;
        });
    }

    /// <summary>
    /// Middleware that converts downstream exceptions into structured error responses — register
    /// it FIRST so it wraps everything after it. <see cref="OperationException"/> crosses as its
    /// structured error; anything else is logged host-side and crosses only as
    /// <see cref="IpcErrorCodes.UnknownError"/> plus the exception type name (the source leaked
    /// <c>ex.Message</c> across the bridge here). <see cref="DispatchAsync"/> keeps a last-resort
    /// copy of this mapping for pipelines composed without it.
    /// </summary>
    public MessageDispatcher UseErrorHandler()
    {
        return Use(async (request, next) =>
        {
            try
            {
                return await next();
            }
            catch (Exception ex)
            {
                return IpcErrorMapping.ToErrorResponse(request, ex, _logger, "in");
            }
        });
    }

    /// <summary>Map one route to a simple handler; the result is wrapped in a success response.</summary>
    public MessageDispatcher MapRoute(string module, string type, Func<IpcRequest, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return UseRoute(module, type,
            request => Task.FromResult(IpcResponse.CreateSuccess(request.Id, handler(request))));
    }

    /// <summary>Map a route table for one module (see <see cref="ModuleRouteBuilder"/>).</summary>
    public MessageDispatcher MapModule(string module, Action<ModuleRouteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new ModuleRouteBuilder(this, module));
        return this;
    }

    /// <summary>
    /// Route a whole module to a facade. This replaces the source app's static mutable service
    /// registry: facades live in DI (registered as <see cref="IModuleFacade"/>) and are mapped
    /// here at composition time.
    /// </summary>
    public MessageDispatcher MapModule(IModuleFacade facade)
    {
        ArgumentNullException.ThrowIfNull(facade);
        return UseModule(facade.ModuleName,
            async request => await facade.HandleMessageAsync(request));
    }

    /// <summary>Compose an IMMUTABLE snapshot — never the live field, or this races <see cref="Use"/>.</summary>
    private static Func<IpcRequest, Task<IpcResponse?>> BuildPipeline(MessageMiddleware[] middlewares)
    {
        // Terminal: nothing handled the request.
        Func<IpcRequest, Task<IpcResponse?>> pipeline = _ => Task.FromResult<IpcResponse?>(null);

        // Compose in reverse so middlewares run in registration order.
        for (var i = middlewares.Length - 1; i >= 0; i--)
        {
            var middleware = middlewares[i];
            var next = pipeline;
            pipeline = request => middleware(request, () => next(request));
        }

        return pipeline;
    }
}

/// <summary>
/// Route table for one module, used by <see cref="MessageDispatcher.MapModule(string, Action{ModuleRouteBuilder})"/>.
/// </summary>
public sealed class ModuleRouteBuilder
{
    private readonly MessageDispatcher _dispatcher;
    private readonly string _module;

    internal ModuleRouteBuilder(MessageDispatcher dispatcher, string module)
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

    /// <summary>Map an async route; the result is wrapped in a success response.</summary>
    public ModuleRouteBuilder RouteAsync(string type, Func<IpcRequest, Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _dispatcher.UseRoute(_module, type, async request =>
            IpcResponse.CreateSuccess(request.Id, await handler(request)));
        return this;
    }
}
