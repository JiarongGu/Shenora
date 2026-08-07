using Shenora.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// Reading what is inside a Matroska file with no external tool — the first piece of the translation layer,
/// because "can this play?" was previously answerable only by shipping a media toolchain.
///
/// <para>
/// Every fixture here is BUILT, not a checked-in file: EBML is nested tag-length-value, so a test can state
/// exactly the bytes it means and a failure names a field rather than a binary blob. It also keeps the repo
/// free of sample media whose own licence would need answering (D51).
/// </para>
/// </summary>
public class MatroskaProbeTests
{
    // ── the EBML fixture builder ──────────────────────────────────────────────────────────────────────

    /// <summary>An element: its id exactly as it appears on the wire, then its payload as a length + bytes.</summary>
    private static byte[] El(uint id, params byte[][] payload)
    {
        var body = payload.SelectMany(p => p).ToArray();
        var idBytes = IdBytes(id);
        return [.. idBytes, .. Size(body.Length), .. body];
    }

    /// <summary>Ids are written as-is — the length marker is PART of the id, which is what the reader keeps.</summary>
    private static byte[] IdBytes(uint id)
    {
        if (id <= 0xFF) return [(byte)id];
        if (id <= 0xFFFF) return [(byte)(id >> 8), (byte)id];
        if (id <= 0xFFFFFF) return [(byte)(id >> 16), (byte)(id >> 8), (byte)id];
        return [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id];
    }

    /// <summary>A one-byte size where it fits, else four — enough for any fixture here.</summary>
    private static byte[] Size(int length) =>
        length < 0x7F
            ? [(byte)(0x80 | length)]
            : [0x10, (byte)(length >> 16), (byte)(length >> 8), (byte)length];

