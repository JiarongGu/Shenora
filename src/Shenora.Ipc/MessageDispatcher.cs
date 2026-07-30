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

    // The middleware list and the built pipeline are mutated by Use() and read by every dispatch,
    // concurrently and by design (P5.5 H6). See Use() and Pipeline for why this shape.
    private readonly object _pipelineLock = new();
    private volatile MessageMiddleware[] _middlewares = [];
    private volatile Func<IpcRequest, CancellationToken, Task<IpcResponse?>>? _pipeline;
    // Claimed module names. Case-insensitive because routing is. Guarded by _pipelineLock rather
    // than a concurrent set: LATE MAPPING is supported, so this is written while requests are in
    // flight, and it must move in step with the pipeline it describes.
    private readonly HashSet<string> _mappedModules = new(StringComparer.OrdinalIgnoreCase);

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
    /// </summary>
    public async Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            // Thrown INSIDE the try on purpose: the catch below maps it, so an already-cancelled
            // token produces the same structured OPERATION_CANCELLED that a handler's own
            // cancellation does. The boundary still never throws to its caller, and the transport
            // has one code to render rather than two shapes for one outcome.
            cancellationToken.ThrowIfCancellationRequested();
            var response = await Pipeline(request, cancellationToken);
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
        get { lock (_pipelineLock) { return _mappedModules.ToArray(); } }
    }

    /// <inheritdoc />
    public bool IsModuleMapped(string module)
    {
        if (string.IsNullOrEmpty(module)) return false;
        lock (_pipelineLock) { return _mappedModules.Contains(module); }
    }

    /// <inheritdoc />
    public void TrackMappedModule(string module)
    {
        ArgumentException.ThrowIfNullOrEmpty(module);
        lock (_pipelineLock) { _mappedModules.Add(module); }
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
