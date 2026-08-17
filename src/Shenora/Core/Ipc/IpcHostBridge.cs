using Microsoft.Extensions.Logging;

namespace Shenora.Core.Ipc;

/// <summary>Inputs for <see cref="IpcHostBridge"/>.</summary>
public sealed class IpcHostBridgeOptions
{
    /// <summary>The pipeline incoming requests are dispatched into.</summary>
    public required IMessageDispatcher Dispatcher { get; init; }

    /// <summary>
    /// The outbound channel whose ready gate the client's handshake opens. Optional — a host that
    /// pushes nothing needs none. Supplying it here is what keeps "a handshake opens the gate" in
    /// ONE place: it is protocol, not transport, so every base would otherwise re-wire it (and one
    /// of them would eventually wire it to the wrong event, which is P5.5 H3).
    /// <para>
    /// CLOSING the gate stays the base's job, because only the base knows which of its own events
    /// mean "the client can no longer receive" — see <see cref="NotificationPump.Close"/> for the
    /// trap that decision must avoid.
    /// </para>
    /// </summary>
    public NotificationPump? Pump { get; init; }

    /// <summary>
    /// Invoked on the ready handshake with the handshake request (its payload is app-defined).
    /// Fires PER handshake — a reloaded page (renderer-crash recovery, dev hot reload) reports
    /// ready again, which is the moment to clear per-page state (stale overlays, splash).
    /// A callback exception is logged and the handshake still succeeds.
    /// </summary>
    public Action<IpcRequest>? OnClientReady { get; init; }

    /// <summary>
    /// What to tell the client about this host, returned as the handshake's response data. Null
    /// answers the handshake with no data, exactly as before — so this is additive for every
    /// existing client, which simply ignores a field it does not read.
    /// </summary>
    public ShellInfo? Shell { get; init; }

    /// <summary>Diagnostics sink.</summary>
    public ILogger? Log { get; init; }

    /// <summary>
    /// Whether disposing the bridge CANCELS dispatches still in flight. True — the desktop shape —
    /// means the bridge's death is the app's death, and a handler still awaiting should learn it.
    /// False is for a bridge whose lifetime is a PAGE's, not the app's: on mobile the WebView (and
    /// this bridge with it) is rebuilt on every activity recreation, and cancelling then aborts work
    /// whose effects are host-side — measured on a device, a save whose picker was open died
    /// <c>OPERATION_CANCELLED</c> with the user's chosen file created and left empty. With false the
    /// in-flight work runs to completion; its RESPONSE still has nowhere to go, which is the correct
    /// asymmetry — the page is gone, the user's file is not.
    /// </summary>
    public bool CancelInFlightOnDispose { get; init; } = true;
}

/// <summary>
/// The transport-neutral half of a host's INBOUND channel: parse → handshake-or-dispatch → response
/// JSON, with the dispatch lifetime and the error boundary that go with it. The mirror of the client's
/// <c>ShenoraBridge</c>, which owns correlation, category demux and batch unbundling on its side.
/// <para>
/// Every non-WinForms host writes the same read → deserialize → dispatch → serialize → write loop, and
/// this is that loop's MIDDLE — the part identical everywhere. Owning it here is what stops a second
/// base re-deriving it.
/// </para>
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
    /// pinned across the two languages by <c>WireMirrorTests</c>). Lives HERE rather than on a
    /// specific transport because it is the wire contract every base speaks —
    /// <c>WebViewIpcBridge.HandshakeModule</c> forwards to it.
    /// </summary>
    public const string HandshakeModule = "SHENORA";

    /// <summary>Reserved wire route: the client's ready handshake type (mirrored by the client bridge).</summary>
    public const string HandshakeType = "READY";

    private readonly IpcHostBridgeOptions _options;
    private readonly ILogger? _log;
    private bool _disposed;

    /// <summary>
    /// The lifetime handed to every dispatch, cancelled in <see cref="Dispose"/> (P6.4). Before this
    /// the whole pipeline was uncancellable: a handler still awaiting when the client went away had
    /// no way to learn that, because it was never given a token to observe. This is the CALLER's
    /// lifetime, not per-request client cancellation — a one-way <c>post</c> has nobody waiting, so
    /// "stop that operation" stays an app-level route.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>
    /// The token, captured ONCE. Reading <c>_lifetime.Token</c> at dispatch time would throw
    /// <see cref="ObjectDisposedException"/> for a message that arrives after <see cref="Dispose"/> —
    /// and messages arriving during teardown is the normal case, not a corner one, since that is
    /// exactly when the client is going away. A <see cref="CancellationToken"/> is a struct that
    /// stays readable after its source is disposed, and still reports the cancellation.
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
    /// (nothing to correlate a response to — logged and dropped; the client's own timeout surfaces
    /// it), which a base should treat as "write nothing back".
    /// <para>
    /// NEVER THROWS. A base typically calls this from an event handler with no caller left to catch
    /// anything — on WinForms an <c>async void</c> one, where an escape re-throws on the UI thread's
    /// synchronization context and takes the process down.
    /// </para>
    /// <para>
    /// Context-preserving by design (§5): no <c>ConfigureAwait(false)</c>, because a facade routing
    /// a window command touches UI state and must resume on the thread it was called on. A base that
    /// dispatches from its UI thread keeps that guarantee; one that dispatches from a pool thread
    /// simply has no context to preserve.
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
            // MessageDispatcher never throws, but IMessageDispatcher is a public seam (an app
            // implementation carries no such guarantee) — and Serialize itself can throw on an
            // unserializable handler result (cycles, Type/delegate members). The client must still
            // get a response, and per design §5 it learns nothing but the code.
            Log(() => $"[Shenora.Core.Ipc] Error handling {request.Module}/{request.Type}", ex);
            return IpcJson.Serialize(IpcResponse.CreateError(request.Id, IpcErrorCodes.UnknownError,
                parameters: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().Name }));
        }
    }

    private IpcResponse HandleHandshake(IpcRequest request)
    {
        _options.Pump?.Open();
        Log(() => "[Shenora.Core.Ipc] Client ready");
        // Per-page glue (splash, overlays) failing must not fail the client's init await. The report
        // sink goes through the guarded Log for the same reason the callback is guarded at all.
        if (_options.OnClientReady is { } onReady)
        {
            AppCallback.Run(() => onReady(request),
                ex => Log(() => "[Shenora.Core.Ipc] OnClientReady callback failed", ex));
        }
        // The shell descriptor rides the ack. Null keeps the pre-existing "success, no data" shape.
        return IpcResponse.CreateSuccess(request.Id, _options.Shell);
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="AppCallback.Log"/>): every site here is inside
    /// a <c>catch</c> that exists to stop a failure escaping, so a throwing sink would defeat the
    /// very catch it reports from.
    /// </summary>
    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);

    /// <summary>
    /// Cancel the dispatch lifetime (unless <see cref="IpcHostBridgeOptions.CancelInFlightOnDispose"/>
    /// opted out). Call FIRST in the base's own teardown, before the transport and any subscriptions
    /// are pulled out from under an in-flight handler — it should learn the client is gone while its
    /// await can still act on it. Does NOT dispose the pump: the base owns that, because the base
    /// owns the tick that drains it.
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
        // Disposing WITHOUT cancelling leaves the already-captured token readable and permanently
        // un-fired — in-flight work keeps its token and simply never hears a cancellation from it.
        _lifetime.Dispose();
    }
}
