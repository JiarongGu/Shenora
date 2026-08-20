using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The copy path (D76) driven over a REAL Matroska file rather than a built one — 60 s of H.264 480×270
/// beside 44.1 kHz AAC, muxed by ffmpeg, the same clip the mobile probes stream.
///
/// <para>
/// 🔴 <b>Every other test in this area builds its own fixture, and a built fixture cannot fail the way a
/// real one does.</b> <see cref="DefaultSegmentEngineTests"/>'s sources carry 40 invented bytes per frame
/// and a plausible <c>avcC</c>, which is exactly right for proving the PUMP and proves nothing at all
/// about the demuxer: a real file laces its blocks, spreads them over many clusters, starts its first
/// keyframe a little after zero, and stores a configuration a decoder will actually be handed. This class
/// covers the seam between the kit and a file it did not write.
/// </para>
/// <para>
/// ⚠ <b>What it still cannot say is whether the result DECODES</b> — that needs a decoder, and this suite
/// must run with no external tool. So the run leaves its artifacts in <c>devtools/_media-real/</c> and
/// <c>node devtools/dev.mjs media-decode</c> hands them to ffmpeg and to a real WebView2
/// <c>MediaSource</c>. The division is deliberate: everything checkable without a decoder is checked
/// HERE, where it runs on every build, and only the decode itself needs the probe.
/// </para>
/// </summary>
public class RealSourceSegmentTests
{
    /// <summary>Cut every 4 s. The clip keys every 2 s, so a cut CAN land on the asked-for boundary.</summary>
    private const double SegmentSeconds = 4.0;

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "TestAssets", "media", "clip-h264-aac.mkv");

    /// <summary>The same fixture as the engine now takes it: an opener, not a path.</summary>
    private static MediaByteSource FixtureBytes => MediaByteSource.ForFile(Fixture);

    /// <summary>
    /// Where the run's bytes are left for the decode probe. A fixed path rather than a temp one BECAUSE the
    /// probe is a separate process run minutes later by hand; <c>devtools/_*</c> is gitignored.
    /// </summary>
    private static string Artifacts
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shenora.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "devtools", "_media-real");
        }
    }

    /// <summary>
    /// A conversion that must never be consulted, and RECORDS rather than throws.
    /// <para>
    /// ⚠ The pump guards its own body, so a throwing fake would be swallowed and reported as a failed run —
    /// a true failure with a misleading cause. Recording turns it into the assertion it should be.
    /// </para>
    /// </summary>
    private sealed class RecordingConversion : IMediaStreamConversion
    {
        public List<string> Asked { get; } = [];

        public bool CanConvert(MediaStreamKind kind, string codec)
        {
            lock (Asked) Asked.Add($"{kind}:{codec}");
            return false;
        }

        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
        {
            lock (Asked) Asked.Add($"begin:{source.Kind}");
            return null;
        }
    }

    private static List<string> Segments(string dir) =>
        [.. Directory.GetFiles(dir, "seg*.m4s").OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)];

    /// <summary>The fixture has to BE there — a silently absent one turns every assertion below vacuous.</summary>
    [Fact]
    public void The_real_fixture_is_present_and_is_the_clip_these_tests_describe()
    {
        Assert.True(File.Exists(Fixture), $"real media fixture missing: {Fixture}");

        var probe = MatroskaProbe.Read(Fixture);
        Assert.NotNull(probe);
        Assert.Equal(480, probe.Width);
        Assert.Equal(270, probe.Height);
        Assert.NotNull(probe.Duration);
        Assert.InRange(probe.Duration.Value.TotalSeconds, 59.0, 61.0);

        // The codecs ARE the branch (D76): this file is only a copy-path fixture while both are carriable.
        Assert.Contains(probe.Streams, s => s.Kind is MediaStreamKind.Video);
        Assert.Contains(probe.Streams, s => s.Kind is MediaStreamKind.Audio);
    }

    /// <summary>
    /// The plan is derived from the file's OWN keyframes, not from the asked-for grid.
    /// <para>
    /// Measured with ffprobe: the clip keys at 0.023 s and every 2 s after, so a 4 s ask yields cuts every
    /// 4 s — and the first one is NOT at zero, which is the detail a built fixture never reproduces.
    /// </para>
    /// </summary>
    [Fact]
    public void A_real_source_plans_on_its_own_keyframes()
    {
        var engine = new DefaultSegmentEngine(new RecordingConversion());

        var plan = engine.PlanSegments(FixtureBytes, SegmentSeconds);

        Assert.NotNull(plan);
        Assert.Null(plan.GridSeconds);          // derived, not uniform
        Assert.InRange(plan.Count, 14, 16);     // 60 s at ~4 s a cut
        Assert.InRange(plan.LongestSeconds, 3.5, 4.5);
    }

    /// <summary>
    /// 🔴 <b>The whole of D76 in one assertion: a real H.264 picture and a real AAC soundtrack reach the
    /// fragments without a codec being asked for.</b> Before D76 this run re-encoded both, and on a phone
    /// produced a picture no webview could decode.
    /// </summary>
    [Fact]
    public async Task A_real_source_is_copied_whole_and_no_converter_is_ever_consulted()
    {
        var conversion = new RecordingConversion();
        var engine = new DefaultSegmentEngine(conversion);
        var plan = engine.PlanSegments(FixtureBytes, SegmentSeconds);
        Assert.NotNull(plan);

        var dir = Artifacts;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var run = engine.Start(new SegmentRunRequest(FixtureBytes, dir, HasPicture: true, FirstSegment: 0, plan, Attempt: 0));
        Assert.NotNull(run);

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!run.HasExited && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(run.HasExited, "the production run did not finish within 120s");

        // ── the claim ───────────────────────────────────────────────────────────────────────────────────
        Assert.Empty(conversion.Asked);

        // ── the bytes ───────────────────────────────────────────────────────────────────────────────────
        var init = Path.Combine(dir, SegmentRunRequest.InitSegmentName);
        Assert.True(File.Exists(init), "no init segment was written");
        Assert.True(new FileInfo(init).Length > 0, "the init segment is empty");

        var segments = Segments(dir);
        Assert.Equal(plan.Count, segments.Count);
        Assert.All(segments, s => Assert.True(new FileInfo(s).Length > 0, $"empty fragment: {s}"));

        // A PICTURE in every fragment — the subtraction that catches a sound-only run (D76's symptom).
        Assert.All(segments, s => Assert.True(
            Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId) > 0,
            $"fragment carries no picture: {Path.GetFileName(s)}"));

        Assert.All(segments, s => Assert.True(
            Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.AudioTrackId) > 0,
            $"fragment carries no sound: {Path.GetFileName(s)}"));

        // ── merged, so the decode probe has one file to open ────────────────────────────────────────────
        var parts = SegmentMerge.Parts(dir, plan);
        Assert.Equal(1 + plan.Count, parts.Count);

        var merged = Path.Combine(dir, "merged.mp4");
        await SegmentMerge.WriteAsync(parts, merged, CancellationToken.None);
        Assert.True(File.Exists(merged), $"merge wrote nothing: {merged}");

        var expected = parts.Sum(p => new FileInfo(p).Length);
        Assert.Equal(expected, new FileInfo(merged).Length);
    }
}
