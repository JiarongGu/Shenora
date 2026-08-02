using Shenora.Core;

namespace Shenora.Ipc;

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

    /// <summary>Diagnostics sink.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The transport-neutral half of a host's INBOUND channel: parse → handshake-or-dispatch →
/// response JSON, with the dispatch lifetime and the error boundary that go with it. The mirror of
/// the client's <c>ShenoraBridge</c>, which has owned correlation, category demux and batch
/// unbundling since P3 while the host side had no equivalent — so <c>WebViewIpcBridge</c> was the
/// only thing that knew this shape and it was welded to WinForms.
/// <para>
/// Evidence, not anticipation: standing up a second base for the D3 transport-neutrality spike
/// needed no change to <c>Shenora.Ipc</c> at all, but did mean hand-writing this by hand — every
/// non-WinForms host writes the same read → deserialize → dispatch → serialize → write loop. This
/// is that loop's middle, which is the part that is identical everywhere.
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
    private readonly Action<string>? _log;
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
            Log(() => $"[Shenora.Ipc] Invalid IPC message dropped: {ex.Message}");
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
            Log(() => $"[Shenora.Ipc] Error handling {request.Module}/{request.Type}: {ex}");
            return IpcJson.Serialize(IpcResponse.CreateError(request.Id, IpcErrorCodes.UnknownError,
                parameters: new Dictionary<string, string> { ["exceptionType"] = ex.GetType().Name }));
        }
    }

    private IpcResponse HandleHandshake(IpcRequest request)
    {
        _options.Pump?.Open();
        Log(() => "[Shenora.Ipc] Client ready");
        // Per-page glue (splash, overlays) failing must not fail the client's init await. The report
        // sink goes through the guarded Log for the same reason the callback is guarded at all.
        if (_options.OnClientReady is { } onReady)
        {
            AppCallback.Run(() => onReady(request),
                ex => Log(() => $"[Shenora.Ipc] OnClientReady callback failed: {ex.Message}"));
        }
        return IpcResponse.CreateSuccess(request.Id);
    }

    /// <summary>
    /// Guarded + lazy, via the one owner (<see cref="AppCallback.Log"/>): every site here is inside
    /// a <c>catch</c> that exists to stop a failure escaping, so a throwing sink would defeat the
    /// very catch it reports from.
    /// </summary>
    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <summary>
    /// Cancel the dispatch lifetime. Call FIRST in the base's own teardown, before the transport and
    /// any subscriptions are pulled out from under an in-flight handler — it should learn the client
    /// is gone while its await can still act on it. Does NOT dispose the pump: the base owns that,
    /// because the base owns the tick that drains it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Guarded because Cancel runs app continuations synchronously — one of them throwing must
        // not stop the rest of a base's teardown.
        try { _lifetime.Cancel(); }
        catch (Exception ex) { Log(() => $"[Shenora.Ipc] Host bridge dispose: cancellation callback threw ({ex.Message})"); }
        _lifetime.Dispose();
    }
}
