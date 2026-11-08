using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// A page's HTTP challenges, answered through the DevTools protocol's <c>Fetch</c> domain, in process: CEF never asks the request
/// handler's <c>get_auth_credentials</c> of this shell's pages (measured), so a hook built on it would never run. Only a browser of
/// an app that set <see cref="ChromiumHostOptions.OnAuthRequest"/> opens the channel. UI thread.
/// </summary>
internal sealed unsafe class ChromiumPageAuth : IDisposable
{
    private readonly DevToolsChannel _channel;
    private readonly IDisposable _subscription, _requests;

    public ChromiumPageAuth(_cef_browser_host_t* host, ChromiumPageHooks hooks, string windowName, ILogger? log)
    {
        _channel = new DevToolsChannel(host);
        _subscription = _channel.Subscribe("Fetch.authRequired", json =>
            _ = Observe(_channel.CallAsync("Fetch.continueWithAuth", Respond(json, hooks, windowName, log)), log));
        // Pausing is what makes Chromium raise a challenge at all (measured: with no pattern matching, no event came, and an empty list
        // is refused alongside handleAuthRequests), so the pattern is the cost. Documents only, a page's navigations and frames: one
        // pause each, where "*" would pause every script, image and fetch.
        _requests = _channel.Subscribe("Fetch.requestPaused", json =>
            _ = Observe(_channel.CallAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId = RequestId(json) })), log));
        _ = Observe(_channel.CallAsync("Fetch.enable", """{"handleAuthRequests":true,"patterns":[{"urlPattern":"*","resourceType":"Document"}]}"""), log);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _requests.Dispose();
        _channel.Dispose();
    }

    private static string RequestId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
    }

    private static Task Observe(Task call, ILogger? log) => call.ContinueWith(
        t => AppCallback.Log(log, () => "[Shenora.Chromium] An authentication DevTools call failed", LogLevel.Warning, t.Exception?.GetBaseException()),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>
    /// The <c>Fetch.continueWithAuth</c> parameters for one <c>Fetch.authRequired</c> event: the app's answer, or <c>Default</c> when
    /// the event cannot be read as a challenge, since the request stays paused until something answers it. Internal: tested.
    /// </summary>
    internal static string Respond(string authRequiredJson, ChromiumPageHooks hooks, string windowName, ILogger? log)
    {
        string requestId, scheme, origin;
        (string? UserName, string? Password)? answer;
        try
        {
            ChromiumAuthRequest request;
            (requestId, request) = Challenge(authRequiredJson);
            (scheme, origin) = (request.Scheme, request.Origin);
            answer = hooks.Authenticate(request, log);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            AppCallback.Log(log, () => $"[Shenora.Chromium] Window '{windowName}': an authentication challenge could not be read; it is left as it was", LogLevel.Warning, ex);
            return Answer(RequestIdOrEmpty(authRequiredJson), null);
        }
        AppCallback.Log(log, () => $"[Shenora.Chromium] Window '{windowName}': {(answer is null ? "left" : answer.Value.UserName is null ? "cancelled" : "answered")} a {scheme} challenge of {origin}");
        return Answer(requestId, answer);
    }

    private static string RequestIdOrEmpty(string json)
    {
        try { return RequestId(json); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return string.Empty; }
    }

    /// <summary>A <c>Fetch.authRequired</c> event as the request id to answer and the challenge the app is asked. Internal: tested.</summary>
    internal static (string RequestId, ChromiumAuthRequest Request) Challenge(string authRequiredJson)
    {
        using var doc = JsonDocument.Parse(authRequiredJson);
        var root = doc.RootElement;
        var url = root.TryGetProperty("request", out var request) && request.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        var challenge = root.GetProperty("authChallenge");
        string Text(string name) => challenge.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        var isProxy = string.Equals(Text("source"), "Proxy", StringComparison.OrdinalIgnoreCase);
        return (root.GetProperty("requestId").GetString() ?? "",
            new ChromiumAuthRequest(url, Text("origin"), Text("scheme").ToLowerInvariant(), Text("realm"), isProxy));
    }

    /// <summary>
    /// The answer as <c>Fetch.continueWithAuth</c>'s parameters: credentials when the app gave both, <c>CancelAuth</c> when it
    /// answered with none, and <c>Default</c> when it declined. Internal: tested.
    /// </summary>
    internal static string Answer(string requestId, (string? UserName, string? Password)? answer) =>
        answer switch
        {
            { UserName: { } user, Password: { } password } => JsonSerializer.Serialize(new { requestId, authChallengeResponse = new { response = "ProvideCredentials", username = user, password } }),
            not null => JsonSerializer.Serialize(new { requestId, authChallengeResponse = new { response = "CancelAuth" } }),
            _ => JsonSerializer.Serialize(new { requestId, authChallengeResponse = new { response = "Default" } }),
        };
}
