using Shenora.Core.WebView;
using Shenora.Modules.Media;

// ⚠ THE FIXTURE BUILDER IS `Mp4RemuxerTests`', NOT A COPY OF IT. Both files assert about the same remuxer
// over the same source shapes, and the claim below is that a PLAN and a WRITE of one file agree — which a
// second builder could break invisibly, by making them two files that merely look alike.
using static Shenora.Tests.Media.Mp4RemuxerTests;

namespace Shenora.Tests.Media;

/// <summary>
/// The remuxer describing the file it WOULD write, without writing it.
///
/// <para>
/// 🔴 <b>Why this matters more than a usual unit test.</b> The layout is what lets a container repair be
/// served as one ordinary <c>&lt;video src&gt;</c>: the total length is stated up front so a 206 can carry a
/// real <c>Content-Range</c>, and every range maps back to source bytes so a seek to the end is serviceable
/// cold (D71). All of that rests on the plan being EXACTLY what the writer produces. A one-byte
/// disagreement makes the route advertise a total the bytes do not honour, and a media element's failure for
/// that is SILENT — a blank picture, no error, nothing in the console.
/// </para>
///
/// <para>
/// So the assertions here are the spec, not a formality, and they are made over several source SHAPES on
/// purpose. The header's length varies with the tables inside it — <c>stss</c> is omitted when every frame
/// is a keyframe, <c>ctts</c> and <c>elst</c> appear only with reordering, and <c>stsc</c>/<c>co64</c> grow
/// with the chunk count — and it is exactly a table whose size changes between the planning pass and the
/// writing pass that would corrupt every chunk offset after it.
/// </para>
/// </summary>
public class Mp4LayoutTests
{
    /// <summary>
    /// The shapes both theories run over. Each one moves a DIFFERENT part of the header's size, so a plan
    /// that happened to agree on the simple case still has to agree on the rest.
    /// </summary>
    private static MemoryStream Source(string shape) => shape switch
    {
        // The ordinary film: picture and sound interleaved across two clusters, so each track gets two
        // chunks and the two are addressed alternately in `mdat`.
        "video+audio" => Mkv(
            Info(120_000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 300)), SimpleBlock(2, 0, true, Frame(10, 50))),
            Cluster(40, SimpleBlock(1, 0, false, Frame(1, 200)), SimpleBlock(2, 0, true, Frame(11, 50)))),

        // Every frame a keyframe, so `stss` is OMITTED entirely — the header is shorter than the shape below
        // by a whole box, which is precisely the kind of difference a plan must not get wrong.
        "all-keyframes" => Mkv(
            Info(1000),
            [VideoTrack(config: AvcConfig)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 64)), SimpleBlock(1, 40, true, Frame(1, 64)))),

        // Two of four frames are keyframes, so `stss` IS written, and with two entries.
        "sparse-keyframes" => Mkv(
            Info(1000),
            [VideoTrack(config: AvcConfig)],
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(0, 64)),
                SimpleBlock(1, 40, false, Frame(1, 64)),
                SimpleBlock(1, 80, false, Frame(2, 64)),
                SimpleBlock(1, 120, true, Frame(3, 64)))),

        // B-frames — stored I P B B, shown 0 40 80 120. The output needs a composition table AND an edit
        // list, so this shape carries two boxes the others do not.
        "b-frames" => Mkv(
            Info(160),
            [VideoTrack(config: AvcConfig)],
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(0, 64)),
                SimpleBlock(1, 120, false, Frame(1, 64)),
                SimpleBlock(1, 40, false, Frame(2, 64)),
                SimpleBlock(1, 80, false, Frame(3, 64)))),

        // Three AAC frames behind ONE block header (fixed-size lacing), sharing one timestamp. The sample
        // count the plan sees has to be the laced count, not the block count.
        "laced-audio" => Mkv(
            Info(1000),
            [AudioTrack(config: AacConfig)],
            Cluster(0, El(0xA3, [0x82], [0x00, 0x00], [0x84], [0x02],
                [.. Frame(30, 40), .. Frame(31, 40), .. Frame(32, 40)]))),

        // ⚠ A timestamp scale that does NOT divide a second (3 ns), which sends `Resolve` down its
        // rescale-to-milliseconds branch instead of the exact one every ordinary file takes — a different
        // timescale, different durations, and therefore a differently sized `stts`.
        "odd-timescale" => Mkv(
            Info(1000, scale: 3),
            [VideoTrack(config: AvcConfig)],
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(0, 64)),
                SimpleBlock(1, 40, false, Frame(1, 96)),
                SimpleBlock(1, 80, false, Frame(2, 48)))),

        // Five clusters, both tracks in each: ten chunks, so `stsc` and `co64` are the biggest they get here
        // and the media start moves furthest from the simple case.
        "many-chunks" => Mkv(
            Info(5000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
            [.. Enumerable.Range(0, 5).Select(i => Cluster(
                (ulong)(i * 40),
                SimpleBlock(1, 0, i == 0, Frame(i, 100 + i * 7)),
                SimpleBlock(2, 0, true, Frame(20 + i, 40 + i))))]),

        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "unknown fixture shape"),
    };

    // ── the claim ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 THE WHOLE CLAIM IN ONE ASSERTION: what <c>Plan</c> says the output will be must equal what
    /// <c>Write</c> actually produces. If these ever disagree the delivery serves a <c>Content-Range</c>
    /// total that the bytes do not honour, and a media element's failure for that is silent.
    /// <para>
    /// ⚠ A one-byte disagreement means the planned header differs from the written one — diff the two
    /// headers (the theory below does exactly that), and never adjust <c>TotalLength</c> to compensate.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("video+audio")]
    [InlineData("all-keyframes")]
    [InlineData("sparse-keyframes")]
    [InlineData("b-frames")]
    [InlineData("laced-audio")]
    [InlineData("odd-timescale")]
    [InlineData("many-chunks")]
    public void The_planned_length_equals_what_the_remuxer_actually_writes(string shape)
    {
        using var source = Source(shape);
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var produced = new MemoryStream();
        var result = new Mp4Remuxer().Write(source, produced, conversion: null);

        Assert.True(result.Succeeded, result.Reason);
        Assert.Equal(produced.Length, layout!.TotalLength);
    }

    /// <summary>
    /// 🔴 <b>And the stronger claim the length alone cannot make: the layout REBUILDS the file.</b> Header
    /// bytes verbatim, then every sample copied from the source offset the span names into the output offset
    /// it names — which is precisely what a range route will do, one range at a time.
    /// <para>
    /// ⚠ The length test above can pass while the provenance is wrong: swap two samples, or shift every
    /// chunk offset by the same amount, and the file is still the right SIZE and decodes garbage. This is
    /// the assertion that catches it, and it fails naming the first byte that differs.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("video+audio")]
    [InlineData("all-keyframes")]
    [InlineData("sparse-keyframes")]
    [InlineData("b-frames")]
    [InlineData("laced-audio")]
    [InlineData("odd-timescale")]
    [InlineData("many-chunks")]
    public void The_layout_rebuilds_the_written_file_byte_for_byte(string shape)
    {
        using var source = Source(shape);
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var produced = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, produced, conversion: null).Succeeded);
        var written = produced.ToArray();

        var sourceBytes = source.ToArray();
        var rebuilt = new byte[layout!.TotalLength];
        layout.Header.Span.CopyTo(rebuilt);
        foreach (var span in layout.Samples)
        {
            sourceBytes.AsSpan((int)span.SourceOffset, span.Length).CopyTo(rebuilt.AsSpan((int)span.OutputOffset));
        }

        // The header first, so a header mismatch reports as one rather than as "these two large arrays
        // differ" — a plan that got the tables wrong is a different bug from one that got the offsets wrong.
        Assert.Equal(written[..layout.Header.Length], layout.Header.ToArray());
        Assert.Equal(written, rebuilt);
    }

    /// <summary>
    /// 🔴 <b>A PLANNED SOURCE WRITES THE SAME BYTES WITH A CONVERSION SUPPLIED, and this is a tripwire on the
    /// refusal rule rather than a curiosity.</b> <c>Plan</c> always prepares with <c>conversion: null</c>,
    /// but the writer a route configures has the device's codecs in it — so if a plannable source could ever
    /// be affected by a converter, the plan would describe one file and the route would serve another.
    /// <para>
    /// It cannot, and the reason is exactly the WIDE refusal rule: a source is only plannable when every
    /// stream is carried untouched, and a carriable stream is never offered to a converter. ⚠ Narrow the rule
    /// to "refuse only what needs re-encoding" and this test goes red on the first H.264 + AC-3 film — planned
    /// at video-only length, written longer once the AC-3 became AAC. That is the silent <c>Content-Range</c>
    /// failure the whole layout exists to prevent, which is why the rule needs a test and not a paragraph.
    /// </para>
    /// <para>
    /// The stand-in accepts EVERY codec it is asked about, so "the converter was never consulted" is proven
    /// rather than arranged (D63: an unconsulted seam is indistinguishable from a working one).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("video+audio")]
    [InlineData("all-keyframes")]
    [InlineData("sparse-keyframes")]
    [InlineData("b-frames")]
    [InlineData("laced-audio")]
    [InlineData("odd-timescale")]
    [InlineData("many-chunks")]
    public void A_planned_source_writes_the_SAME_bytes_even_with_a_conversion_supplied(string shape)
    {
        using var source = Source(shape);
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var copied = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, copied, conversion: null).Succeeded);

        source.Position = 0;
        using var withCodecs = new MemoryStream();
        var greedy = new GreedyConversion();
        Assert.True(new Mp4Remuxer().Write(source, withCodecs, greedy).Succeeded);

        Assert.False(greedy.Began, "a carriable stream must never be handed to a converter");
        Assert.Equal(copied.ToArray(), withCodecs.ToArray());
        Assert.Equal(withCodecs.Length, layout!.TotalLength);
    }

    /// <summary>Says yes to every codec, and records whether anything actually asked it to run.</summary>
    private sealed class GreedyConversion : IMediaStreamConversion, IMediaStreamConversionRun
    {
        public bool Began { get; private set; }
        public ReadOnlyMemory<byte> OutputConfig => AacConfig;
        public int OutputFramesPerPacket => 1024;
        public MediaStreamInfo OutputFormat => new(MediaStreamKind.Audio, "aac", Channels: 2, SampleRate: 48000);

        public bool CanConvert(MediaStreamKind kind, string codec) => true;

        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
        {
            Began = true;
            return this;
        }

        public IReadOnlyList<MediaFrame> Push(MediaFrame frame) => [frame with { Data = frame.Data.ToArray() }];
        public IReadOnlyList<MediaFrame> Drain() => [];
        public void Dispose() { }
    }

    /// <summary>
    /// 🔴 <b>The token is OBSERVED INSIDE THE WALK, not merely accepted.</b> A plan of a real film reads every
    /// cluster in a multi-gigabyte file, and Task 5 runs it inside a web request on a phone — so an abandoned
    /// request has to stop costing disk and a thread. A `CancellationToken` parameter nothing consults is
    /// D63's exact failure mode: absent is indistinguishable from working.
    /// <para>
    /// ⚠ <b>The token starts UNCANCELLED and is cancelled by the source's first read</b>, on purpose. A
    /// pre-cancelled token would also be caught by a check at the top of <c>Plan</c>, which would prove
    /// nothing about the walk; this shape can only pass if something inside the walk asks.
    /// </para>
    /// <para>
    /// ⚠ And it THROWS rather than answering null. Null means "send this source to the segment path", so a
    /// cancellation reported that way would reroute a film because someone navigated away.
    /// </para>
    /// </summary>
    [Fact]
    public void Cancelling_a_plan_mid_walk_THROWS_rather_than_answering_null()
    {
        using var cts = new CancellationTokenSource();
        using var source = new CancelsOnFirstRead(Source("many-chunks").ToArray(), cts);

        Assert.False(cts.IsCancellationRequested, "the token must not be cancelled before Plan is entered");
        Assert.Throws<OperationCanceledException>(() => Mp4Remuxer.Plan(source, cts.Token));
    }

    /// <summary>Cancels as soon as anything reads a byte of it — so the trip happens after <c>Plan</c> begins.</summary>
    private sealed class CancelsOnFirstRead(byte[] bytes, CancellationTokenSource cts) : MemoryStream(bytes)
    {
        public override int ReadByte()
        {
            cts.Cancel();
            return base.ReadByte();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            cts.Cancel();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            cts.Cancel();
            return base.Read(buffer);
        }
    }

    /// <summary>
    /// The contract a range route does arithmetic against: the samples begin exactly where the header ends
    /// and run back to back, so any byte of the output is either header or in exactly one span.
    /// <para>
    /// ⚠ <c>Header</c> includes the <c>mdat</c> box header for this reason. Leaving it out would put an
    /// 8-or-16-byte hole between <c>Header.Length</c> and the first sample, and every consumer would have to
    /// re-derive which of the two it is.
    /// </para>
    /// </summary>
    [Fact]
    public void The_samples_start_where_the_header_ends_and_run_back_to_back()
    {
        using var source = Source("video+audio");
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        var at = (long)layout!.Header.Length;
        foreach (var span in layout.Samples)
        {
            Assert.Equal(at, span.OutputOffset);
            at += span.Length;
        }

        Assert.Equal(layout.TotalLength, at);
    }

    // ── what cannot be planned, and why null is the right answer ──────────────────────────────────────

    /// <summary>
    /// 🔴 A source needing a re-encode cannot state a length, and must say so rather than guess.
    /// <para>
    /// ⚠ <b>The contrast is the rule.</b> The WRITER succeeds on this very file — it carries the H.264
    /// picture and reports the AC-3 soundtrack it left behind — and that is right for a conversion, whose
    /// job is the best playable file it can make. It is wrong for a PLAN, which would then be describing a
    /// silent film as if it were the film. Refusing is what routes the source to the segment path, where a
    /// re-encoder can actually rescue the sound (D71).
    /// </para>
    /// </summary>
    [Fact]
    public void A_source_that_needs_re_encoding_cannot_be_planned()
    {
        using var source = Mkv(
            Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(codec: "A_AC3")],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(2, 0, true, Frame(9, 64))));

        Assert.Null(Mp4Remuxer.Plan(source, CancellationToken.None));

        // ⚠ The contrast: the writer takes this file happily, and names what it lost.
        source.Position = 0;
        using var copied = new MemoryStream();
        var written = new Mp4Remuxer().Write(source, copied, conversion: null);
        Assert.True(written.Succeeded, written.Reason);
        Assert.Contains("ac3", written.Dropped);

        // 🔴 AND HERE IS WHAT ACCEPTING IT WOULD HAVE COST, made executable rather than argued: the same
        // file written by a writer that HAS the device's codecs is a different, longer file, because the
        // AC-3 came back as AAC. A plan derived from the copy-only path would have advertised the shorter
        // total — the exact silent Content-Range failure this refusal exists to prevent.
        source.Position = 0;
        using var withCodecs = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, withCodecs, new GreedyConversion()).Succeeded);
        Assert.True(withCodecs.Length > copied.Length,
            $"the converted write must be longer, or this film proves nothing: {withCodecs.Length} vs {copied.Length}");
    }

    /// <summary>
    /// 🔴 <b>AND THE SAME RULE WHERE RE-ENCODING IS NOT THE REASON — this is the half a carriability check
    /// alone would miss, and every one of these is a source the writer accepts.</b>
    /// <list type="bullet">
    /// <item>A second AAC dub: both are carriable, and the first-of-each-kind selection still leaves one
    /// behind, because a webview plays one soundtrack.</item>
    /// <item>A second picture: same rule, same loss.</item>
    /// <item>🔴 A carriable soundtrack that DECLARES itself and holds no frames — the worst of the three,
    /// because the output is a silent film that nothing in a layout can explain. Only the cluster walk
    /// reveals it, which is why the cheap carriability gate cannot be the whole rule.</item>
    /// </list>
    /// <para>
    /// ⚠ The writer reports every one of these in <c>MediaRemuxerResult.Dropped</c> and is right to succeed.
    /// A layout has no <c>Dropped</c> — it is a length and a byte map — so the same output served off a plan
    /// is a 200 with a perfect <c>Content-Range</c> and no way to say what is missing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("second-dub")]
    [InlineData("second-picture")]
    [InlineData("empty-soundtrack")]
    public void A_source_the_output_would_LOSE_a_stream_from_cannot_be_planned(string shape)
    {
        using var source = shape switch
        {
            "second-dub" => Mkv(Info(1000),
                [VideoTrack(config: AvcConfig), AudioTrack(number: 2, config: AacConfig), AudioTrack(number: 3, config: AacConfig)],
                Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)),
                           SimpleBlock(2, 0, true, Frame(9, 64)),
                           SimpleBlock(3, 0, true, Frame(8, 64)))),

            "second-picture" => Mkv(Info(1000),
                [VideoTrack(number: 1, config: AvcConfig), VideoTrack(number: 4, config: AvcConfig)],
                Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(4, 0, true, Frame(5, 128)))),

            // The soundtrack is declared and the clusters carry none of it.
            _ => Mkv(Info(1000),
                [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
                Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)))),
        };

        Assert.Null(Mp4Remuxer.Plan(source, CancellationToken.None));

        // ⚠ The contrast again: the WRITER takes this file happily, and names what it lost.
        source.Position = 0;
        using var produced = new MemoryStream();
        var written = new Mp4Remuxer().Write(source, produced, conversion: null);
        Assert.True(written.Succeeded, written.Reason);
        Assert.NotEmpty(written.Dropped);
    }

    /// <summary>Nothing MP4 can carry at all — the transcode tier's file, and not this path's.</summary>
    [Fact]
    public void A_source_with_no_carriable_stream_cannot_be_planned()
    {
        using var source = Mkv(Info(1000), [AudioTrack(codec: "A_AC3", config: AacConfig)],
            Cluster(0, SimpleBlock(2, 0, true, Frame(0, 64))));

        Assert.Null(Mp4Remuxer.Plan(source, CancellationToken.None));
    }

    /// <summary>
    /// ⚠ A subtitle track must NOT cost the plan, and this is the case the "every stream must be carriable"
    /// rule would break if it were applied to the whole FILE. MP4 cannot carry a text track without a format
    /// conversion, and most real films have one — so counting it would make almost nothing plannable. The
    /// reader drops subtitles before the question is ever asked, on the planner's own droppable rule.
    /// </summary>
    [Fact]
    public void A_subtitle_track_does_not_stop_a_film_being_planned()
    {
        // TrackType 17 = subtitle, with a codec MP4 has no box for at all.
        var subtitles = El(0xAE, El(0xD7, [3]), El(0x83, [17]), El(0x86, Ascii("S_TEXT/UTF8")));

        using var source = Mkv(
            Info(1000),
            [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig), subtitles],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 128)), SimpleBlock(2, 0, true, Frame(9, 64))));

        Assert.NotNull(Mp4Remuxer.Plan(source, CancellationToken.None));
    }

    /// <summary>
    /// A carriable codec with no decoder configuration is refused here exactly as it is refused by the
    /// writer: the file it would produce opens and shows nothing, so there is nothing worth describing.
    /// </summary>
    [Fact]
    public void A_track_with_no_decoder_configuration_cannot_be_planned()
    {
        using var source = Mkv(Info(1000), [VideoTrack(config: null)],
            Cluster(0, SimpleBlock(1, 0, true, Frame(0, 64))));

        Assert.Null(Mp4Remuxer.Plan(source, CancellationToken.None));
    }

    /// <summary>
    /// Anything that is not a Matroska file answers null rather than throwing — a route asks this question
    /// about whatever a page pointed at, so a refusal is an ordinary answer and not an exception.
    /// </summary>
    [Fact]
    public void Anything_that_is_not_matroska_cannot_be_planned()
    {
        Assert.Null(Mp4Remuxer.Plan(new MemoryStream([]), CancellationToken.None));
        Assert.Null(Mp4Remuxer.Plan(new MemoryStream(Ascii("not a media file")), CancellationToken.None));
        Assert.Null(Mp4Remuxer.Plan(new MemoryStream([0, 0, 0, 0x18, .. "ftypisom"u8.ToArray()]), CancellationToken.None));
    }

    /// <summary>
    /// A truncated source is unplannable rather than plannable-and-wrong. ⚠ This is the dangerous direction:
    /// a layout derived from half a file would state a total the writer could never reach, and the range
    /// route would stall forever waiting for bytes that do not exist.
    /// </summary>
    [Fact]
    public void A_truncated_source_cannot_be_planned()
    {
        using var whole = Source("many-chunks");
        var bytes = whole.ToArray();

        Assert.Null(Mp4Remuxer.Plan(new MemoryStream(bytes[..(bytes.Length / 2)]), CancellationToken.None));
    }

    /// <summary>A stream that cannot seek has no frame index to walk, so there is nothing to describe.</summary>
    [Fact]
    public void A_stream_that_cannot_seek_cannot_be_planned()
    {
        using var source = new UnseekableStream(Source("video+audio").ToArray());
        Assert.Null(Mp4Remuxer.Plan(source, CancellationToken.None));
    }

    /// <summary>Readable, and refuses to seek — the shape a network body has.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    // ── reading a range back out of the plan (Mp4LayoutReader) ───────────────────────────────────────

    /// <summary>
    /// A source large enough that a byte range can meaningfully be "header only", "straddling header into
    /// mdat", "mid-mdat", or "the tail" — the four shapes the theory below exercises. Picture and sound
    /// interleaved across many clusters, the same shape "many-chunks" uses above but scaled up: ~150 KB of
    /// media is enough that 50,000–60,000 lands solidly inside <c>mdat</c> while staying fast to remux twice
    /// per test case (once for the plan, once for the whole-file comparison).
    /// </summary>
    private static MemoryStream LargeSource()
    {
        const int videoFrames = 800;
        const int audioFrames = 400;
        var clusters = new List<byte[]>();

        for (int v = 0, a = 0, ticks = 0; v < videoFrames || a < audioFrames; ticks += 40)
        {
            var blocks = new List<byte[]>();
            for (var k = 0; k < 2 && v < videoFrames; k++, v++)
            {
                // 151, not a round 150: a size that divides the per-cluster byte count evenly would risk a
                // hand-picked range landing EXACTLY on a sample boundary by coincidence rather than genuinely
                // mid-sample — which is exactly what happened here once, and the InlineData below is verified
                // against these odd sizes rather than assumed.
                blocks.Add(SimpleBlock(1, (short)(k * 20), v % 10 == 0, Frame(v, 151)));
            }
            if (a < audioFrames)
            {
                blocks.Add(SimpleBlock(2, 0, true, Frame(1000 + a, 80)));
                a++;
            }
            clusters.Add(Cluster((ulong)ticks, [.. blocks]));
        }

        return Mkv(Info(60_000), [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)], [.. clusters]);
    }

    /// <summary>
    /// 🔴 The strongest form of the claim, and cheap: for a spread of ranges, the bytes
    /// <see cref="Mp4LayoutReader.CopyRange"/> produces must be byte-identical to the same slice of the file
    /// the remuxer actually writes. Header-only, header→mdat straddle, mid-mdat and the tail are four shapes
    /// that break independently — a range reader wrong about any one of them still passes a test that only
    /// tries the others.
    /// </summary>
    [Theory]
    [InlineData(0, 99)]            // header only
    [InlineData(0, 100_000)]       // straddles header into mdat
    [InlineData(50_000, 60_000)]   // mid-mdat, starts inside one sample and ends inside a different one
    [InlineData(-2048, -1)]        // the tail (negative = from the end)
    public void A_range_equals_the_same_slice_of_the_real_output(long start, long end)
    {
        using var source = LargeSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var whole = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, whole, conversion: null).Succeeded);
        var expectedAll = whole.ToArray();

        var from = start < 0 ? expectedAll.LongLength + start : start;
        var to = end < 0 ? expectedAll.LongLength + end : Math.Min(end, expectedAll.LongLength - 1);

        source.Position = 0;
        using var actual = new MemoryStream();
        Mp4LayoutReader.CopyRange(layout!, source, from, to, actual, CancellationToken.None);

        Assert.Equal(expectedAll[(int)from..(int)(to + 1)], actual.ToArray());
    }

    /// <summary>
    /// 🔴 The existing four-range theory above proves <see cref="Mp4LayoutReader.CopyRange"/> against the real
    /// remuxer's output. This proves the LAZY path — <see cref="Mp4LayoutRangeStream"/> — against the SAME
    /// source of truth, over the SAME four shapes (header only, header→mdat straddle, mid-mdat, the tail), so
    /// the two cannot quietly drift apart: a pull reader that disagreed with the push writer over any one of
    /// them would fail here without needing a second fixture to expose it.
    /// <para>
    /// ⚠ Driven through <see cref="Stream.CopyTo(Stream, int)"/> with a deliberately SMALL 2 KiB buffer —
    /// Android's own measured chunk size — rather than the default ~80 KiB one, specifically so the larger
    /// ranges here (<c>100_000</c> and the mid-mdat span) cannot be satisfied in a single <c>Read</c>
    /// call. A reader that only worked when handed one big buffer would pass every assertion here and still
    /// corrupt the first real device that pulls it 2 KiB at a time.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, 99)]            // header only
    [InlineData(0, 100_000)]       // straddles header into mdat
    [InlineData(50_000, 60_000)]   // mid-mdat, starts inside one sample and ends inside a different one
    [InlineData(-2048, -1)]        // the tail (negative = from the end)
    public void A_lazily_read_range_equals_the_same_slice_of_the_real_output(long start, long end)
    {
        using var source = LargeSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var whole = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, whole, conversion: null).Succeeded);
        var expectedAll = whole.ToArray();

        var from = start < 0 ? expectedAll.LongLength + start : start;
        var to = end < 0 ? expectedAll.LongLength + end : Math.Min(end, expectedAll.LongLength - 1);

        source.Position = 0;
        using var range = new Mp4LayoutRangeStream(layout!, source, from, to);
        using var actual = new MemoryStream();
        range.CopyTo(actual, bufferSize: 2048);

        Assert.Equal(expectedAll[(int)from..(int)(to + 1)], actual.ToArray());
    }

    /// <summary>
    /// 🔴 The disposal invariant, at the layer that owns the source handle: reading a lazy range to its end
    /// must close the source it was reading, because iOS never will (measured 2026-08-12 — see
    /// <see cref="BoundedBodyStream"/>'s own remarks). Proven with <c>ownsSource: true</c>, the mode
    /// <c>ComputedRemuxRoute.Produce</c> actually uses; <see cref="Mp4LayoutReader.CopyRange"/>
    /// deliberately runs the OPPOSITE way (see its own remarks), which is why this test constructs the reader
    /// directly rather than going through <c>CopyRange</c>.
    /// </summary>
    [Fact]
    public void A_fully_read_lazy_range_closes_the_source_it_was_reading()
    {
        using var source = LargeSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        var counting = new DisposalCountingStream(source);
        var range = new Mp4LayoutRangeStream(layout!, counting, 0, layout!.TotalLength - 1, ownsSource: true);

        using var discard = new MemoryStream();
        range.CopyTo(discard, bufferSize: 2048);

        Assert.Equal(1, counting.DisposeCount);
    }

    /// <summary>A thin pass-through that counts how many times it was actually disposed.</summary>
    private sealed class DisposalCountingStream(Stream inner) : Stream
    {
        public int DisposeCount { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// One video keyframe past 4 KiB, filled with <see cref="VaryingFrame"/> bytes rather than
    /// <see cref="Frame"/>'s repeated value, bracketed by ordinary small frames on both tracks — so the reader
    /// must transition INTO and OUT of the big span's resumption state rather than starting fresh on it, the
    /// shape a real video keyframe takes inside an ordinary interleave.
    /// </summary>
    private static MemoryStream VaryingSource() => Mkv(
        Info(1000),
        [VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig)],
        Cluster(0,
            SimpleBlock(1, 0, true, Frame(0, 64)),
            SimpleBlock(2, 0, true, Frame(50, 40)),
            SimpleBlock(1, 40, false, VaryingFrame(1, 10_000)),
            SimpleBlock(2, 40, true, Frame(51, 40))));

    /// <summary>
    /// 🔴 THE HAZARD NOTHING ELSE IN THIS FILE CAN CATCH. Every span in <see cref="LargeSource"/> and
    /// <c>ComputedRemuxRouteTests</c>' <c>Film</c> is well under one read buffer (151 and 300 bytes
    /// respectively), and <see cref="Stream.CopyTo(Stream)"/>'s default 80 KiB buffer plus every explicit
    /// buffer size used elsewhere in this file (2 KiB) is bigger than either — so <c>toRead = min(count,
    /// _currentSpanRemaining)</c> always equals the whole remainder and a span always drains inside ONE
    /// <see cref="Mp4LayoutRangeStream.Read"/> call. The resumption state <c>Mp4LayoutRangeStream</c> exists
    /// FOR — <c>_currentSpanSourceCursor</c> surviving across multiple partial reads of the SAME span — never
    /// once executes in any other test in this file. Worse: <see cref="Mp4RemuxerTests.Frame"/> repeats one
    /// byte value, so even a test that DID force resumption could not detect an intra-span offset error —
    /// reading the wrong 3 bytes out of a 10,000-byte frame that is the SAME byte everywhere still compares
    /// equal. <see cref="VaryingSource"/> and <c>count</c> values of 1, 3 and 2048 (2 KiB — Android's own
    /// measured chunk size against a real keyframe, the most-executed production path this reader has) close
    /// both gaps at once.
    /// <para>
    /// Also exercises a NONZERO <c>offset</c> into the caller's own buffer, which every other test in this
    /// file calls <c>Read</c> without — a fixed sentinel region ahead of where bytes are asked to land, that
    /// must stay untouched.
    /// </para>
    /// <para>
    /// Sabotage-verified (not committed): dropping <c>_currentSpanSourceCursor += read</c> — so every
    /// resumed read of the big span re-reads from its ORIGINAL start instead of where the previous call left
    /// off — turns this failure from "off by a few bytes" into the reader repeating early bytes and running
    /// out of real span before the expected length is reached, and <c>Assert.Equal</c> reports the first
    /// differing byte and its index rather than merely "not equal".
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(2048)]
    public void A_lazy_read_resumes_correctly_mid_span_at_small_buffer_sizes(int count)
    {
        using var source = VaryingSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        source.Position = 0;
        using var whole = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, whole, conversion: null).Succeeded);
        var expected = whole.ToArray();

        source.Position = 0;
        using var range = new Mp4LayoutRangeStream(layout!, source, 0, layout!.TotalLength - 1);

        // A NONZERO offset and a buffer bigger than `count`: a `Read` that ignored `offset` (writing at 0
        // instead of into the caller's requested slice) would corrupt the sentinel bytes ahead of it, which
        // nothing in the rest of this file's `Read` calls (all `offset: 0`) could ever notice.
        const int sentinelSize = 7;
        var buffer = new byte[count + sentinelSize];
        var actual = new List<byte>(expected.Length);
        int read;
        while ((read = range.Read(buffer, sentinelSize, count)) > 0)
        {
            Assert.All(buffer.Take(sentinelSize), b => Assert.Equal((byte)0, b));
            actual.AddRange(buffer.Skip(sentinelSize).Take(read));
        }

        Assert.Equal(expected, actual.ToArray());
    }

    /// <summary>
    /// 🔴 The theory above LABELS <c>(50_000, 60_000)</c> as "starts inside one sample and ends inside a
    /// different one", but a label is not a proof — one revision of this fixture had those exact bytes land
    /// on the very FIRST byte of a sample rather than genuinely mid-sample, which a naive off-by-one in
    /// <c>CopyRange</c> could satisfy without ever exercising the harder case. This test computes its start
    /// and end from the plan's OWN sample list instead of a hand-picked byte pair, so "strictly interior,
    /// two different samples" is a checked guarantee rather than an arithmetic coincidence a future fixture
    /// change could quietly break again.
    /// </summary>
    [Fact]
    public void A_range_that_starts_and_ends_strictly_inside_different_samples_is_exact()
    {
        using var source = LargeSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        // Two samples, comfortably apart, each with at least 3 bytes so a strictly-interior byte exists.
        var candidates = layout!.Samples.Where(s => s.Length >= 3).ToList();
        var first = candidates[candidates.Count / 4];
        var second = candidates[candidates.Count * 3 / 4];
        Assert.NotEqual(first.OutputOffset, second.OutputOffset);

        var from = first.OutputOffset + 1;                  // one past the span's first byte
        var to = second.OutputOffset + second.Length - 2;   // one before the span's last byte

        // The guarantee itself, asserted BEFORE calling CopyRange — the whole point is that this is checked,
        // not assumed.
        Assert.InRange(from, first.OutputOffset + 1, first.OutputOffset + first.Length - 2);
        Assert.InRange(to, second.OutputOffset + 1, second.OutputOffset + second.Length - 2);

        source.Position = 0;
        using var whole = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, whole, conversion: null).Succeeded);
        var expected = whole.ToArray();

        source.Position = 0;
        using var actual = new MemoryStream();
        Mp4LayoutReader.CopyRange(layout, source, from, to, actual, CancellationToken.None);

        Assert.Equal(expected[(int)from..(int)(to + 1)], actual.ToArray());
    }

    /// <summary>
    /// 🔴 <b>EBML lacing — the ONE lacing scheme with no test at all until 2026-08-14, and the one the
    /// reader's own doc calls "the part that is easy to get wrong".</b> Xiph and fixed lacing both had
    /// fixtures; this had none, so nothing had ever exercised the signed-delta arithmetic or the
    /// frame-count rule.
    /// <para>
    /// The rule that matters: the lacing header byte is <c>frames − 1</c>, and EBML lacing codes exactly
    /// that many sizes — the first as an unsigned vint, each one after it as a signed delta, and the LAST
    /// frame's size implied by what is left. So a block declaring ONE frame codes ZERO sizes.
    /// </para>
    /// </summary>
    /// <param name="count">The lacing header byte — <c>frames − 1</c>.</param>
    /// <param name="codedSizes">The sizes actually written after it, already vint-encoded.</param>
    /// <param name="payload">The frame bytes.</param>
    private static MemoryStream EbmlLacedSource(byte count, byte[] codedSizes, byte[] payload)
    {
        var trackByte = (byte)(0x80 | 2);            // one-byte vint: track number 2 (audio)
        var flagsByte = (byte)(0x80 | 0x06);         // keyframe (bit 7) + EBML lacing (bits 0x06 == 0x06)
        var block = El(0xA3, [trackByte], [0x00, 0x00], [flagsByte], [count], codedSizes, payload);
        return Mkv(Info(1000), [AudioTrack(config: AacConfig)], Cluster(0, block));
    }

    /// <summary>
    /// 🔴 <b>An EBML-laced block carrying ONE frame codes NO sizes</b> — the last frame is always implied,
    /// and with a single frame it is the only frame. Reading a size here consumes the frame's own first
    /// bytes as a length, so the plan either refuses the file outright or points at the wrong bytes.
    /// <para>
    /// Asserted against the real remuxer's output rather than "did not throw": a misparse that happens to
    /// produce a positive length would still plan, and would still be wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void An_EBML_laced_block_with_a_SINGLE_frame_codes_no_sizes()
    {
        // count = 0 → one frame → zero coded sizes → the whole payload is that frame.
        using var source = EbmlLacedSource(count: 0, codedSizes: [], payload: Frame(30, 40));

        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);

        Assert.NotNull(layout);
        var sample = Assert.Single(layout!.Samples);
        Assert.Equal(40, sample.Length);
    }

    /// <summary>
    /// The ordinary EBML-lacing shape, so the single-frame case above is not the only thing holding this
    /// scheme up: three frames, two coded sizes — the first an unsigned vint, the second a signed delta
    /// biased by <c>2^(7·width − 1) − 1</c>, and the third implied.
    /// </summary>
    [Fact]
    public void An_EBML_laced_block_reads_its_first_size_then_signed_deltas()
    {
        // Frames of 40, 40 and 40 bytes. First size = 40 as a 1-byte vint (0x80 | 40).
        // Second is a DELTA of 0 from the first, biased by 2^6 − 1 = 63 → coded value 63 → 0x80 | 63.
        using var source = EbmlLacedSource(
            count: 2,
            codedSizes: [(byte)(0x80 | 40), (byte)(0x80 | 63)],
            payload: [.. Frame(30, 40), .. Frame(31, 40), .. Frame(32, 40)]);

        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);

        Assert.NotNull(layout);
        Assert.Equal(3, layout!.Samples.Count);
        Assert.All(layout.Samples, s => Assert.Equal(40, s.Length));
    }

    /// <summary>
    /// A degenerate laced frame — legal Matroska, one Xiph-laced block whose MIDDLE frame declares size
    /// ZERO — is exactly the hazard <see cref="Mp4Layout.Samples"/>'s own remarks warn about: the planned
    /// span for that frame shares its <c>OutputOffset</c> with the frame after it. Nothing in the shared
    /// fixture set above produces one, so the reader's "skip empties" rule had never actually been exercised
    /// before this.
    /// </summary>
    private static MemoryStream ZeroLengthSpanSource()
    {
        var trackByte = (byte)(0x80 | 2);                 // one-byte vint: track number 2 (audio)
        var flagsByte = (byte)(0x80 | 0x02);              // keyframe (bit 7) + Xiph lacing (bits 0x06 == 0x02)
        var lacingHeader = new byte[] { 0x02, 40, 0x00 }; // 3 frames (count-1=2); sizes 40, 0, then the remainder

        // The middle frame's size byte is 0x00, so it contributes NO bytes at all — the data that follows is
        // only the first and third frame's, back to back.
        var block = El(0xA3, [trackByte], [0x00, 0x00], [flagsByte], lacingHeader,
            [.. Frame(30, 40), .. Frame(32, 40)]);

        return Mkv(Info(1000), [AudioTrack(config: AacConfig)], Cluster(0, block));
    }

    /// <summary>
    /// 🔴 A range that TOUCHES a zero-length span — landing on the exact offset it shares with the real span
    /// after it — must resolve to that real span rather than stalling on the empty one or reading the wrong
    /// bytes. Asserted the same way as the theory above: byte-identical to the real remuxer's output, not
    /// merely "did not throw", which a stall or an off-by-one could both still pass.
    /// </summary>
    [Fact]
    public void A_zero_length_span_is_skipped_rather_than_walked_onto()
    {
        using var source = ZeroLengthSpanSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        // Confirm the fixture actually produces the hazard, rather than trusting the construction above —
        // an assertion that never sees a zero-length span proves nothing about the code meant to skip it.
        Assert.Contains(layout!.Samples, s => s.Length == 0);
        var zero = layout.Samples.First(s => s.Length == 0);
        Assert.Contains(layout.Samples, s => s.Length > 0 && s.OutputOffset == zero.OutputOffset);

        source.Position = 0;
        using var whole = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, whole, conversion: null).Succeeded);
        var expected = whole.ToArray();

        // A range starting EXACTLY on the shared offset, running to the end of the file — the request most
        // likely to land squarely on the empty span rather than merely passing near it.
        source.Position = 0;
        using var actual = new MemoryStream();
        Mp4LayoutReader.CopyRange(layout, source, zero.OutputOffset, layout.TotalLength - 1, actual, CancellationToken.None);

        Assert.Equal(expected[(int)zero.OutputOffset..], actual.ToArray());
    }

    /// <summary>
    /// TWO zero-length spans in a row, sharing ONE <c>OutputOffset</c> with the real span after them — Xiph
    /// lacing lets a block declare any number of degenerate frames back to back, not just one.
    /// <para>
    /// ⚠ <b>Sabotage-verified in both directions, and narrower than it looks.</b> An UNDER-shooting lookup
    /// (take the FIRST span at a tied offset) does NOT fail this, because
    /// <see cref="Mp4LayoutReader.CopyRange"/> re-checks every later span's overlap and so self-corrects; what
    /// it catches is an OVER-shoot past the content-bearing span. Kept because it pins the multiple-zero
    /// SHAPE, not because it is a stronger net than its single-zero sibling.
    /// </para>
    /// </summary>
    private static MemoryStream MultipleZeroLengthSpansSource()
    {
        var trackByte = (byte)(0x80 | 2);                       // one-byte vint: track number 2 (audio)
        var flagsByte = (byte)(0x80 | 0x02);                    // keyframe (bit 7) + Xiph lacing (bits 0x06 == 0x02)
        var lacingHeader = new byte[] { 0x03, 40, 0x00, 0x00 }; // 4 frames (count-1=3); sizes 40, 0, 0, remainder

        // The middle TWO frames' size bytes are both 0x00, so neither contributes any bytes — the data that
        // follows is only the first and fourth frame's, back to back.
        var block = El(0xA3, [trackByte], [0x00, 0x00], [flagsByte], lacingHeader,
            [.. Frame(30, 40), .. Frame(34, 40)]);

        return Mkv(Info(1000), [AudioTrack(config: AacConfig)], Cluster(0, block));
    }

    /// <summary>
    /// 🔴 The durability half of the zero-length hazard: a range touching the offset TWO zero-length spans
    /// share with the real span after them must still resolve to that real span, byte-identical to the real
    /// remuxer's output — not merely "did not throw" or "did not hang", either of which a lookup that landed
    /// on the wrong span in this tied run could still satisfy by accident.
    /// </summary>
    [Fact]
    public void Multiple_consecutive_zero_length_spans_sharing_one_offset_are_all_skipped()
    {
        using var source = MultipleZeroLengthSpansSource();
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);

        // Confirm the fixture actually produces TWO zero-length spans sharing one offset, immediately
        // followed by the real, nonzero span that offset belongs to — an assertion that never sees the tied
        // run proves nothing about the code meant to walk past all of it.
        var zeros = layout!.Samples.Where(s => s.Length == 0).ToList();
        Assert.True(zeros.Count >= 2, $"expected at least two zero-length spans in this fixture, found {zeros.Count}");
        var sharedOffset = zeros[0].OutputOffset;
        Assert.All(zeros, z => Assert.Equal(sharedOffset, z.OutputOffset));
        Assert.Contains(layout.Samples, s => s.Length > 0 && s.OutputOffset == sharedOffset);

        source.Position = 0;
        using var whole = new MemoryStream();
        Assert.True(new Mp4Remuxer().Write(source, whole, conversion: null).Succeeded);
        var expected = whole.ToArray();

        // A range starting EXACTLY on the shared offset, running to the end of the file — the byte a lookup
        // landing on either zero-length span, instead of the real one after them, would misresolve.
        source.Position = 0;
        using var actual = new MemoryStream();
        Mp4LayoutReader.CopyRange(layout, source, sharedOffset, layout.TotalLength - 1, actual, CancellationToken.None);

        Assert.Equal(expected[(int)sharedOffset..], actual.ToArray());
    }

    /// <summary>
    /// ⚠ <c>CopyRange</c> is the one place a route's numbers meet the plan directly, so its own argument
    /// checks are the last line between a malformed <c>Range</c> header and silent corruption — a caller
    /// asking for bytes the file does not have must fail LOUD rather than read whatever happened to sit at
    /// the wrong offset.
    /// </summary>
    [Fact]
    public void CopyRange_rejects_a_request_for_bytes_the_layout_does_not_have()
    {
        using var source = Source("video+audio");
        var layout = Mp4Remuxer.Plan(source, CancellationToken.None);
        Assert.NotNull(layout);
        using var destination = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Mp4LayoutReader.CopyRange(layout!, source, -1, 10, destination, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Mp4LayoutReader.CopyRange(layout!, source, 10, 5, destination, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Mp4LayoutReader.CopyRange(layout!, source, 0, layout!.TotalLength, destination, CancellationToken.None));
    }
}
