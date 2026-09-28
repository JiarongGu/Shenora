using Shenora.Core.Ipc;

namespace Shenora.Chromium.Host;

/// <summary>
/// A Chromium page's view of the app's dispatcher: the modules whose protocol differs by engine are addressed to the
/// engine's own module name. The page still speaks the WebView2 shell's names, and its request still goes through the
/// app's whole pipeline, middleware and tracking included.
/// <para>
/// Without it an app with both engines had ONE <c>SHENORA.DROPZONE</c>, and whichever was mapped first answered every
/// page: a Chromium page's zones reached the WebView2 module, which raises overlays on its own form, or mapping the
/// WebView2 module after the engine threw.
/// </para>
/// </summary>
/// <param name="app">The app's dispatcher.</param>
internal sealed class PageModuleDispatcher(IMessageDispatcher app) : IMessageDispatcher
{
    // Page's name → the engine's. Case-insensitive, as routing is.
    private static readonly Dictionary<string, string> Engine = new(StringComparer.OrdinalIgnoreCase)
    {
        [ChromiumDropZones.Module] = ChromiumDropZones.EngineModule,
    };

    // A page may send a null module (the JSON allows it): it passes through, and the dispatcher answers NO_HANDLER, since
    // dispatch never throws.
    public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
        app.DispatchAsync(request.Module is { } module && Engine.TryGetValue(module, out var engine) ? Addressed(request, engine) : request,
            cancellationToken);

    // The host's own sends are not a page's: they reach the app's modules unchanged.
    public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null, CancellationToken cancellationToken = default) =>
        app.SendAsync(module, type, scope, payload, cancellationToken);

    public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null, CancellationToken cancellationToken = default) =>
        app.SendAsync<T>(module, type, scope, payload, cancellationToken);

    public IMessageDispatcher Use(MessageMiddleware middleware) => app.Use(middleware);

    private static IpcRequest Addressed(IpcRequest request, string module) => new()
    {
        Id = request.Id,
        Module = module,
        Type = request.Type,
        Scope = request.Scope,
        Payload = request.Payload,
        Timestamp = request.Timestamp,
    };
}
