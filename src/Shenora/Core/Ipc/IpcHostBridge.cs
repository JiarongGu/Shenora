using Microsoft.Extensions.Logging;

namespace Shenora.Core.Ipc;

/// <summary>Inputs for <see cref="IpcHostBridge"/>.</summary>
public sealed class IpcHostBridgeOptions
{
    /// <summary>The pipeline incoming requests are dispatched into.</summary>
    public required IMessageDispatcher Dispatcher { get; init; }

    /// <summary>
    /// The outbound channel whose ready gate the client's handshake opens. Optional — a host that
    /// pushes nothing needs none. Opening is protocol and lives here; CLOSING stays the base's job,
    /// because only the base knows which of its own events mean "the client can no longer receive" —
    /// see <see cref="NotificationPump.Close"/> for the trap that decision must avoid.
    /// </summary>
    public NotificationPump? Pump { get; init; }

    /// <summary>
    /// Invoked on the ready handshake with the handshake request (its payload is app-defined). Fires
    /// PER handshake — a reloaded page reports ready again, which is the moment to clear per-page
    /// state. A callback exception is logged and the handshake still succeeds.
    /// </summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>
    /// What to tell the client about this host, returned as the handshake's response data. Null
    /// answers the handshake with no data.
    /// </summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Whether disposing the bridge CANCELS dispatches still in flight. True is the desktop shape: the
    /// bridge's death is the app's. ⚠ Set false where the bridge's lifetime is a PAGE's — on mobile the
    /// WebView is rebuilt on every activity recreation, and cancelling there aborts work whose effects
    /// are host-side (<c>docs/guides/mobile.md</c>). With false the work completes and only its response
    /// has nowhere to go.
    /// </summary>
    public bool CancelInFlightOnDispose { get; init; } = true;
}

/// <summary>
/// The transport-neutral half of a host's INBOUND channel: parse → handshake-or-dispatch → response
/// JSON, with the dispatch lifetime and the error boundary that go with it. The mirror of the client's
/// <c>ShenoraBridge</c>, which owns correlation, category demux and batch unbundling on its side.
/// <para>
/// Owns NO TRANSPORT and NO TIMER, for the same reason <see cref="NotificationPump"/> does not:
/// which thread may touch a base's client is a base-specific fact. The base reads a message off its
/// own wire, calls <see cref="HandleIncomingAsync"/>, and writes the result back if there is one.
/// </para>
/// </summary>
public sealed class IpcHostBridge : IDisposable
{
    /// <summary>
    /// Reserved wire route: the client's ready handshake module (mirrored by the client bridge, and
    /// pinned across the two languages by <c>WireMirrorTests</c>).
    /// </summary>
    public const string HandshakeModule = "SHENORA";

    /// <summary>Reserved wire route: the client's ready handshake type (mirrored by the client bridge).</summary>
    public const string HandshakeType = "READY";

    private readonly IpcHostBridgeOptions _options;
    private readonly ILogger? _log;
    private bool _disposed;

    /// <summary>
    /// The lifetime handed to every dispatch, cancelled in <see cref="Dispose"/>. The CALLER's
    /// lifetime, not per-request client cancellation (D23).
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// The token, captured ONCE: reading <c>_lifetime.Token</c> at dispatch time throws
    /// <see cref="ObjectDisposedException"/> for a message arriving after <see cref="Dispose"/>, which
    /// is the normal case during teardown. The struct stays readable and still reports cancellation.
    /// </summary>
    private readonly CancellationToken _lifetimeToken;

    /// <summary>Construct before the base's client can send anything.</summary>
    public IpcHostBridge(IpcHostBridgeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = options.Log;
        _lifetimeToken = _lifetime.Token;
    }

    /// <summary>True once the client has completed the ready handshake — only meaningful with a pump.</summary>
    public bool IsClientReady => _options.Pump?.IsOpen ?? false;

    /// <summary>
    /// Parse → handshake-or-dispatch → response JSON. Null when the input was not a valid request
    /// (logged and dropped; there is nothing to correlate a response to), which a base should treat as
    /// "write nothing back".
    /// <para>
    /// 🔴 <b>NEVER THROWS.</b> A base typically calls this from an event handler with no caller left to
    /// catch anything — on WinForms an <c>async void</c> one, where an escape re-throws on the UI
    /// thread and takes the process down.
    /// </para>
    /// <para>
    /// Context-preserving by design (§5): no <c>ConfigureAwait(false)</c>, because a facade routing a
    /// window command must resume on the thread it was called on.
    /// </para>
    /// </summary>
    public async Task<string?> HandleIncomingAsync(string json)
    {
        IpcRequest? request;
        try
        {
            request = IpcJson.Deserialize<IpcRequest>(json);
        }
        catch (Exception ex)
        {
            Log(() => "[Shenora.Core.Ipc] Invalid IPC message dropped", ex);
            return null;
        }
        if (request is null) return null;

        try
        {
            if (string.Equals(request.Module, HandshakeModule, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(request.Type, HandshakeType, StringComparison.OrdinalIgnoreCase))
            {
                return IpcJson.Serialize(HandleHandshake(request));
            }

            var response = await _options.Dispatcher.DispatchAsync(request, _lifetimeToken);
            return IpcJson.Serialize(response);
        }
        catch (Exception ex)
        {
            // MessageDispatcher never throws, but IMessageDispatcher is a public seam — and Serialize
            // itself can throw on an unserializable handler result (cycles, Type/delegate members).
            // The client must still get a response, carrying nothing but the code.
            Log(() => $"[Shenora.Core.Ipc] Error handling {request.Module}/{request.Type}", ex);
            return IpcJson.Serialize(IpcResponse.CreateError(request.Id, IpcErrorCodes.UnknownError,
                parameters: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().Name }));
        }
    }

    private IpcResponse HandleHandshake(IpcRequest request)
    {
        _options.Pump?.Open();
        Log(() => "[Shenora.Core.Ipc] Client ready");
        // Per-page glue (splash, overlays) failing must not fail the client's init await.
        if (_options.OnClientReady is { } onReady)
        {
            AppCallback.Run(() => onReady(request),
                ex => Log(() => "[Shenora.Core.Ipc] OnClientReady callback failed", ex));
        }
        return IpcResponse.CreateSuccess(request.Id, _options.Shell);
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="AppCallback.Log"/>): every site here is inside a
    /// <c>catch</c>, so a throwing sink would defeat the catch it reports from.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <summary>
    /// Cancel the dispatch lifetime (unless <see cref="IpcHostBridgeOptions.CancelInFlightOnDispose"/>
    /// opted out). ⚠ Call FIRST in the base's own teardown, before the transport and any subscriptions
    /// are pulled out from under an in-flight handler. Does NOT dispose the pump — the base owns that.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_options.CancelInFlightOnDispose)
        {
            // Guarded because Cancel runs app continuations synchronously — one of them throwing must
            // not stop the rest of a base's teardown.
            try { _lifetime.Cancel(); }
            catch (Exception ex) { Log(() => "[Shenora.Core.Ipc] Host bridge dispose: cancellation callback threw", ex); }
        }
        // Disposing WITHOUT cancelling leaves the captured token readable and permanently un-fired.
        _lifetime.Dispose();
    }
}
