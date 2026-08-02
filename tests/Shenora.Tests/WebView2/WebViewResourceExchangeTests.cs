using Shenora.Windows;

namespace Shenora.Tests.WebView2;

/// <summary>
/// P6.6 found that a deferred-scheme handler could only ever answer "200, here are all the bytes":
/// it never saw a request header, so <c>Range</c> was invisible and nothing it served could be
/// SOUGHT, and it returned a <c>byte[]</c>, so a large file had to be materialised whole. One of the
/// surveyed apps had bypassed the seam entirely for exactly that.
///
/// The parser is where this feature is actually won or lost — each of the three legal forms is its
/// own chance to be wrong, and the suffix form is the one hand-rolled versions reverse.
/// </summary>
public class WebViewResourceExchangeTests
{
    [Theory]
    // closed range
    [InlineData("bytes=0-499", 1000, 0, 499)]
    [InlineData("bytes=500-999", 1000, 500, 999)]
    // open-ended — what a media element actually sends when it seeks
    [InlineData("bytes=500-", 1000, 500, 999)]
    [InlineData("bytes=0-", 1000, 0, 999)]
    // SUFFIX: the LAST n bytes, not "from n". The one that gets written backwards.
    [InlineData("bytes=-500", 1000, 500, 999)]
    [InlineData("bytes=-1", 1000, 999, 999)]
    // a suffix longer than the resource is the whole resource, not a negative offset
    [InlineData("bytes=-5000", 1000, 0, 999)]
    // the END clamps to the resource
    [InlineData("bytes=900-5000", 1000, 900, 999)]
    // whitespace and casing are legal
    [InlineData("  BYTES=0-9  ", 1000, 0, 9)]
    public void The_three_legal_range_forms_all_resolve(string header, long total, long from, long to)
    {
        Assert.True(WebViewByteRange.TryParse(header, total, out var range));
        Assert.Equal(from, range.From);
        Assert.Equal(to, range.To);
        Assert.Equal(to - from + 1, range.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("items=0-10")]      // not the bytes unit
    [InlineData("bytes=abc-def")]   // malformed
    [InlineData("bytes=10")]        // no dash
    [InlineData("bytes=20-10")]     // inverted
    [InlineData("bytes=0-99,200-299")] // multi-range: declined honestly rather than half-served
    [InlineData("bytes=-0")]        // a zero-length suffix means nothing
    public void Anything_it_cannot_honour_declines_so_the_caller_serves_the_whole_resource(string? header)
    {
        Assert.False(WebViewByteRange.TryParse(header, 1000, out _));
    }

    [Fact]
    public void A_start_past_the_end_is_reported_unsatisfiable_rather_than_clamped()
    {
        // Clamping the START would silently serve the wrong bytes — a player would get data it did
        // not ask for and no error. The spec's answer is 416, and it needs the real length so the
        // client can retry; omitting that leaves a player retrying the same bad range forever.
        Assert.True(WebViewByteRange.TryParse("bytes=5000-", 1000, out var range));
        Assert.False(range.IsSatisfiable(1000));

        var response = WebViewResourceResponse.RangeNotSatisfiable(1000);
        Assert.Equal(416, response.StatusCode);
        Assert.Equal("bytes */1000", response.Headers["Content-Range"]);
    }

    [Fact]
    public void A_partial_response_carries_the_status_and_the_Content_Range_that_go_with_it()
    {
        var response = WebViewResourceResponse.PartialContent(
            new MemoryStream(new byte[500]), "video/mp4", new WebViewByteRange(500, 999), 1000);

        Assert.Equal(206, response.StatusCode);
        Assert.Equal("Partial Content", response.ReasonPhrase);
        Assert.Equal("bytes 500-999/1000", response.Headers["Content-Range"]);
        Assert.Equal("video/mp4", response.Headers["Content-Type"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
    }

    [Fact]
    public void A_complete_response_advertises_that_it_accepts_ranges()
    {
        // Without Accept-Ranges a media element will not even TRY to seek, which looks exactly like
        // "seeking is broken" while the handler is perfectly capable of serving the range.
        var response = WebViewResourceResponse.Bytes([1, 2, 3], "video/mp4");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
    }

    [Fact]
    public void A_handler_supplied_header_is_not_overwritten()
    {
        var response = WebViewResourceResponse.Bytes([], "text/plain",
            new Dictionary<string, string> { ["Accept-Ranges"] = "none", ["X-App"] = "yes" });

        Assert.Equal("none", response.Headers["Accept-Ranges"]);
        Assert.Equal("yes", response.Headers["X-App"]);
    }

    [Fact]
    public void Headers_are_case_insensitive_on_both_sides_because_HTTP_is()
    {
        var request = new WebViewResourceRequest
        {
            Uri = new Uri("app://x/y"),
            Method = "GET",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Range"] = "bytes=0-9" },
        };

        Assert.Equal("bytes=0-9", request.GetHeader("range"));
        Assert.Equal("bytes=0-9", request.GetHeader("RANGE"));
        Assert.Null(request.GetHeader("If-None-Match"));

        Assert.Equal("text/plain", WebViewResourceResponse.Bytes([], "text/plain").Headers["CONTENT-TYPE"]);
    }

    [Fact]
    public void NotFound_carries_the_one_fixed_body_and_no_diagnosis()
    {
        // Design §5: an app scheme handler's failure detail is the most likely of all of these to
        // carry a real path or a remote URL, and page script can read a response body. One constant
        // body for every 404 (P5.5 H3), with the diagnosis going to the host log instead.
        var response = WebViewResourceResponse.NotFound();
        using var reader = new StreamReader(response.Content);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Not Found", reader.ReadToEnd());
    }
}
