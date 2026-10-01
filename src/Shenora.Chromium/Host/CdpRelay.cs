using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shenora.Chromium.Host;

/// <summary>
/// The browser process's debugging port as clients reach it (D86): Chromium's own endpoint, relayed, with the one thing
/// the engine says wrong said right. Everything else passes through unchanged. Harvested from an adopter, which built
/// it for its in-app browser and measured Playwright's and Chrome DevTools' MCP servers opening tabs through it.
/// </summary>
/// <remarks>
/// <para><b>What is corrected.</b> The engine announces a tab made by <c>Target.createTarget</c> as type
/// <c>other</c>, and calls it a <c>page</c> only later (<c>Target.targetInfoChanged</c>). Playwright's MCP server and Chrome
/// DevTools' both wait for a page, so neither could open a tab. A target the engine calls <c>other</c> whose address is
/// a web page or a blank tab is a tab, and this says so. An <c>other</c> that is the engine's own (a devtools window, an
/// extension's page) is left alone.</para>
///
/// <para><b>Loopback, like the engine's own port</b>: anything on this machine can drive the browser while it runs.
/// The relay adds no reach the engine's port did not have, which takes keeping the engine's own refusals: see
/// <see cref="Admits"/>.</para>
/// </remarks>
internal sealed class CdpRelay : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly int _engine;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    private CdpRelay(int port, int engine)
    {
        _engine = engine;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = AcceptAsync();
    }

    /// <summary>The port clients are given.</summary>
    public int Port { get; }

    /// <summary>Relay <paramref name="port"/> (0 for any free one) to the engine's own debug port.</summary>
    public static CdpRelay Start(int port, int enginePort) => new(port, enginePort);

    /// <summary>
    /// A message from the engine as a client should see it: a tab it announced as <c>other</c> is a
    /// <c>page</c>. Anything else, including text that is not JSON, is returned as it came.
    /// </summary>
    public static string Rewrite(string message)
    {
        if (!message.Contains("\"other\"", StringComparison.Ordinal)) return message;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(message);
        }
        catch (JsonException)
        {
            return message;
        }

        if (root is not JsonObject envelope) return message;
        var changed = false;
        foreach (var info in TargetInfos(envelope)) changed |= Correct(info);
        return changed ? envelope.ToJsonString(Unescaped) : message;
    }

    /// <summary>
    /// An HTTP answer's body as a client should see it: the engine's own address replaced by this
    /// relay's, so a socket URL it names leads back here, and a tab in a target list said right.
    /// </summary>
    public static string Readdress(string body, int enginePort, int relayPort)
    {
        var text = body
            .Replace($"127.0.0.1:{enginePort}", $"127.0.0.1:{relayPort}", StringComparison.Ordinal)
            .Replace($"localhost:{enginePort}", $"127.0.0.1:{relayPort}", StringComparison.Ordinal);
        if (!text.Contains("\"other\"", StringComparison.Ordinal)) return text;

        try
        {
            if (JsonNode.Parse(text) is JsonArray list)
            {
                var changed = false;
                foreach (var item in list)
                {
                    if (item is JsonObject target) changed |= Correct(target);
                }

                if (changed) return list.ToJsonString(Unescaped);
            }
        }
        catch (JsonException)
        {
            // Not a target list: as it came.
        }

        return text;
    }

    /// <summary>Whether an address is one a tab shows: a web page, a blank tab, or the engine's new-tab page.</summary>
    public static bool IsTab(string url) =>
        url.Length == 0
        || url == "about:blank"
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("chrome://newtab", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("chrome://new-tab-page", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<JsonObject> TargetInfos(JsonObject envelope)
    {
        if (envelope["params"]?["targetInfo"] is JsonObject announced) yield return announced;
        if (envelope["result"]?["targetInfo"] is JsonObject answered) yield return answered;
        if (envelope["result"]?["targetInfos"] is JsonArray listed)
        {
            foreach (var item in listed)
            {
                if (item is JsonObject target) yield return target;
            }
        }
    }

    private static bool Correct(JsonObject target)
    {
        if (target["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String
            || type.GetValue<string>() != "other") return false;
        var url = target["url"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
        if (!IsTab(url)) return false;
        target["type"] = "page";
        return true;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        var stream = connection.GetStream();
        try
        {
            var request = await ReadHeadAsync(stream, _stop.Token).ConfigureAwait(false);
            if (request is null) return;

            var socket = request.Headers.TryGetValue("Upgrade", out var upgrade)
                         && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase);
            if (!Admits(request, socket))
            {
                await stream.WriteAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (socket)
            {
                await RelaySocketAsync(stream, request, _stop.Token).ConfigureAwait(false);
            }
            else
            {
                await RelayHttpAsync(stream, request, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or SocketException or WebSocketException
                                          or OperationCanceledException or HttpRequestException)
        {
            // A client or the engine went away mid-exchange: the connection ends, the relay does not.
        }
    }

    /// <summary>
    /// 🔴 The engine's own checks, which its port applies and the relay's requests to it would otherwise BYPASS: the
    /// relay calls the engine with its own <c>Host</c> and no <c>Origin</c>. So a <c>Host</c> must name an IP address or
    /// <c>localhost</c> (a DNS-rebinding page arrives under its own name), and a socket must carry no <c>Origin</c> (the
    /// engine is started allowing none, so a web page cannot open one). A path must start with <c>/</c>: appended to the
    /// engine's address, <c>@host/…</c> named another host.
    /// </summary>
    public static bool Admits(Head request, bool socket)
    {
        if (!request.Path.StartsWith('/')) return false;
        if (socket && request.Headers.ContainsKey("Origin")) return false;
        if (!request.Headers.TryGetValue("Host", out var host)) return true;   // as the engine: only a NAMED host is refused
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)]
                 : host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':')] : host;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || IPAddress.TryParse(name.Trim('[', ']'), out _);
    }

    private async Task RelayHttpAsync(Stream stream, Head request, CancellationToken ct)
    {
        using var forwarded = new HttpRequestMessage(new HttpMethod(request.Method), $"http://127.0.0.1:{_engine}{request.Path}");
        using var answer = await _http.SendAsync(forwarded, ct).ConfigureAwait(false);
        var body = Readdress(await answer.Content.ReadAsStringAsync(ct).ConfigureAwait(false), _engine, Port);
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {(int)answer.StatusCode} {answer.ReasonPhrase}\r\n"
                   + $"Content-Type: {answer.Content.Headers.ContentType?.ToString() ?? "application/json; charset=UTF-8"}\r\n"
                   + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private async Task RelaySocketAsync(Stream stream, Head request, CancellationToken ct)
    {
        using var engine = new ClientWebSocket();
        engine.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await engine.ConnectAsync(new Uri($"ws://127.0.0.1:{_engine}{request.Path}"), ct).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            return;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(Accepting(request)), ct).ConfigureAwait(false);
        using var client = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var up = PumpAsync(client, engine, text => text, ended.Token);
        var down = PumpAsync(engine, client, Rewrite, ended.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        await ended.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>The server half of the WebSocket handshake (RFC 6455 §4.2.2).</summary>
    public static string Accepting(Head request)
    {
        var key = request.Headers.TryGetValue("Sec-WebSocket-Key", out var value) ? value : "";
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        return "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
               + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, Func<string, string> say, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && from.State == WebSocketState.Open && to.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult received;
            do
            {
                received = await from.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    // Answered, then passed on: a side that closes waits for the other's close frame,
                    // and a relay that only forwarded it would leave that side waiting on a dropped socket.
                    var status = from.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                    if (from.State == WebSocketState.CloseReceived)
                    {
                        await from.CloseOutputAsync(status, from.CloseStatusDescription, CancellationToken.None).ConfigureAwait(false);
                    }

                    if (to.State == WebSocketState.Open)
                    {
                        await to.CloseOutputAsync(status, from.CloseStatusDescription, CancellationToken.None).ConfigureAwait(false);
                    }

                    return;
                }

                message.Write(buffer, 0, received.Count);
            }
            while (!received.EndOfMessage);

            var bytes = received.MessageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetBytes(say(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)))
                : message.ToArray();
            await to.SendAsync(bytes, received.MessageType, endOfMessage: true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A request's first line and headers — all a relay reads before it knows where to send it.</summary>
    public sealed record Head(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    /// <summary>Read up to the blank line that ends a request's head, or null when none came.</summary>
    public static async Task<Head?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
        }

        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new Head(first[0], first[1], headers);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        _http.Dispose();
        _stop.Dispose();
    }
}
