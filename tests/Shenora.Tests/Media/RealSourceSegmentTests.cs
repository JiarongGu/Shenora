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
/// <c>node devtools/dev.mjs media-decode</c> hands them to ffmpeg. The division is deliberate: everything
/// checkable without a decoder is checked HERE, where it runs on every build, and only the decode itself
/// needs a tool this repo refuses to depend on.
/// </para>
/// <para>
/// ⚠ <b>A real <c>MediaSource</c> is still not covered.</b> ffmpeg accepts streams a webview rejects — it
/// repairs what it can — so a green decode is a floor, and the sample remains the only thing that proves
/// the page can play what this writes.
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
        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new RecordingConversion(), AppCallback.Logger(lines.Add));

        var plan = engine.PlanSegments(FixtureBytes, SegmentLengths.Of(SegmentSeconds));

        Assert.NotNull(plan);
        Assert.Null(plan.GridSeconds);          // derived, not uniform
        Assert.InRange(plan.Count, 14, 16);     // 60 s at ~4 s a cut
        Assert.InRange(plan.LongestSeconds, 3.5, 4.5);

        // 🔴 And it got there from the INDEX. This is the whole point of reading Cues: the walk it replaces
        // touches about a third of the file's pages, on the request that answers the first manifest.
        Assert.DoesNotContain(lines, l => l.Contains("walking its clusters", StringComparison.Ordinal));
    }

    /// <summary>
    /// The same real file, read through <see cref="MediaByteSource.ForRanges"/> instead of off the disk —
    /// the shape a remote source has.
    ///
    /// <para>
    /// 🔴 <b>A fake TRANSPORT, and the plan must come out identical.</b> The adapter exists so the Cues work
    /// pays off for a source that is not a local file; nothing else in the suite proves the real demuxer can
    /// drive it, because every other test hands the engine a <c>FileStream</c> whose own buffer hides the
    /// access pattern entirely.
    /// </para>
    /// <para>
    /// ⚠ <b>The two assertions prove DIFFERENT things and neither substitutes for the other.</b> The absent
    /// "walking its clusters" line is what proves the INDEX was used. The fetch ceiling proves the ADAPTER
    /// BUFFERS — measured at 4 round trips for the whole plan, against the hundreds of thousands an
    /// unbuffered fetch-per-`ReadByte` would take. ⚠ The count cannot tell walk from index at this size: the
    /// file is ~456 KB and the window 256 KB, so even a full walk would be a couple of fetches.
    /// </para>
    /// </summary>
    [Fact]
    public void A_real_source_plans_identically_through_a_RANGE_transport()
    {
        var content = File.ReadAllBytes(Fixture);
        var fetches = 0;
        var bytes = MediaByteSource.ForRanges("clip-h264-aac.mkv", content.Length, (offset, count, _) =>
        {
            Interlocked.Increment(ref fetches);
            var give = (int)Math.Min(count, content.Length - offset);
            return Task.FromResult<Stream>(new MemoryStream(content, (int)offset, give, writable: false));
        });

        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new RecordingConversion(), AppCallback.Logger(lines.Add));

        var overRanges = engine.PlanSegments(bytes, SegmentLengths.Of(SegmentSeconds));
        var overFile = engine.PlanSegments(FixtureBytes, SegmentLengths.Of(SegmentSeconds));

        Assert.NotNull(overRanges);
        Assert.NotNull(overFile);
        Assert.Equal(overFile.Count, overRanges.Count);
        Assert.Equal(overFile.GridSeconds, overRanges.GridSeconds);
        for (var i = 0; i < overFile.Count; i++)
            Assert.Equal(overFile.StartOf(i), overRanges.StartOf(i), precision: 6);

        Assert.DoesNotContain(lines, l => l.Contains("walking its clusters", StringComparison.Ordinal));
        Assert.True(fetches > 0, "the range source was never asked for anything — the fake was not used");
        Assert.True(fetches < 64,
            $"{fetches} fetches to plan a {content.Length / 1024} KB file — the adapter has stopped buffering");
    }

    /// <summary>
    /// 🔴 <b>The file's own index and a full cluster walk must produce THE SAME CUTS.</b> Planning from Cues
    /// is what removes the walk from the first request — the walk seeks past every frame in the source and
    /// touches about a third of its pages — and the only thing that makes the shortcut safe is that it
    /// answers identically. A cheaper answer that is a DIFFERENT answer would put every boundary somewhere
    /// the manifest does not claim, silently.
    /// <para>
    /// ⚠ Asserted on a real ffmpeg-muxed file, because a built fixture carries whatever index the builder
    /// writes and would be checking this suite against itself.
    /// </para>
    /// <para>
    /// ⚠ A SPARSE index — one cue per several keyframes — is legal and would legitimately give a COARSER
    /// plan rather than a wrong one, since every boundary it names is still a real keyframe. This fixture
    /// carries a cue per keyframe, so equality is the right assertion HERE; it is not a general law.
    /// </para>
    /// </summary>
    [Fact]
    public void The_files_own_index_and_a_full_walk_agree_about_where_to_cut()
    {
        using var file = File.OpenRead(Fixture);
        var reader = new MatroskaSampleReader(file);
        Assert.True(reader.ReadHeader());

        var video = reader.Tracks.First(t => t.Kind is MediaStreamKind.Video);
        var timeline = SourceTimeline.For(reader.TimestampScaleNs);

        // ffmpeg and mkvmerge both write Cues by default. A null here means the fixture changed, or the
        // reader's own checks rejected an index that is in fact fine — either is worth failing over.
        var fromIndex = reader.KeyFrameTicksFromCues(video.Number);
        Assert.NotNull(fromIndex);
        Assert.True(fromIndex!.Count > 1, "the index named fewer than two keyframes");

        Assert.True(reader.ReadSamples(new HashSet<ulong> { video.Number }));
        var fromWalk = video.Samples.Where(s => s.KeyFrame).Select(s => s.Ticks).ToList();

        Assert.Equal(fromWalk, fromIndex);
        Assert.Equal(SegmentGrid.KeyFrameStarts(fromWalk, timeline, SegmentSeconds),
                     SegmentGrid.KeyFrameStarts(fromIndex, timeline, SegmentSeconds));
    }

    /// <summary>
    /// 🔴 <b>Indexing the source in CHUNKS must give the same decode timeline as indexing it whole.</b>
    /// A run no longer walks every cluster before writing its first fragment — it indexes what it is about
    /// to emit and comes back for more — and the risk that buys is real: <c>SampleTiming.Derive</c> SORTS,
    /// and it takes the presentation shift as a maximum over everything it is given. Derived per chunk, a
    /// B-frame stream could get a different decode order or a different shift on either side of a seam,
    /// which appends without error and plays wrongly.
    /// <para>
    /// ⚠ The control is derived INDEPENDENTLY here — a full walk plus <c>Derive</c> over the whole track,
    /// which is exactly what the engine used to do — rather than comparing the engine against itself.
    /// </para>
    /// <para>
    /// ⚠ This fixture is many clusters, so the run genuinely crosses chunk seams. A built fixture is one
    /// cluster and would exercise none of this.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Indexing_the_source_in_chunks_gives_the_same_decode_timeline_as_indexing_it_whole()
    {
        // ── the control: one walk, one derivation over the whole track ──────────────────────────────────
        long[] wholeTrackDecode;
        using (var file = File.OpenRead(Fixture))
        {
            var reader = new MatroskaSampleReader(file);
            Assert.True(reader.ReadHeader());
            var track = reader.Tracks.First(t => t.Kind is MediaStreamKind.Video);
            Assert.True(reader.ReadSamples(new HashSet<ulong> { track.Number }));

            var timeline = SourceTimeline.For(reader.TimestampScaleNs);
            var presentation = track.Samples.Select(s => s.Ticks * timeline.Factor).ToArray();
            long shift;
            (wholeTrackDecode, _, shift) = SampleTiming.Derive(presentation);
            Assert.True(wholeTrackDecode.Length > 100, "the control derived almost nothing");

            // ⚠ POSITIVE CONTROL. Chunked derivation is only INTERESTING on a reordered stream: with no
            // B-frames decode order IS presentation order and any chunking scheme passes. If this fixture
            // is ever replaced by one without them, this test goes quiet rather than wrong — so it says so.
            var reordered = shift > 0 || presentation.Where((p, i) => p != wholeTrackDecode[i]).Any();
            Assert.True(reordered,
                "this fixture's frames are stored in presentation order, so it cannot exercise chunked "
                + "reordering at all — the claim this test makes needs a B-frame source");
        }

        // ── the subject: the engine as it now runs, indexing as it writes ───────────────────────────────
        var dir = Path.Combine(Path.GetTempPath(), "shenora-chunked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var engine = new DefaultSegmentEngine(new RecordingConversion());
            var plan = engine.PlanSegments(FixtureBytes, SegmentLengths.Of(SegmentSeconds));
            Assert.NotNull(plan);

            using (var run = engine.Start(new SegmentRunRequest(FixtureBytes, dir, HasPicture: true,
                                                                FirstSegment: 0, plan!, Attempt: 0))!)
            {
                Assert.NotNull(run);
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (!run.HasExited && DateTime.UtcNow < deadline) await Task.Delay(25);
                Assert.True(run.HasExited, "the production run did not finish within 120s");
            }

            var segments = Segments(dir);
            Assert.Equal(plan!.Count, segments.Count);

            // Every fragment opens at a decode time the WHOLE-TRACK derivation also produced. A chunk seam
            // that shifted the timeline would put a fragment at a time this set does not contain.
            var known = wholeTrackDecode.ToHashSet();
            var bases = new List<long>();
            foreach (var segment in segments)
            {
                var at = Mp4FragmentReader.BaseDecodeTime(segment, DefaultSegmentEngine.VideoTrackId);
                Assert.NotNull(at);
                Assert.True(known.Contains(at!.Value),
                    $"{Path.GetFileName(segment)} opens at {at} — not a decode time the whole-track "
                    + "derivation produced, so a chunk seam moved the timeline");
                bases.Add(at.Value);
            }

            // ...and they advance. A seam that clamped backwards would repeat or reverse one.
            Assert.Equal(bases.OrderBy(b => b).ToList(), bases);
            Assert.Equal(bases.Distinct().Count(), bases.Count);
            Assert.Equal(wholeTrackDecode[0], bases[0]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a temp dir */ }
        }
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
        var plan = engine.PlanSegments(FixtureBytes, SegmentLengths.Of(SegmentSeconds));
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
