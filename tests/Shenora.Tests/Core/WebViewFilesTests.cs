using Shenora;
using Shenora.Core.WebView;

namespace Shenora.Tests.Core;

/// <summary>
/// Path containment — what stands between a page and the disk. MOVED HERE with the code in D45's re-layering
/// (it was `MediaAccess`, which was never a media concern), and these are pure, which is the point: a
/// security check reachable only through a live webview is a security check nobody runs.
/// </summary>
public class WebViewFilesContainmentTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "shenora-files-root");

    [Fact]
    public void A_file_inside_an_allowed_root_resolves()
    {
        var wanted = Path.Combine(Root, "clip.mp4");
        Assert.Equal(Path.GetFullPath(wanted), WebViewFiles.ResolveContained(wanted, [Root]));
    }

    /// <summary>
    /// The default must serve NOTHING. A middleware wired up before its roots are configured has to refuse,
    /// because the alternative default is the whole filesystem.
    /// </summary>
    [Fact]
    public void With_no_allowed_roots_nothing_resolves_at_all()
    {
        Assert.Null(WebViewFiles.ResolveContained(Path.Combine(Root, "clip.mp4"), []));
    }

    [Fact]
    public void A_file_outside_every_allowed_root_is_refused()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "somewhere-else", "clip.mp4");
        Assert.Null(WebViewFiles.ResolveContained(elsewhere, [Root]));
    }

    /// <summary>
    /// Traversal, refused before the filesystem is consulted. A `..` that happens to resolve back inside the
    /// root would pass a containment test, and allowing it means the URL shape is no longer what is authorised.
    /// </summary>
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("sub/../../secrets.txt")]
    [InlineData(@"sub\..\..\secrets.txt")]
    public void Traversal_segments_are_refused(string relative)
    {
        Assert.Null(WebViewFiles.ResolveContained(Path.Combine(Root, relative), [Root]));
    }

    /// <summary>
    /// ⚠ The one a naive prefix comparison gets wrong. Without the separator appended, `…-evil` passes as a
    /// child of the root — a real defect this kit had to fix in its static serving, and the reason the logic
    /// was generalised here rather than written a third time.
    /// </summary>
    [Fact]
    public void A_sibling_directory_sharing_the_roots_PREFIX_does_not_pass_as_a_child()
    {
        Assert.Null(WebViewFiles.ResolveContained(Path.Combine(Root + "-evil", "clip.mp4"), [Root]));
    }

    [Fact]
    public void Several_roots_are_each_honoured()
    {
        var second = Path.Combine(Path.GetTempPath(), "shenora-files-second");
        var wanted = Path.Combine(second, "clip.mp4");
        Assert.Equal(Path.GetFullPath(wanted), WebViewFiles.ResolveContained(wanted, [Root, second]));
    }

    /// <summary>A malformed ROOT disqualifies itself; it must not disqualify the whole request.</summary>
    [Fact]
    public void One_unusable_root_does_not_block_a_good_one()
    {
        var wanted = Path.Combine(Root, "clip.mp4");
        Assert.Equal(Path.GetFullPath(wanted), WebViewFiles.ResolveContained(wanted, ["", Root]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_path_is_refused_rather_than_defaulted(string? requested)
    {
        Assert.Null(WebViewFiles.ResolveContained(requested, [Root]));
    }
}

/// <summary>
/// The range responses a player needs, and above all the per-platform body rule measured on devices (D44).
/// The test that matters most is the one asserting the two deliveries genuinely DIFFER — because unifying
/// them looks like a tidy-up and breaks every file whose index sits at the end.
/// </summary>
public class WebViewFilesServeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shenora-serve-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _file;
    private readonly byte[] _bytes;

    public WebViewFilesServeTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "clip.mp4");
        // byte i == i % 251, so a wrong offset shows as a wrong first byte rather than a plausible buffer.
        _bytes = new byte[1000];
        for (var i = 0; i < _bytes.Length; i++) _bytes[i] = (byte)(i % 251);
        File.WriteAllBytes(_file, _bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp dir outliving a test is harmless */ }
        GC.SuppressFinalize(this);
    }

    private static WebViewResourceRequest Request(string? range) => new()
    {
        Uri = new Uri("app://0.0.0.1/media?x"),
        Method = "GET",
        Headers = range is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Range"] = range },
    };

    private static byte[] Body(WebViewResourceResponse response)
    {
        using var ms = new MemoryStream();
        response.Content.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void No_range_serves_the_whole_file_and_ADVERTISES_that_ranges_are_possible()
    {
        var r = WebViewFiles.Serve(Request(null), _file, "video/mp4", WebViewRangeDelivery.Sliced);

        Assert.Equal(200, r.StatusCode);
        Assert.Equal(_bytes, Body(r));
        // Without Accept-Ranges a player will not even ATTEMPT a seek — indistinguishable from broken seeking.
        Assert.Equal("bytes", r.Headers["Accept-Ranges"]);
        Assert.Equal("1000", r.Headers["Content-Length"]);
    }

    [Fact]
    public void Sliced_delivery_returns_EXACTLY_the_requested_window()
    {
        var r = WebViewFiles.Serve(Request("bytes=100-199"), _file, "video/mp4", WebViewRangeDelivery.Sliced);

        Assert.Equal(206, r.StatusCode);
        Assert.Equal("bytes 100-199/1000", r.Headers["Content-Range"]);
        Assert.Equal("100", r.Headers["Content-Length"]);
        var body = Body(r);
        Assert.Equal(100, body.Length);
        Assert.Equal(_bytes[100], body[0]);   // the OFFSET, not merely the length
    }

    /// <summary>
    /// The Android rule: the platform applies the range start to whatever body it receives, so the handler
    /// hands over the WHOLE file and the headers describe from→EOF, which is what the client really gets.
    /// </summary>
    [Fact]
    public void Unsliced_delivery_returns_the_WHOLE_file_with_headers_describing_from_to_EOF()
    {
        var r = WebViewFiles.Serve(Request("bytes=100-199"), _file, "video/mp4", WebViewRangeDelivery.Unsliced);

        Assert.Equal(206, r.StatusCode);
        Assert.Equal("bytes 100-999/1000", r.Headers["Content-Range"]);
        Assert.Equal("900", r.Headers["Content-Length"]);
        var body = Body(r);
        Assert.Equal(1000, body.Length);
        Assert.Equal(_bytes[0], body[0]);     // from offset 0 — the platform does the skipping
    }

    /// <summary>
    /// The two deliveries must genuinely DIFFER. If someone unifies them this is what fails — and it earns
    /// its own case because the field symptom is subtle: faststart files keep working.
    /// </summary>
    [Fact]
    public void The_two_deliveries_are_not_the_same_response()
    {
        var sliced = WebViewFiles.Serve(Request("bytes=500-599"), _file, "video/mp4", WebViewRangeDelivery.Sliced);
        var unsliced = WebViewFiles.Serve(Request("bytes=500-599"), _file, "video/mp4", WebViewRangeDelivery.Unsliced);

        Assert.NotEqual(Body(sliced).Length, Body(unsliced).Length);
        Assert.NotEqual(sliced.Headers["Content-Range"], unsliced.Headers["Content-Range"]);
    }

    /// <summary>Open-ended is what a media element sends when it seeks, and what a moov-at-end file needs.</summary>
    [Fact]
    public void An_open_ended_range_runs_to_the_end_of_the_file()
    {
        var r = WebViewFiles.Serve(Request("bytes=900-"), _file, "video/mp4", WebViewRangeDelivery.Sliced);

        Assert.Equal(206, r.StatusCode);
        Assert.Equal("bytes 900-999/1000", r.Headers["Content-Range"]);
        // ONE read: the body is consume-once, so a second Body() returns empty and fails with an index error
        // that says nothing about ranges. Learned by writing it the other way.
        var body = Body(r);
        Assert.Equal(100, body.Length);
        Assert.Equal(_bytes[900], body[0]);
    }

    [Fact]
    public void A_range_past_the_end_is_416_and_reports_the_real_size()
    {
        var r = WebViewFiles.Serve(Request("bytes=5000-"), _file, "video/mp4", WebViewRangeDelivery.Sliced);

        Assert.Equal(416, r.StatusCode);
        Assert.Equal("bytes */1000", r.Headers["Content-Range"]);
    }

    /// <summary>A suffix range means the LAST n bytes — the form hand-rolled parsers read as "from n".</summary>
    [Fact]
    public void A_suffix_range_serves_the_LAST_bytes()
    {
        var r = WebViewFiles.Serve(Request("bytes=-10"), _file, "video/mp4", WebViewRangeDelivery.Sliced);

        Assert.Equal(206, r.StatusCode);
        Assert.Equal("bytes 990-999/1000", r.Headers["Content-Range"]);
        Assert.Equal(_bytes[990], Body(r)[0]);
    }

    [Fact]
    public void A_missing_file_is_a_fixed_404_that_leaks_nothing()
    {
        var r = WebViewFiles.Serve(Request(null), Path.Combine(_dir, "absent.mp4"),
            "video/mp4", WebViewRangeDelivery.Sliced);

        Assert.Equal(404, r.StatusCode);
        var body = System.Text.Encoding.UTF8.GetString(Body(r));
        Assert.Equal("Not Found", body);
        Assert.DoesNotContain(_dir, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serve_refuses_missing_arguments_rather_than_guessing()
    {
        Assert.Throws<ArgumentNullException>(
            () => WebViewFiles.Serve(null!, _file, "video/mp4", WebViewRangeDelivery.Sliced));
        Assert.Throws<ArgumentException>(
            () => WebViewFiles.Serve(Request(null), " ", "video/mp4", WebViewRangeDelivery.Sliced));
        Assert.Throws<ArgumentException>(
            () => WebViewFiles.Serve(Request(null), _file, " ", WebViewRangeDelivery.Sliced));
    }

    /// 🔴 Serving a file no longer allocates its window. Under Android's Unsliced rule the window IS the
    /// whole file, so `new byte[count]` meant every response allocated the entire file — measured on this
    /// repo's own 475 KB sample clip, and unbounded for a real film.
    /// <para>
    /// Asserted on the STREAM's own shape rather than process memory, which is not deterministic in a
    /// test: the body is a <see cref="BoundedBodyStream"/> (never a <c>MemoryStream</c>), it reports the
    /// whole file's length up front without having read a byte of it, and a small read advances
    /// <c>Position</c> by exactly what was asked for — none of which is true of a body that was already
    /// fully materialised before this method ever saw it.
    /// </para>
    [Fact]
    public void Serving_a_file_does_not_materialise_it()
    {
        // Larger than any sane read buffer (a few KB to a few hundred KB), so a pre-loaded body of this
        // size would be an obvious upfront allocation rather than a coincidence of a small fixture.
        var bigPath = Path.Combine(_dir, "big.bin");
        var bigBytes = new byte[2 * 1024 * 1024];
        new Random(Seed: 1).NextBytes(bigBytes);
        File.WriteAllBytes(bigPath, bigBytes);

        var r = WebViewFiles.Serve(Request(null), bigPath, "video/mp4", WebViewRangeDelivery.Sliced);
        try
        {
            Assert.Equal(200, r.StatusCode);
            // The regression this pins: the old body was ALWAYS a MemoryStream, allocated in full before
            // Serve even returned. This one is a lazy window over the still-open file.
            Assert.IsType<BoundedBodyStream>(r.Content);
            Assert.Equal(bigBytes.LongLength, r.Content.Length);   // known up front — nothing was read yet
            Assert.Equal(0, r.Content.Position);

            var head = new byte[64];
            Assert.Equal(64, r.Content.Read(head, 0, 64));
            Assert.Equal(bigBytes[..64], head);
            Assert.Equal(64, r.Content.Position);   // advanced by exactly what was read, not by the file
        }
        finally
        {
            r.Content.Dispose();
        }
    }
}

/// <summary>
/// The content-type map. It moved to Core with D45, and MEDIA TYPES WERE ADDED — their absence was the point:
/// the map was built for bundle assets and would have answered octet-stream for an mp4, which makes a media
/// element refuse before it has tried.
/// </summary>
public class WebViewContentTypesTests
{
    [Theory]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.webm", "video/webm")]
    [InlineData("a.mkv", "video/x-matroska")]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.flac", "audio/flac")]
    [InlineData("a.wav", "audio/wav")]
    public void Media_extensions_are_named_rather_than_falling_back_to_octet_stream(string file, string expected)
    {
        Assert.Equal(expected, WebViewContentTypes.FromPath(file));
    }

    [Fact]
    public void The_bundle_types_it_already_had_are_unchanged()
    {
        Assert.Equal("text/html", WebViewContentTypes.FromPath("index.html"));
        Assert.Equal("application/javascript", WebViewContentTypes.FromPath("app.mjs"));
        Assert.Equal("image/webp", WebViewContentTypes.FromPath("a.WEBP"));   // case-insensitive
    }

    [Fact]
    public void An_unknown_extension_still_falls_back()
    {
        Assert.Equal("application/octet-stream", WebViewContentTypes.FromPath("a.qqq"));
    }

    /// <summary>
    /// The bundle caching policy, which the FILE middleware deliberately does not apply: a file from disk is
    /// not content-hashed, so `immutable` would pin a stale copy after the user replaces it.
    /// </summary>
    [Fact]
    public void Html_is_no_cache_and_hashed_assets_are_immutable()
    {
        Assert.Equal("no-cache", WebViewContentTypes.CacheControlFromPath("index.html"));
        Assert.Contains("immutable", WebViewContentTypes.CacheControlFromPath("app.abc123.js"));
    }
}
