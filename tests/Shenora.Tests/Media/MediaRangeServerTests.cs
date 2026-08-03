using Shenora.Core;
using Shenora.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The range responses a player actually needs, and above all the PER-PLATFORM body rule that was measured
/// on devices (D44). The tests worth having are the ones that fail if someone "tidies" the unsliced mode
/// into ordinary slicing — because that tidy-up plays every faststart file perfectly and breaks every file
/// whose index sits at the end.
/// </summary>
public class MediaRangeServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shenora-range-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _file;
    private readonly byte[] _bytes;

    public MediaRangeServerTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "clip.mp4");
        // Distinguishable content: byte i == i % 251, so any wrong offset shows up as a wrong first byte
        // rather than as a plausible-looking buffer.
        _bytes = new byte[1000];
        for (var i = 0; i < _bytes.Length; i++) _bytes[i] = (byte)(i % 251);
        File.WriteAllBytes(_file, _bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir that outlives a test is harmless */ }
        GC.SuppressFinalize(this);
    }

    private static WebViewResourceRequest Request(string? range) => new()
    {
        Uri = new Uri("app://media/?src=clip.mp4"),
        Method = "GET",
        Headers = range is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Range"] = range },
    };

    private static MediaServingOptions Mode(MediaBodyMode mode) => new() { BodyMode = mode };

    private static byte[] Body(WebViewResourceResponse response)
    {
        using var ms = new MemoryStream();
        response.Content.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void No_range_serves_the_whole_file_and_ADVERTISES_that_ranges_are_possible()
    {
        var response = MediaRangeServer.Serve(Request(null), _file, "video/mp4", Mode(MediaBodyMode.Sliced));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(_bytes, Body(response));
        // Without Accept-Ranges a player will not even ATTEMPT a seek — indistinguishable from seeking
        // being broken while the handler is perfectly capable.
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.Equal("1000", response.Headers["Content-Length"]);
    }

    [Fact]
    public void Sliced_mode_returns_EXACTLY_the_requested_window()
    {
        var response = MediaRangeServer.Serve(Request("bytes=100-199"), _file, "video/mp4", Mode(MediaBodyMode.Sliced));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal("bytes 100-199/1000", response.Headers["Content-Range"]);
        Assert.Equal("100", response.Headers["Content-Length"]);

        var body = Body(response);
        Assert.Equal(100, body.Length);
        Assert.Equal(_bytes[100], body[0]);   // the OFFSET, not just the length
    }

    /// <summary>
    /// ⚠ The Android rule, and the reason <see cref="MediaBodyMode"/> exists: the platform applies the range
    /// start to whatever body it receives, so the handler must hand over the WHOLE file. The headers then
    /// describe from→EOF, because that is what the client will really receive after the platform skips.
    /// </summary>
    [Fact]
    public void Unsliced_mode_returns_the_WHOLE_file_with_headers_describing_from_to_EOF()
    {
        var response = MediaRangeServer.Serve(Request("bytes=100-199"), _file, "video/mp4", Mode(MediaBodyMode.Unsliced));

        Assert.Equal(206, response.StatusCode);
        // NOT 100-199: the platform truncates the front itself and streams the rest, so a header claiming
        // the requested END would be the inaccurate one.
        Assert.Equal("bytes 100-999/1000", response.Headers["Content-Range"]);
        Assert.Equal("900", response.Headers["Content-Length"]);

        var body = Body(response);
        Assert.Equal(1000, body.Length);
        Assert.Equal(_bytes[0], body[0]);     // from offset 0 — the platform does the skipping
    }

    /// <summary>
    /// The two modes must genuinely DIFFER. If someone unifies them, this is the test that fails — and it
    /// is worth its own case because the symptom in the field is subtle: faststart files keep working.
    /// </summary>
    [Fact]
    public void The_two_body_modes_are_not_the_same_response()
    {
        var sliced = MediaRangeServer.Serve(Request("bytes=500-599"), _file, "video/mp4", Mode(MediaBodyMode.Sliced));
        var unsliced = MediaRangeServer.Serve(Request("bytes=500-599"), _file, "video/mp4", Mode(MediaBodyMode.Unsliced));

        Assert.NotEqual(Body(sliced).Length, Body(unsliced).Length);
        Assert.NotEqual(sliced.Headers["Content-Range"], unsliced.Headers["Content-Range"]);
    }

    /// <summary>
    /// An open-ended range is what a media element actually sends when it seeks, and the tail request is
    /// what a moov-at-end file needs before it can open at all.
    /// </summary>
    [Fact]
    public void An_open_ended_range_runs_to_the_end_of_the_file()
    {
        var response = MediaRangeServer.Serve(Request("bytes=900-"), _file, "video/mp4", Mode(MediaBodyMode.Sliced));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal("bytes 900-999/1000", response.Headers["Content-Range"]);
        // ONE read. The response body is consume-once — its own XML says ownership passes to the webview,
        // which reads it after the handler returns — so a second Body() here returns an empty array and the
        // assertion fails with an index error that says nothing about ranges. Caught by this test failing.
        var body = Body(response);
        Assert.Equal(100, body.Length);
        Assert.Equal(_bytes[900], body[0]);
    }

    /// <summary>
    /// A start past the end is UNSATISFIABLE, not clamped — clamping serves bytes nobody asked for with no
    /// error — and the 416 must carry the real size or a player retries the same bad range forever.
    /// </summary>
    [Fact]
    public void A_range_past_the_end_is_416_and_reports_the_real_size()
    {
        var response = MediaRangeServer.Serve(Request("bytes=5000-"), _file, "video/mp4", Mode(MediaBodyMode.Sliced));

        Assert.Equal(416, response.StatusCode);
        Assert.Equal("bytes */1000", response.Headers["Content-Range"]);
    }

    /// <summary>A suffix range means the LAST n bytes — the form hand-rolled parsers read as "from n".</summary>
    [Fact]
    public void A_suffix_range_serves_the_LAST_bytes()
    {
        var response = MediaRangeServer.Serve(Request("bytes=-10"), _file, "video/mp4", Mode(MediaBodyMode.Sliced));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal("bytes 990-999/1000", response.Headers["Content-Range"]);
        Assert.Equal(_bytes[990], Body(response)[0]);
    }

    /// <summary>
    /// A missing file answers the kit's ONE fixed 404 body. Page script can read a response body, and a
    /// media handler's failure detail is the likeliest of all of them to carry a real filesystem path.
    /// </summary>
    [Fact]
    public void A_missing_file_is_a_fixed_404_that_leaks_nothing()
    {
        var response = MediaRangeServer.Serve(Request(null), Path.Combine(_dir, "absent.mp4"),
            "video/mp4", Mode(MediaBodyMode.Sliced));

        Assert.Equal(404, response.StatusCode);
        var body = System.Text.Encoding.UTF8.GetString(Body(response));
        Assert.Equal("Not Found", body);
        Assert.DoesNotContain(_dir, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serve_refuses_missing_arguments_rather_than_guessing()
    {
        var options = Mode(MediaBodyMode.Sliced);
        Assert.Throws<ArgumentNullException>(() => MediaRangeServer.Serve(null!, _file, "video/mp4", options));
        Assert.Throws<ArgumentException>(() => MediaRangeServer.Serve(Request(null), " ", "video/mp4", options));
        Assert.Throws<ArgumentException>(() => MediaRangeServer.Serve(Request(null), _file, " ", options));
        Assert.Throws<ArgumentNullException>(() => MediaRangeServer.Serve(Request(null), _file, "video/mp4", null!));
    }
}
