using System.Text.Json;
using Shenora;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The transport-neutral inbound half. Note what this file does NOT reference: no WebView2, no
/// WinForms, no control, no window — which is the whole claim. <c>WebViewIpcBridgeTests</c> proves
/// the same protocol through the WebView2 composition; these prove it stands alone, which is what a
/// second base (an on-device mobile host) actually depends on.
/// </summary>
public class IpcHostBridgeTests
{
    private static string ReadyJson(string id = "h1") =>
        IpcJson.Serialize(new IpcRequest
        {
            Id = id,
            Module = IpcHostBridge.HandshakeModule,
            Type = IpcHostBridge.HandshakeType,
        });

    /// <summary>
    /// An app-implemented dispatcher that throws. <see cref="MessageDispatcher"/> never does, but
    /// <see cref="IMessageDispatcher"/> is a PUBLIC SEAM and an app implementation carries no such
    /// guarantee — which is exactly the case the bridge's catch-all exists for.
    /// </summary>
    private sealed class ThrowingDispatcher : IMessageDispatcher
    {
        public Task<IpcResponse> DispatchAsync(IpcRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("connection string Server=secret;Password=hunter2");

        public Task<IpcResponse> SendAsync(string module, string type, string? scope = null, object? payload = null,
                                           CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<T?> SendAsync<T>(string module, string type, string? scope = null, object? payload = null,
                                     CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IMessageDispatcher Use(MessageMiddleware middleware) => this;
    }

    [Fact]
    public async Task A_host_with_no_pump_still_handshakes()
    {
        // A host that pushes nothing supplies no pump. The handshake must still succeed rather than
        // null-reference on the gate it does not have.
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions { Dispatcher = new MessageDispatcher() });

        var response = await bridge.HandleIncomingAsync(ReadyJson());

        using var doc = JsonDocument.Parse(response!);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(bridge.IsClientReady);   // no pump, so there is no gate to be open
    }

    [Fact]
    public async Task The_handshake_opens_the_pumps_gate()
    {
        // The pairing that moved out of WebViewIpcBridge: handshake -> Open() is PROTOCOL, so every
        // base gets it rather than re-wiring it (and eventually wiring it to the wrong event).
        using var pump = new NotificationPump(new NotificationPumpOptions());
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            Pump = pump,
        });

        Assert.False(pump.IsOpen);
        await bridge.HandleIncomingAsync(ReadyJson());

        Assert.True(pump.IsOpen);
        Assert.True(bridge.IsClientReady);
    }

    [Fact]
    public async Task The_handshake_answers_with_what_the_shell_can_do()
    {
        // The other half of "universal": one PAGE on every shell. The client learns the capability
        // set from the ack it already waits for, so it can render before its first layout instead of
        // sniffing the platform or discovering a missing module by getting NO_HANDLER back.
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            Shell = new ShellInfo
            {
                Name = "test-shell",
                Capabilities = [ShellCapability.WindowChrome, ShellCapability.DropZones],
            },
        });

        var response = await bridge.HandleIncomingAsync(ReadyJson());

        using var doc = JsonDocument.Parse(response!);
        // Named rather than indexed: dropping the descriptor from the ack otherwise surfaces as a bare
        // KeyNotFoundException, which says nothing about WHICH contract broke. Verified by removing it.
        Assert.True(doc.RootElement.TryGetProperty("data", out var data),
            "the handshake ack carried no `data` — the shell descriptor is not reaching the client, so " +
            "every page falls back to 'assume nothing' and renders no capability-gated UI.");
        Assert.Equal("test-shell", data.GetProperty("name").GetString());
        Assert.Equal(["windowChrome", "dropZones"],
            data.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_host_that_declares_nothing_answers_exactly_as_before()
    {
        // Additive: an existing client sees the same success-with-no-data ack it always did.
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions { Dispatcher = new MessageDispatcher() });

        var response = await bridge.HandleIncomingAsync(ReadyJson());

        using var doc = JsonDocument.Parse(response!);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task A_throwing_OnClientReady_still_completes_the_handshake()
    {
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions
        {
            Dispatcher = new MessageDispatcher(),
            OnClientReady = _ => throw new InvalidOperationException("app glue exploded"),
        });

        var response = await bridge.HandleIncomingAsync(ReadyJson());

        // Per-page glue failing must not fail the client's init await.
        using var doc = JsonDocument.Parse(response!);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_throwing_dispatcher_answers_without_leaking_the_exception_text()
    {
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions { Dispatcher = new ThrowingDispatcher() });

        var response = await bridge.HandleIncomingAsync(IpcJson.Serialize(
            new IpcRequest { Id = "r1", Module = "APP", Type = "PING" }));

        // The no-raw-exception-text boundary (design §5) — the client learns the CODE and the
        // exception TYPE name, never the message. A new error path gets a leak test; this is one.
        Assert.NotNull(response);
        Assert.DoesNotContain("hunter2", response, StringComparison.Ordinal);
        Assert.DoesNotContain("connection string", response, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(response!);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(IpcErrorCodes.UnknownError, error.GetProperty("code").GetString());
        Assert.Equal(nameof(InvalidOperationException),
            error.GetProperty("parameters").GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task Malformed_input_is_dropped_rather_than_answered()
    {
        using var bridge = new IpcHostBridge(new IpcHostBridgeOptions { Dispatcher = new MessageDispatcher() });

        // Nothing to correlate a response to, so there is nothing honest to send back — the client's
        // own timeout surfaces it.
        Assert.Null(await bridge.HandleIncomingAsync("not json"));
        Assert.Null(await bridge.HandleIncomingAsync("null"));
    }

    [Fact]
    public async Task A_message_arriving_after_dispose_is_answered_not_thrown()
    {
        // The token is captured ONCE at construction: reading it from the disposed source at dispatch
        // time would throw ObjectDisposedException, and a message arriving during teardown is the
        // NORMAL case — teardown is exactly when the client is going away.
        var dispatcher = new MessageDispatcher();
        dispatcher.MapRoute("APP", "PING", _ => "pong");
        var bridge = new IpcHostBridge(new IpcHostBridgeOptions { Dispatcher = dispatcher });

        bridge.Dispose();

        var response = await bridge.HandleIncomingAsync(IpcJson.Serialize(
            new IpcRequest { Id = "r1", Module = "APP", Type = "PING" }));

        using var doc = JsonDocument.Parse(response!);
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(IpcErrorCodes.OperationCancelled,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var bridge = new IpcHostBridge(new IpcHostBridgeOptions { Dispatcher = new MessageDispatcher() });

        bridge.Dispose();
        bridge.Dispose();   // a base's teardown may run twice; the second must not throw on the disposed CTS
    }

    [Fact]
    public void Null_options_are_a_caller_bug()
    {
        Assert.Throws<ArgumentNullException>(() => new IpcHostBridge(null!));
    }
}
