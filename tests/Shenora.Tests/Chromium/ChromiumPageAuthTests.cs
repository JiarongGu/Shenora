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
