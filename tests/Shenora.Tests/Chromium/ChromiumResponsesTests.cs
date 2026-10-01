using Shenora.Chromium.Host;
using Shenora.Core.Sessions;

namespace Shenora.Tests.Chromium;

/// <summary>
/// A CEF session's responses put back together from the DevTools events. Measured on 0.19.0 against a 302 that sets a
/// cookie, then a page that sets another: neither the redirect nor either cookie reached ResponseReceived.
/// </summary>
public class ChromiumResponsesTests
{
    private static string Response(string id, string url, int status) =>
        "{\"requestId\":\"" + id + "\",\"response\":{\"url\":\"" + url + "\",\"status\":" + status
        + ",\"statusText\":\"\",\"headers\":{\"Content-Type\":\"text/html\"}}}";

    // The protocol folds repeated headers into one value, lines apart: the JSON escape is a backslash and an n.
    private static string Extra(string id, int status, params string[] cookies) =>
        "{\"requestId\":\"" + id + "\",\"statusCode\":" + status + ",\"headers\":{\"set-cookie\":\""
        + string.Join("\\n", cookies) + "\"}}";

    private static string Redirected(string id, string from, int status) =>
        "{\"requestId\":\"" + id + "\",\"request\":{\"url\":\"x\"},\"redirectResponse\":{\"url\":\"" + from + "\",\"status\":"
        + status + ",\"statusText\":\"\",\"headers\":{\"Location\":\"/home\"}}}";

    private static string End(string id) => "{\"requestId\":\"" + id + "\"}";

    private static IEnumerable<string> Cookies(SessionResponse? response) =>
        response!.Headers.Where(h => h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);

    [Fact]
    public void A_redirect_and_its_page_are_both_reported_each_with_its_own_cookies()
    {
        var responses = new ChromiumResponses(_ => true, sampleBodies: false);
        Assert.Null(responses.ExtraInfo(Extra("1", 302, "session=abc; Path=/")));
        var redirect = responses.RequestWillBeSent(Redirected("1", "http://a.example/login", 302));
        Assert.Equal(302, redirect!.StatusCode);
        Assert.Equal(["session=abc; Path=/"], Cookies(redirect));

        // The page: its extra info after its response this time, which publishes it then.
        Assert.Null(responses.Response(Response("1", "http://a.example/home", 200)));
        var home = responses.ExtraInfo(Extra("1", 200, "seen=1; Path=/", "theme=dark"));
        Assert.Equal("http://a.example/home", home!.Uri);
        Assert.Equal(["seen=1; Path=/", "theme=dark"], Cookies(home));
        Assert.Empty(responses.Ended(End("1"), out _, out _));   // already reported
    }

    [Fact]
    public void A_redirect_announced_before_its_cookies_waits_for_them()
    {
        // The order measured from CEF: the next request names the redirect's response, and only then its extra info.
        var responses = new ChromiumResponses(_ => true, sampleBodies: false);
        Assert.Null(responses.RequestWillBeSent(Redirected("7", "http://a.example/login", 302)));
        Assert.Equal(["session=abc; Path=/"], Cookies(responses.ExtraInfo(Extra("7", 302, "session=abc; Path=/"))));
    }

    [Fact]
    public void Responses_whose_cookies_never_come_go_out_in_order_when_the_request_ends()
    {
        // Measured: Chromium sent no extra info for the page a redirect led to.
        var responses = new ChromiumResponses(_ => true, sampleBodies: false);
        Assert.Null(responses.RequestWillBeSent(Redirected("8", "http://a.example/login", 302)));
        Assert.Null(responses.Response(Response("8", "http://a.example/home", 200)));
        var ended = responses.Ended(End("8"), out var id, out var final);
        Assert.Equal([302, 200], ended.Select(r => r.StatusCode));
        Assert.Same(ended[1], final);
        Assert.Equal("8", id);
    }

    [Fact]
    public void A_response_whose_cookies_came_first_is_reported_at_once()
    {
        var responses = new ChromiumResponses(_ => true, sampleBodies: false);
        Assert.Null(responses.ExtraInfo(Extra("2", 200, "a=1")));
        Assert.Equal(["a=1"], Cookies(responses.Response(Response("2", "http://a.example/", 200))));
    }

    [Fact]
    public void With_body_samples_the_response_waits_for_its_end_and_keeps_its_cookies()
    {
        var responses = new ChromiumResponses(_ => true, sampleBodies: true);
        Assert.Null(responses.Response(Response("4", "http://a.example/", 200)));
        Assert.Null(responses.ExtraInfo(Extra("4", 200, "b=2")));   // not yet: its body is read at the end
        responses.Ended(End("4"), out _, out var final);
        Assert.Equal(["b=2"], Cookies(final));
    }

    [Fact]
    public void What_the_app_does_not_observe_is_neither_reported_nor_kept()
    {
        var responses = new ChromiumResponses(_ => false, sampleBodies: false);
        Assert.Null(responses.ExtraInfo(Extra("5", 302, "c=3")));
        Assert.Null(responses.RequestWillBeSent(Redirected("5", "http://a.example/login", 302)));
        Assert.Null(responses.Response(Response("5", "http://a.example/", 200)));
        Assert.Empty(responses.Ended(End("5"), out _, out _));
    }

    [Fact]
    public void A_request_that_is_not_a_redirect_reports_nothing_on_its_way_out()
    {
        var responses = new ChromiumResponses(_ => true, sampleBodies: false);
        Assert.Null(responses.RequestWillBeSent("""{"requestId":"6","request":{"url":"http://a.example/"}}"""));
    }
}
