using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A client that closes right after the engine's message still gets its close answered. The relay used to stop at the
/// FIRST pump to end and dispose the client's socket while the other pump was still answering that close, so the client
/// read EOF instead of the close frame.
/// </summary>
public class CdpRelayCloseStressTests
{
    [Fact]
    public async Task A_client_closing_right_after_a_message_always_gets_its_close_answered()
    {
        using var engine = new TcpListener(IPAddress.Loopback, 0);
        engine.Start();
        var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
        var serving = ServeEngineAsync(engine);
        await using var relay = CdpRelay.Start(0, enginePort);

        var failures = 0;
        // A thousand, because the race is probabilistic: with the fix reverted, 16 to 27 clients in 1000 lost their close
        // (5 runs), and a run of two hundred could pass with the bug present.
        await Parallel.ForEachAsync(Enumerable.Range(0, 1000), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (_, ct) =>
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{relay.Port}/devtools/browser/x"), ct);
            await client.SendAsync("""{"id":1,"method":"Target.createTarget"}"""u8.ToArray(), WebSocketMessageType.Text, true, ct);
            var buffer = new byte[4096];
            await client.ReceiveAsync(buffer, ct);
            try
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
            }
            catch (WebSocketException)
            {
                Interlocked.Increment(ref failures);
            }
        });

        engine.Stop();
        await Task.WhenAny(serving, Task.Delay(2000));
        Assert.Equal(0, failures);
    }

    // An engine that answers one message with an event, then reads until the relay closes its side.
    private static async Task ServeEngineAsync(TcpListener engine)
    {
        try
        {
            while (true)
            {
                var connection = await engine.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    using var _ = connection;
                    var stream = connection.GetStream();
                    var head = await CdpRelay.ReadHeadAsync(stream, CancellationToken.None);
                    if (head is null) return;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(CdpRelay.Accepting(head)));
                    using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                    var buffer = new byte[4096];
                    await socket.ReceiveAsync(buffer, CancellationToken.None);
                    await socket.SendAsync("""{"method":"Target.targetCreated","params":{"targetInfo":{"targetId":"T","type":"other"}}}"""u8.ToArray(),
                        WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                    try
                    {
                        await socket.ReceiveAsync(buffer, CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        // The relay went away.
                    }
                });
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }
}
