using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The browser process's debugging port as clients reach it (D86): Chromium's own, relayed, with a new tab called a
/// page. Chromium announces a tab made by <c>Target.createTarget</c> as <c>other</c>, and no browser MCP server takes one
/// of those up. Harvested with the relay from the adopter that built it.
/// </summary>
public sealed class CdpRelayTests
{
    private static string TypeIn(string message, string path)
    {
        using var document = JsonDocument.Parse(message);
        var node = document.RootElement;
        foreach (var step in path.Split('.')) node = int.TryParse(step, out var at) ? node[at] : node.GetProperty(step);
        return node.GetProperty("type").GetString()!;
    }

    /// <summary>The engine's announcement of a target, as it sends it: a new one, or one a session attached to.</summary>
    private static string Announced(string url, bool attached = false) => attached
        ? JsonSerializer.Serialize(new
        {
            method = "Target.attachedToTarget",
            @params = new { sessionId = "S", targetInfo = new { targetId = "T", type = "other", url }, waitingForDebugger = false },
        })
        : JsonSerializer.Serialize(new
        {
            method = "Target.targetCreated",
            @params = new { targetInfo = new { targetId = "T", type = "other", url, attached = false } },
        });

    [Theory]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData("https://site.example/a")]
    [InlineData("http://127.0.0.1:5300/")]
    [InlineData("chrome://newtab/")]
    public void A_new_tab_the_engine_calls_other_is_announced_as_a_page(string url)
    {
        Assert.Equal("page", TypeIn(CdpRelay.Rewrite(Announced(url)), "params.targetInfo"));
        Assert.Equal("page", TypeIn(CdpRelay.Rewrite(Announced(url, attached: true)), "params.targetInfo"));
    }

    [Fact]
    public void An_answer_that_lists_targets_says_them_right_too()
    {
        const string listed = """{"id":7,"result":{"targetInfos":[{"targetId":"A","type":"other","url":"about:blank"},{"targetId":"B","type":"browser_ui","url":"chrome://omnibox-popup.top-chrome/"}]}}""";
        const string one = """{"id":8,"result":{"targetInfo":{"targetId":"A","type":"other","url":"about:blank"}}}""";

        var rewritten = CdpRelay.Rewrite(listed);
        Assert.Equal("page", TypeIn(rewritten, "result.targetInfos.0"));
        Assert.Equal("browser_ui", TypeIn(rewritten, "result.targetInfos.1"));
        Assert.Equal("page", TypeIn(CdpRelay.Rewrite(one), "result.targetInfo"));
    }

    /// <summary>The engine's own windows and an extension's pages are not tabs, and stay what they were.</summary>
    [Theory]
    [InlineData("devtools://devtools/bundled/inspector.html")]
    [InlineData("chrome-extension://abcdef/offscreen.html")]
    [InlineData("chrome://omnibox-popup.top-chrome/")]
    public void What_is_not_a_tab_is_left_alone(string url)
    {
        var message = Announced(url);

        Assert.Same(message, CdpRelay.Rewrite(message));
    }

    [Theory]
    [InlineData("""{"id":1,"result":{}}""")]
    [InlineData("""{"method":"Page.frameNavigated","params":{"frame":{"id":"F","url":"https://site.example/"}}}""")]
    [InlineData("""{"method":"Target.targetCreated","params":{"targetInfo":{"type":"page","url":"about:blank"}}}""")]
    [InlineData("not json, but it says \"other\"")]
    public void Everything_else_passes_as_it_came(string message) =>
        Assert.Same(message, CdpRelay.Rewrite(message));

    /// <summary>A socket URL the engine names leads back to the relay, or a client would go around it.</summary>
    [Fact]
    public void An_http_answer_names_the_relay_not_the_engine()
    {
        const string version = """{"Browser":"Chrome/152","webSocketDebuggerUrl":"ws://127.0.0.1:41001/devtools/browser/x"}""";
        const string list = """[{"id":"A","type":"other","url":"about:blank","webSocketDebuggerUrl":"ws://localhost:41001/devtools/page/A"}]""";

        Assert.Contains("ws://127.0.0.1:9422/devtools/browser/x", CdpRelay.Readdress(version, 41001, 9422));
        var relisted = CdpRelay.Readdress(list, 41001, 9422);
        Assert.Contains("ws://127.0.0.1:9422/devtools/page/A", relisted);
        Assert.Equal("page", TypeIn($$"""{"x":{{relisted}}}""", "x.0"));
    }

    /// <summary>
    /// Through a real socket: a client asks the relay for a tab, the stand-in engine announces it as
    /// <c>other</c>, and the client hears a page. The HTTP door on the same port names the relay.
    /// </summary>
    [Fact]
    public async Task A_client_through_the_relay_hears_its_new_tab_as_a_page()
    {
        using var engine = new TcpListener(IPAddress.Loopback, 0);
        engine.Start();
        var enginePort = ((IPEndPoint)engine.LocalEndpoint).Port;
        var serving = ServeEngineAsync(engine, enginePort);

        await using var relay = CdpRelay.Start(0, enginePort);
        using var http = new HttpClient();
        var version = await http.GetStringAsync($"http://127.0.0.1:{relay.Port}/json/version");
        Assert.Contains($"ws://127.0.0.1:{relay.Port}/devtools/browser/x", version);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{relay.Port}/devtools/browser/x"), CancellationToken.None);
        await client.SendAsync("""{"id":1,"method":"Target.createTarget","params":{"url":"about:blank"}}"""u8.ToArray(),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var buffer = new byte[4096];
        var received = await client.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        var heard = Encoding.UTF8.GetString(buffer, 0, received.Count);
        Assert.Equal("page", TypeIn(heard, "params.targetInfo"));

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        engine.Stop();
        await Task.WhenAny(serving, Task.Delay(2000));
    }

    /// <summary>The stand-in engine: one HTTP answer, then one socket that announces every tab as <c>other</c>.</summary>
    private static async Task ServeEngineAsync(TcpListener engine, int port)
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
                    if (!head.Headers.ContainsKey("Upgrade"))
                    {
                        var body = Encoding.UTF8.GetBytes($$"""{"webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/devtools/browser/x"}""");
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(body);
                        return;
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(CdpRelay.Accepting(head)));
                    using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                    var buffer = new byte[4096];
                    await socket.ReceiveAsync(buffer, CancellationToken.None);
                    await socket.SendAsync(
                        """{"method":"Target.targetCreated","params":{"targetInfo":{"targetId":"T","type":"other","url":"about:blank"}}}"""u8.ToArray(),
                        WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                    await socket.ReceiveAsync(buffer, CancellationToken.None);
                });
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }
}
