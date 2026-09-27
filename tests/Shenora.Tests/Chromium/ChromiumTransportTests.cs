using System.Text.Json;
using Shenora.Chromium;

namespace Shenora.Tests.Chromium;

/// <summary>
/// What the Chromium shell writes into a page: the marker, first in the head, and the push call. Both are
/// script the page RUNS, so the cases that matter are the ones that could break out of it.
/// </summary>
public class ChromiumTransportTests
{
    [Fact]
    public void The_marker_is_the_first_thing_in_the_head()
    {
        var html = ChromiumTransport.MarkHtml("<html><head><script src=app.js></script></head><body></body></html>");

        var marker = html.IndexOf(ChromiumTransport.HostGlobal, StringComparison.Ordinal);
        Assert.True(marker > html.IndexOf("<head>", StringComparison.Ordinal));
        Assert.True(marker < html.IndexOf("app.js", StringComparison.Ordinal), "the marker must exist before the page's own script runs");
    }

    [Theory]
    [InlineData("<HTML><HEAD lang=en>x</HEAD></HTML>", "<HEAD lang=en>")]
    [InlineData("<html><head\n>x</head></html>", "<head\n>")]
    [InlineData("<html><header>h</header><head>x</head></html>", "<head>")]
    public void The_marker_lands_inside_the_real_head_tag(string document, string headTag)
    {
        var html = ChromiumTransport.MarkHtml(document);

        var tag = html.IndexOf(headTag, StringComparison.Ordinal);
        Assert.Equal(tag + headTag.Length, html.IndexOf("<script>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_document_with_no_head_is_marked_at_its_start()
    {
        Assert.StartsWith("<script>window.__shenora_chromium=", ChromiumTransport.MarkHtml("<p>fragment</p>"));
    }

    [Fact]
    public void The_marker_names_the_route_as_JSON_and_cannot_close_its_own_script()
    {
        var script = ChromiumTransport.MarkerScript("/a</script><b>");

        // One closing tag: the script's own. The path's `</script>` is escaped by the serializer.
        Assert.Equal(1, CountOf(script, "</script>"));
        var json = script["<script>window.__shenora_chromium=".Length..^";</script>".Length];
        Assert.Equal("/a</script><b>", JsonDocument.Parse(json).RootElement.GetProperty(ChromiumTransport.IpcMember).GetString());
    }

    [Theory]
    [InlineData("{\"id\":\"1\"}")]
    [InlineData("quote \" and backslash \\ and ') and </script>")]
    public void A_pushed_message_travels_as_one_string_literal(string message)
    {
        var script = ChromiumTransport.PushScript(message);

        const string call = "window.__shenora_chromium.receive(";
        var start = script.LastIndexOf(call, StringComparison.Ordinal) + call.Length;
        var literal = script[start..^");".Length];
        // It parses back as ONE JSON string equal to the message: nothing in it escaped the argument.
        Assert.Equal(message, JsonSerializer.Deserialize<string>(literal));
    }

    [Fact]
    public void Line_separators_leave_as_escapes_never_raw()
    {
        // Built at run time: a raw U+2028 in this SOURCE file is a line break to C# itself.
        var message = $"a{(char)0x2028}b{(char)0x2029}c";

        var script = ChromiumTransport.PushScript(message);

        Assert.DoesNotContain((char)0x2028, script);   // a raw one ends a line in older JS parsers
        Assert.DoesNotContain((char)0x2029, script);
        var start = script.LastIndexOf('(') + 1;
        Assert.Equal(message, JsonSerializer.Deserialize<string>(script[start..^");".Length]));
    }

    [Fact]
    public void A_push_is_a_no_op_on_a_page_that_never_created_the_transport()
    {
        // Guarded on both the global and its receive, so an unmarked or not-yet-ready page throws nothing.
        Assert.StartsWith("window.__shenora_chromium&&window.__shenora_chromium.receive&&", ChromiumTransport.PushScript("x"));
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
