using Shenora.Chromium;
using System.Text.Json;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A page's HTTP challenge as the DevTools protocol reports it and answers it. The channel itself, and that Chromium raises the
/// event for a navigation, were driven for real by the sample's <c>HooksProbe</c> (<c>SHENORA_SAMPLE_HOOKS_AUTH</c>).
/// </summary>
public class ChromiumPageAuthTests
{
    [Fact]
    public void A_server_challenge_reads_its_scheme_realm_and_origin()
    {
        var (requestId, request) = ChromiumPageAuth.Challenge(
            """{"requestId":"interception-7","request":{"url":"http://10.0.0.5/status","method":"GET"},"authChallenge":{"source":"Server","origin":"http://10.0.0.5","scheme":"Basic","realm":"device"}}""");

        Assert.Equal("interception-7", requestId);
        Assert.Equal(("http://10.0.0.5/status", "http://10.0.0.5", "basic", "device", false),
            (request.Url, request.Origin, request.Scheme, request.Realm, request.IsProxy));
    }

    [Fact]
    public void A_proxy_challenge_says_so_and_a_missing_realm_is_empty()
    {
        var (_, request) = ChromiumPageAuth.Challenge(
            """{"requestId":"r","request":{"url":"http://example.com/"},"authChallenge":{"source":"Proxy","origin":"http://proxy:3128","scheme":"ntlm"}}""");

        Assert.True(request.IsProxy);
        Assert.Equal(("ntlm", ""), (request.Scheme, request.Realm));
    }

    [Fact]
    public void Credentials_are_given_only_when_both_halves_are_there()
    {
        using var doc = JsonDocument.Parse(ChromiumPageAuth.Answer("r1", ("user", "secret")));
        var response = doc.RootElement.GetProperty("authChallengeResponse");
        Assert.Equal("r1", doc.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(("ProvideCredentials", "user", "secret"),
            (response.GetProperty("response").GetString(), response.GetProperty("username").GetString(), response.GetProperty("password").GetString()));
    }

    private const string Event =
        """{"requestId":"r9","request":{"url":"http://10.0.0.5/status"},"authChallenge":{"source":"Server","origin":"http://10.0.0.5","scheme":"Basic","realm":"device"}}""";

    private static (string Response, string RequestId, string? UserName) Responded(string json, Func<ChromiumAuthRequest, bool>? auth)
    {
        using var doc = JsonDocument.Parse(ChromiumPageAuth.Respond(json, new ChromiumPageHooks(null, null, null, auth: auth), "main", null));
        var response = doc.RootElement.GetProperty("authChallengeResponse");
        return (response.GetProperty("response").GetString()!, doc.RootElement.GetProperty("requestId").GetString()!,
            response.TryGetProperty("username", out var user) ? user.GetString() : null);
    }

    [Fact]
    public void Every_event_is_answered_with_what_the_app_said_or_the_default()
    {
        Assert.Equal(("ProvideCredentials", "r9", "admin"), Responded(Event, r => { r.UserName = "admin"; r.Password = "pw"; return true; }));
        Assert.Equal(("CancelAuth", "r9", null), Responded(Event, _ => true));
        Assert.Equal(("Default", "r9", null), Responded(Event, _ => false));
        Assert.Equal(("Default", "r9", null), Responded(Event, _ => throw new InvalidOperationException("app bug")));
    }

    [Theory]
    [InlineData("""{"requestId":"r9","request":{"url":"http://h/"}}""")]                 // no challenge in it
    [InlineData("""{"requestId":"r9","authChallenge":"Basic"}""")]                        // a challenge of another shape
    public void An_event_that_cannot_be_read_as_a_challenge_is_still_answered_so_the_request_is_not_left_paused(string json)
    {
        var asked = false;
        Assert.Equal(("Default", "r9", null), Responded(json, _ => { asked = true; return true; }));
        Assert.False(asked);
    }

    [Fact]
    public void An_event_with_no_request_id_is_answered_with_an_empty_one() =>
        Assert.Equal(("Default", "", null), Responded("{}", null));

    [Fact]
    public void An_answer_with_none_cancels_and_a_decline_is_the_default()
    {
        using var cancel = JsonDocument.Parse(ChromiumPageAuth.Answer("r2", (null, null)));
        var cancelled = cancel.RootElement.GetProperty("authChallengeResponse");
        Assert.Equal("CancelAuth", cancelled.GetProperty("response").GetString());
        Assert.False(cancelled.TryGetProperty("password", out _));

        using var declined = JsonDocument.Parse(ChromiumPageAuth.Answer("r3", null));
        Assert.Equal("Default", declined.RootElement.GetProperty("authChallengeResponse").GetProperty("response").GetString());
    }
}
