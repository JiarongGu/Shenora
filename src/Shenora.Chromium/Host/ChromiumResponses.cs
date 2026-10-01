using System.Text.Json;
using Shenora.Core.Sessions;

namespace Shenora.Chromium.Host;

/// <summary>
/// A session browser's responses as the DevTools protocol reports them, put back together. Measured (0.19.0, a 302
/// that sets a cookie, then a page that sets another): <c>Network.responseReceived</c> carries no <c>Set-Cookie</c>, and
/// a redirect's response never arrives as one. So <see cref="SessionEvents.ResponseReceived"/>, the session's way to see
/// a cookie being set, saw neither. A hop's <c>Set-Cookie</c> comes in <c>Network.responseReceivedExtraInfo</c>, and a
/// redirect's response in the next <c>Network.requestWillBeSent</c>; both share the request's id, so a hop is matched
/// by its status. Either may come first (measured: a redirect's extra info came just after its <c>requestWillBeSent</c>),
/// so a response waits for its extra info, or for its request to end; and Chromium sends none for some responses
/// (measured: the page a redirect led to), which are reported as they came. UI thread, as the channel's events are.
/// </summary>
internal sealed class ChromiumResponses(Func<Uri, bool> observe, bool sampleBodies)
{
    private const int MaxTracked = 512;   // a request whose end never arrives must not grow these for ever

    private sealed record Hop(int Status, List<string> Cookies);
    private sealed class Waiting(SessionResponse response, bool final)
    {
        public SessionResponse Response = response;
        public readonly bool Final = final;
        public bool HasCookies;
    }

    private readonly Dictionary<string, List<Hop>> _hops = new(StringComparer.Ordinal);         // extra info not yet claimed
    private readonly Dictionary<string, List<Waiting>> _waiting = new(StringComparer.Ordinal);  // responses, in order

    /// <summary><c>Network.responseReceivedExtraInfo</c>: one hop's raw headers. Answers a response it completes.</summary>
    public SessionResponse? ExtraInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("requestId").GetString() ?? string.Empty;
        var status = root.TryGetProperty("statusCode", out var code) && code.TryGetInt32(out var s) ? s : 0;
        var cookies = Header(root.TryGetProperty("headers", out var headers) ? headers : default, "set-cookie");

        if (_waiting.TryGetValue(id, out var list) && list.FindIndex(w => !w.HasCookies && w.Response.StatusCode == status) is var at and >= 0)
        {
            var waiting = list[at];
            waiting.Response = WithCookies(waiting.Response, cookies);
            waiting.HasCookies = true;
            if (waiting.Final && sampleBodies) return null;   // goes out with its body, as its request ends
            list.RemoveAt(at);
            if (list.Count == 0) _waiting.Remove(id);
            return waiting.Response;
        }
        if (_hops.Count >= MaxTracked) _hops.Clear();
        (_hops.TryGetValue(id, out var hops) ? hops : _hops[id] = []).Add(new Hop(status, cookies));
        return null;
    }

    /// <summary><c>Network.requestWillBeSent</c>: when it follows a redirect, that redirect's response, once its
    /// cookies are known.</summary>
    public SessionResponse? RequestWillBeSent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("redirectResponse", out var redirect) || redirect.ValueKind != JsonValueKind.Object) return null;
        var id = root.GetProperty("requestId").GetString() ?? string.Empty;
        return Arrived(id, Describe(redirect), final: false);
    }

    /// <summary><c>Network.responseReceived</c>: the final response, once its cookies are known.</summary>
    public SessionResponse? Response(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.GetProperty("requestId").GetString() ?? string.Empty;
        return Arrived(id, Describe(root.GetProperty("response")), final: true);
    }

    /// <summary>
    /// <c>Network.loadingFinished</c> or <c>Network.loadingFailed</c>: the request is over, and everything still waiting
    /// goes out as it is, in order. <paramref name="final"/> is the final response among them, whose body may be read.
    /// </summary>
    public IReadOnlyList<SessionResponse> Ended(string json, out string requestId, out SessionResponse? final)
    {
        using (var doc = JsonDocument.Parse(json)) requestId = doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
        _hops.Remove(requestId);
        final = null;
        if (!_waiting.Remove(requestId, out var list)) return [];
        final = list.FirstOrDefault(w => w.Final)?.Response;
        return [.. list.Select(w => w.Response)];
    }

    private SessionResponse? Arrived(string id, SessionResponse? response, bool final)
    {
        if (response is null) return null;
        if (Claim(id, response.StatusCode) is { } hop)
        {
            response = WithCookies(response, hop.Cookies);
            if (!(final && sampleBodies)) return response;
            Wait(id, new Waiting(response, final) { HasCookies = true });
            return null;
        }
        Wait(id, new Waiting(response, final));
        return null;
    }

    private void Wait(string id, Waiting waiting)
    {
        if (_waiting.Count >= MaxTracked) _waiting.Clear();
        (_waiting.TryGetValue(id, out var list) ? list : _waiting[id] = []).Add(waiting);
    }

    private Hop? Claim(string id, int status)
    {
        if (!_hops.TryGetValue(id, out var list)) return null;
        var index = list.FindIndex(hop => hop.Status == status);
        if (index < 0) return null;
        var hop = list[index];
        list.RemoveAt(index);
        if (list.Count == 0) _hops.Remove(id);
        return hop;
    }

    private SessionResponse? Describe(JsonElement response)
    {
        var url = response.GetProperty("url").GetString() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !observe(uri)) return null;
        var headers = new List<KeyValuePair<string, string>>();
        if (response.TryGetProperty("headers", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var header in map.EnumerateObject())
                // The protocol folds a repeated header into one value, lines apart: split them back.
                foreach (var value in (header.Value.GetString() ?? string.Empty).Split('\n'))
                    headers.Add(new(header.Name, value));
        return new SessionResponse(url, response.GetProperty("status").GetInt32(),
            response.TryGetProperty("statusText", out var text) ? text.GetString() ?? string.Empty : string.Empty, headers, string.Empty);
    }

    // The hop's Set-Cookie headers, in place of any the response itself carried (the same ones, where it carried any).
    private static SessionResponse WithCookies(SessionResponse response, List<string> cookies) =>
        cookies.Count == 0 ? response : response with
        {
            Headers = [.. response.Headers.Where(h => !h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)),
                       .. cookies.Select(c => new KeyValuePair<string, string>("Set-Cookie", c))],
        };

    private static List<string> Header(JsonElement headers, string name)
    {
        var values = new List<string>();
        if (headers.ValueKind != JsonValueKind.Object) return values;
        foreach (var header in headers.EnumerateObject())
            if (header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                values.AddRange((header.Value.GetString() ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        return values;
    }
}