    private static byte[] UInt(ulong value)
    {
        if (value <= 0xFF) return [(byte)value];
        if (value <= 0xFFFF) return [(byte)(value >> 8), (byte)value];
        return [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] Dbl(double value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    private static byte[] Ascii(string value) => System.Text.Encoding.ASCII.GetBytes(value);

    /// <summary>A whole file: an EBML header, then a Segment holding Info and Tracks.</summary>
    private static MemoryStream File_(byte[] info, params byte[][] trackEntries) =>
        new([
            .. El(0x1A45DFA3, Ascii("hdr")),
            .. El(0x18538067,
                info,
                El(0x1654AE6B, trackEntries.SelectMany(t => t).ToArray())),
        ]);

    private static byte[] Info(double durationTicks, ulong scale = 1_000_000) =>
        El(0x1549A966,
            El(0x2AD7B1, UInt(scale)),
            El(0x4489, Dbl(durationTicks)));

    private static byte[] Track(ulong type, string codecId, byte[]? extra = null) =>
        El(0xAE,
            El(0x83, UInt(type)),
            El(0x86, Ascii(codecId)),
            extra ?? []);

    // ── the cases ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The case the whole translation layer exists for: an MKV whose VIDEO is perfectly playable and whose
    /// SOUNDTRACK is not. The picture needs nothing done to it; only the audio does.
    /// </summary>
    [Fact]
    public void An_h264_plus_ac3_mkv_reads_as_exactly_that()
    {
        using var file = File_(
            Info(120_000),                                   // 120 000 ticks × 1 ms = 120 s
            Track(1, "V_MPEG4/ISO/AVC", El(0xE0, El(0xB0, UInt(1920)), El(0xBA, UInt(1080)))),
            Track(2, "A_AC3", El(0xE1, El(0x9F, UInt(6)))));

        var probe = MatroskaProbe.Read(file);

        Assert.NotNull(probe);
        Assert.Equal(".mkv", probe!.Container);
        Assert.Equal(TimeSpan.FromSeconds(120), probe.Duration);
        Assert.Equal(1920, probe.Width);
        Assert.Equal(1080, probe.Height);

        var video = Assert.Single(probe.Streams, s => s.Kind == MediaStreamKind.Video);
        Assert.Equal("h264", video.Codec);

        var audio = Assert.Single(probe.Streams, s => s.Kind == MediaStreamKind.Audio);
        Assert.Equal("ac3", audio.Codec);
        Assert.Equal(6, audio.Channels);
    }

    /// <summary>
    /// Matroska's own codec names are translated to the lowercase vocabulary every policy speaks. A policy
    /// written against probe output must not have to know two names for one codec.
    /// </summary>
    [Theory]
    [InlineData("V_MPEG4/ISO/AVC", "h264")]
    [InlineData("V_MPEGH/ISO/HEVC", "hevc")]
    [InlineData("V_AV1", "av1")]
    [InlineData("V_MPEG4/ISO/ASP", "mpeg4")]
    [InlineData("A_AAC", "aac")]
    [InlineData("A_AAC/MPEG4/LC", "aac")]
    [InlineData("A_AC3", "ac3")]
    [InlineData("A_EAC3", "eac3")]
    [InlineData("A_DTS", "dts")]
    [InlineData("A_TRUEHD", "truehd")]
    [InlineData("A_MPEG/L3", "mp3")]
    [InlineData("A_FLAC", "flac")]
    [InlineData("A_ALAC", "alac")]
    [InlineData("A_PCM/INT/LIT", "pcm")]
    public void Matroska_codec_ids_are_translated_to_the_planner_vocabulary(string codecId, string expected)
    {
        using var file = File_(Info(1000), Track(codecId.StartsWith("V_", StringComparison.Ordinal) ? 1u : 2u, codecId));
        var probe = MatroskaProbe.Read(file);
        Assert.Equal(expected, Assert.Single(probe!.Streams).Codec);
    }

    /// <summary>
    /// An id nothing recognises comes back lowercased rather than null — an unknown name is correctly read
    /// as unplayable by a policy, which is the SAFE direction. Null would read as "no codec" and could be
    /// waved through.
    /// </summary>
    [Fact]
    public void An_unknown_codec_id_is_reported_rather_than_dropped()
    {
        using var file = File_(Info(1000), Track(2, "A_SOMETHING_NEW"));
        Assert.Equal("a_something_new", Assert.Single(MatroskaProbe.Read(file)!.Streams).Codec);
    }

    [Fact]
    public void The_timestamp_scale_is_honoured_rather_than_assumed()
    {
        // 90 kHz ticks: 5 400 000 of them at 11 111 ns each is 60 s. A parser that assumed the 1 ms default
        // would report 5400 s — a scrub bar 90× too long.
        using var file = File_(Info(5_400_000, scale: 11_111), Track(1, "V_MPEG4/ISO/AVC"));
        var probe = MatroskaProbe.Read(file);
        Assert.Equal(60, probe!.Duration!.Value.TotalSeconds, precision: 1);
    }

    [Fact]
    public void Subtitle_tracks_are_reported_as_subtitles_not_as_audio()
    {
        using var file = File_(Info(1000),
            Track(1, "V_MPEG4/ISO/AVC"),
            Track(2, "A_AAC"),
            Track(17, "S_TEXT/UTF8"));

        var probe = MatroskaProbe.Read(file);
        Assert.Equal(3, probe!.Streams.Count);
        Assert.Equal("subrip", Assert.Single(probe.Streams, s => s.Kind == MediaStreamKind.Subtitle).Codec);
    }

    [Fact]
    public void A_webm_is_the_same_format_and_reports_the_container_it_was_called()
    {
        using var file = File_(Info(1000), Track(1, "V_VP9"), Track(2, "A_OPUS"));
        var probe = MatroskaProbe.Read(file, ".webm");
        Assert.Equal(".webm", probe!.Container);
        Assert.Equal("vp9", Assert.Single(probe.Streams, s => s.Kind == MediaStreamKind.Video).Codec);
    }

    /// <summary>
    /// ⚠ This parses a file a PAGE can point at, so every malformed shape must cost a bounded read and a
    /// null — never a throw, a hang, or a walk to EOF.
    /// </summary>
    [Fact]
    public void Anything_that_is_not_matroska_answers_null_rather_than_throwing()
    {
        Assert.Null(MatroskaProbe.Read(new MemoryStream([])));
        Assert.Null(MatroskaProbe.Read(new MemoryStream([0x00, 0x00, 0x00, 0x00])));
        Assert.Null(MatroskaProbe.Read(new MemoryStream(System.Text.Encoding.ASCII.GetBytes("not a media file"))));
        // An MP4: a real file, a real container, simply not this one.
        Assert.Null(MatroskaProbe.Read(new MemoryStream([0, 0, 0, 0x18, .. "ftypisom"u8.ToArray()])));
    }

    [Fact]
    public void A_truncated_file_answers_null_rather_than_hanging()
    {
        using var whole = File_(Info(120_000), Track(1, "V_MPEG4/ISO/AVC"));
        var bytes = whole.ToArray();

        // Cut it off mid-header. A length that promises more than the file holds is the classic way an
        // EBML parser spins or reads past the end.
        using var truncated = new MemoryStream(bytes[..(bytes.Length / 2)]);
        var probe = MatroskaProbe.Read(truncated);

        // Either null or a partial answer is acceptable; hanging or throwing is not, and that is what this
        // pins. (It completes, which is the assertion.)
        Assert.True(probe is null || probe.Streams.Count <= 1);
    }

    [Fact]
    public void A_missing_file_answers_null()
        => Assert.Null(MatroskaProbe.Read(Path.Combine(Path.GetTempPath(), "shenora-does-not-exist.mkv")));

    /// <summary>
    /// The probe's whole point: its output feeds the planner unchanged, so the kit can now answer "must this
    /// be transformed?" without an external tool.
    /// </summary>
    [Fact]
    public void The_probe_feeds_the_planner_directly()
    {
        using var file = File_(Info(120_000),
            Track(1, "V_MPEG4/ISO/AVC"),
            Track(2, "A_AC3"));

        var probe = MatroskaProbe.Read(file)!;
        var policy = new MediaPlaybackPolicy
        {
            Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4a", ".webm" },
            Codecs = new Dictionary<MediaStreamKind, IReadOnlySet<string>>
            {
                [MediaStreamKind.Video] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "vp9", "av1" },
                [MediaStreamKind.Audio] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac", "opus", "flac" },
            },
        };

        // ⚠ WITHOUT an encoder the honest answer is Unsupported, not Transcode — the planner refuses to
        // promise work nothing can do, which is the same discipline the engine seam follows. Asserted
        // because it is the half a test would normally skip and the half that keeps the layer truthful.
        Assert.Equal(MediaPlaybackAction.Unsupported, MediaPlaybackPlanner.Plan(probe, policy).Action);

        // WITH one, the exact case the translation layer exists for, decided end to end and with no
        // external tool: the PICTURE is fine and needs nothing done to it, the SOUNDTRACK cannot play, so
        // the answer is Transcode rather than Direct (it cannot just be served) or Remux (a new box would
        // still carry AC-3). This is the whole thesis in one assertion.
        var withEncoder = policy with { Encodable = new HashSet<MediaStreamKind> { MediaStreamKind.Audio } };
        Assert.Equal(MediaPlaybackAction.Transcode, MediaPlaybackPlanner.Plan(probe, withEncoder).Action);
    }

    /// <summary>
    /// And the cheap half of the same thesis: right codecs, wrong box. Nothing has to be decoded — which is
    /// what makes a remuxer worth writing in managed code at all.
    /// </summary>
    [Fact]
    public void An_h264_plus_aac_mkv_needs_only_a_REMUX()
    {
        using var file = File_(Info(120_000), Track(1, "V_MPEG4/ISO/AVC"), Track(2, "A_AAC"));

        var plan = MediaPlaybackPlanner.Plan(MatroskaProbe.Read(file)!, new MediaPlaybackPolicy
        {
            Containers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4a", ".webm" },
            Codecs = new Dictionary<MediaStreamKind, IReadOnlySet<string>>
            {
                [MediaStreamKind.Video] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "vp9", "av1" },
                [MediaStreamKind.Audio] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac", "opus", "flac" },
            },
        });

        Assert.Equal(MediaPlaybackAction.Remux, plan.Action);
    }
}
