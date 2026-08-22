using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// The segment tier over REAL files carrying the three shapes the kit's own muxer never emits: a soundtrack
/// that starts seconds in, a picture track with a single keyframe, and laced sound.
///
/// <para>
/// 🔴 <b><see cref="SegmentRunWriterTests"/> builds these shapes synthetically, and a built shape is an
/// assumption about the shape.</b> It drives the writer directly with hand-made <c>MatroskaTrack</c>s, so it
/// proves the PUMP and says nothing about whether the demuxer produces that shape from a file — which is
/// where the tier's faults sat. These read a real ffmpeg-muxed file end to end, so the reader and the writer
/// have to agree about it.
/// </para>
/// <para>
/// The fixtures are regenerated with (ffmpeg 8.0, <c>testsrc2</c> so nothing is licensed):
/// <code>
/// ffmpeg -f lavfi -i testsrc2=size=160x90:rate=12:duration=10 -c:v libx264 -preset veryslow -crf 34 \
///        -g 24 -bf 3 -pix_fmt yuv420p v.mkv
/// ffmpeg -f lavfi -i sine=frequency=440:duration=5:sample_rate=48000 -c:a aac -b:a 24k a.mka
/// ffmpeg -i v.mkv -itsoffset 4 -i a.mka -map 0:v -map 1:a -c copy -copyts clip-late-audio.mkv
/// ffmpeg -f lavfi -i testsrc2=size=160x90:rate=12:duration=20 -c:v libx264 -preset veryslow -crf 34 \
///        -g 100000 -keyint_min 100000 -sc_threshold 0 -bf 3 -pix_fmt yuv420p clip-one-keyframe.mkv
/// </code>
/// ⚠ <c>-itsoffset</c> with <c>-copyts</c> is what puts the sound's FIRST PACKET at 4 s; <c>adelay</c> would
/// have padded it with silence from zero, which is a different file and tests nothing.
/// </para>
/// <para>
/// 🔴 <b>The laced one needs a different muxer — ffmpeg writes no lacing at all</b>, so every other clip in
/// this tree is unlaced and the tie-spreading path had never met a real file. mkvmerge laces by default;
/// its <c>DefaultDuration</c> then has to go, because the reader uses it to space laced frames and the
/// ties never reach the writer while it is there (<c>MatroskaSampleReader</c> says so at its lacing loop).
/// <code>
/// ffmpeg -f lavfi -i testsrc2=size=160x90:rate=12:duration=8 -f lavfi -i sine=frequency=440:duration=8 \
///        -c:v libx264 -preset veryslow -crf 34 -g 24 -bf 3 -pix_fmt yuv420p -c:a aac -b:a 24k -shortest pre.mkv
/// mkvmerge -o clip-laced-audio.mkv pre.mkv
/// mkvpropedit clip-laced-audio.mkv --edit track:a1 --delete default-duration
/// </code>
/// Measured on the result: 376 sound frames in 48 blocks — 47 EBML-laced and one Xiph-laced. The third
/// scheme needs equal-sized frames, so <c>clip-fixed-lacing.mkv</c> is CBR MP3, which mkvmerge fixed-laces.
/// All three parsers now run against bytes this repo did not write.
/// <para>
/// ⚠ <b>A laced PICTURE track is not covered and is not reachable here</b>: lacing is legal on any track,
/// and mkvmerge laces none of the video it was given. It stays covered by hand-built blocks alone.
/// </para>
/// </para>
/// </summary>
public class RealSourceShapeTests : IDisposable
{
    private const double SegmentSeconds = 2.0;

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "media", name);

    private static string LateAudio => Fixture("clip-late-audio.mkv");
    private static string OneKeyFrame => Fixture("clip-one-keyframe.mkv");
    private static string LacedAudio => Fixture("clip-laced-audio.mkv");
    private static string FixedLacing => Fixture("clip-fixed-lacing.mkv");

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shenora-shape-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// 🔴 <b>Captured per TEST, not into a static.</b> A <c>static readonly</c> holding the production
    /// default is <c>beforefieldinit</c>, so the runtime may not initialize it until the first test's
    /// <c>Dispose</c> READS it — by which point that test has already lowered the bound, and the "default"
    /// captured is the lowered value. Every later test then runs under a bound it never asked for, and
    /// silently: a forced cut is a supported behaviour, so nothing throws.
    /// </summary>
    private readonly long _maxPendingBytes = SegmentRunWriter.MaxPendingBytes;

    public RealSourceShapeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SegmentRunWriter.MaxPendingBytes = _maxPendingBytes;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Declines everything, so a copy that quietly became a conversion fails rather than passes.</summary>
    private sealed class NoConversion : IMediaStreamConversion
    {
        public bool CanConvert(MediaStreamKind kind, string codec) => false;
        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate) => null;
    }

    /// <summary>
    /// The run's own per-channel accounting, which is what tells a DROPPED sample from one that was never
    /// read — see <c>SegmentRunWriter.Run</c>'s closing report. Worth carrying into a failure message: a
    /// byte-count mismatch on its own names neither end of the pipeline.
    /// </summary>
    private static string Accounting(List<string> log) =>
        string.Join("\n", log.Where(l => l.Contains("segments:", StringComparison.Ordinal)));

    private static List<string> Segments(string dir) =>
        [.. Directory.GetFiles(dir, "seg*" + SegmentRunRequest.SegmentExtension)
                     .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)];

    /// <summary>
    /// Where the SPILLED run's bytes are left for <c>dev.mjs media-decode</c>. A fixed path rather than a
    /// temp one for the reason <see cref="RealSourceSegmentTests"/> gives: the decode probe is a separate
    /// process, run later, and this suite may not depend on a decoder itself.
    /// <para>
    /// 🔴 A multi-fragment segment is what this design produces and nothing here can say a DECODER takes
    /// one — every assertion below reads boxes.
    /// </para>
    /// <para>
    /// ⚠ Its OWN directory, beside <c>_media-real</c> rather than inside it: that one is cleared wholesale by
    /// the run that owns it, so a subdirectory there survives or not depending on which class xunit happens
    /// to run second.
    /// </para>
    /// </summary>
    private static string Artifacts
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shenora.slnx"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "devtools", "_media-spill");
        }
    }

    /// <summary>
    /// Per-sample durations from one <c>trun</c> in a fragment — <paramref name="occurrence"/> follows the
    /// channel order the init segment declared, so 0 is the picture and 1 the sound.
    /// </summary>
    private static List<uint> SampleDurations(string segmentPath, int occurrence)
    {
        var bytes = File.ReadAllBytes(segmentPath);
        var at = IndexOf(bytes, "trun", occurrence);
        Assert.True(at > 0, $"the fragment carries no trun #{occurrence}");
        var body = at + 4;                                     // past the fourcc, at version/flags
        var count = (int)U32(bytes, body + 4);
        var durations = new List<uint>(count);
        for (var i = 0; i < count; i++) durations.Add(U32(bytes, body + 12 + (i * 12)));
        return durations;
    }

    private static uint U32(byte[] b, int at) =>
        ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

    private static int IndexOf(byte[] haystack, string fourcc, int occurrence)
    {
        var needle = fourcc.Select(c => (byte)c).ToArray();
        var seen = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++) if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit && seen++ == occurrence) return i;
        }
        return -1;
    }

    /// <summary>Read one track's samples straight from the file — the control every writer claim is measured against.</summary>
    private static List<MatroskaSample> SamplesOf(string path, MediaStreamKind kind)
    {
        using var file = File.OpenRead(path);
        var reader = new MatroskaSampleReader(file);
        Assert.True(reader.ReadHeader(), $"the demuxer would not read {Path.GetFileName(path)}");
        var track = reader.Tracks.First(t => t.Kind == kind);
        Assert.True(reader.ReadSamples(new HashSet<ulong> { track.Number }));
        return track.Samples;
    }

    private async Task<List<string>> RunWhole(string fixture, List<string> log, string? into = null)
    {
        var dir = into ?? _dir;
        var engine = new DefaultSegmentEngine(new NoConversion(), AppCallback.Logger(log.Add));
        var source = MediaByteSource.ForFile(fixture);
        var plan = engine.PlanSegments(source, SegmentLengths.Of(SegmentSeconds));
        Assert.NotNull(plan);

        using var run = engine.Start(new SegmentRunRequest(
            source, dir, HasPicture: true, FirstSegment: 0, plan!, Attempt: 0));
        Assert.NotNull(run);

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!run!.HasExited && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(run.HasExited, "the run did not finish within 120s");

        return Segments(dir);
    }


    /// <summary>
    /// 🔴 <b>The positive control.</b> Both claims below are about a SHAPE, and a fixture regenerated without
    /// it would leave them passing while testing the ordinary case — the failure mode that makes a corpus
    /// worthless. Asserted through the kit's own demuxer, because that is the reader under test.
    /// </summary>
    [Fact]
    public void The_shape_fixtures_are_present_and_still_carry_the_shapes()
    {
        Assert.True(File.Exists(LateAudio), $"missing fixture: {LateAudio}");
        Assert.True(File.Exists(OneKeyFrame), $"missing fixture: {OneKeyFrame}");

        // Sound arrives SECONDS after the picture — past the first segment boundary, which is the point.
        var picture = SamplesOf(LateAudio, MediaStreamKind.Video);
        var sound = SamplesOf(LateAudio, MediaStreamKind.Audio);
        Assert.Equal(0, picture[0].Ticks);
        Assert.InRange(sound[0].Ticks, (long)(SegmentSeconds * 1000) + 1, 6_000);

        // Exactly one keyframe, so no cut the plan asks for can ever be honoured.
        var uncut = SamplesOf(OneKeyFrame, MediaStreamKind.Video);
        Assert.Equal(1, uncut.Count(s => s.KeyFrame));
        Assert.True(uncut.Count > 100, "the uncut fixture is too short to hold anything back");

        // 🔴 Sound frames SHARING a timestamp, which is what lacing produces and the only reason
        // SpreadTies exists. Muxed by mkvmerge because ffmpeg's Matroska muxer writes no lacing at all —
        // measured: this file laces 376 frames into 48 blocks, every other clip in the tree laces none.
        var laced = SamplesOf(LacedAudio, MediaStreamKind.Audio);
        var tied = laced.GroupBy(s => s.Ticks).Count(g => g.Count() > 1);
        Assert.True(tied > 10,
            $"only {tied} tied timestamp(s) in {laced.Count} sound frames — this fixture is not laced, so "
            + "the durations test below would pass against any file");
    }

    /// <summary>
    /// 🔴 <b>A soundtrack that produces nothing until after the init segment is written must still be
    /// DECLARED there, or every later fragment drops it and the film is silent end to end.</b> Nothing
    /// downstream notices, because <c>VerifyPicture</c> only looks for picture.
    /// <para>
    /// ⚠ Asserted as bytes that ARRIVED, not as a log line that stayed absent: this run writes several
    /// fragments and a test that only checks for silence in the log passes when the whole soundtrack is
    /// missing for some other reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_soundtrack_that_starts_LATE_in_a_real_file_still_reaches_the_fragments()
    {
        var log = new List<string>();
        var segments = await RunWhole(LateAudio, log);
        Assert.True(segments.Count > 2, $"only {segments.Count} segment(s) — nothing was held back to test");

        var picture = segments.Select(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId)).ToList();
        var sound = segments.Select(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.AudioTrackId)).ToList();

        // The picture runs throughout; the sound cannot be in the opening fragment, and MUST be in a later one.
        Assert.All(picture, bytes => Assert.True(bytes > 0, "a fragment carries no picture"));
        Assert.Equal(0, sound[0]);
        Assert.True(sound.Any(bytes => bytes > 0),
            "the late soundtrack never reached a fragment — it was dropped for the whole run: "
            + string.Join(", ", segments.Select((s, i) => $"{Path.GetFileName(s)}={sound[i]}")));

        // ...and ALL of it did. The declaration bug lost the opening frames of a track, not the whole track,
        // so a "some sound arrived" assertion is satisfied by a run that dropped its first fragment's worth.
        var expected = SamplesOf(LateAudio, MediaStreamKind.Audio).Sum(s => (long)s.Length);
        Assert.True(expected == sound.Sum(),
            $"the fragments carry {sound.Sum()} of the source's {expected} sound bytes.\n{Accounting(log)}");

        Assert.DoesNotContain(log, l => l.Contains("dropping its samples", StringComparison.Ordinal));
    }

    /// <summary>
    /// 🔴 <b>FIXED lacing, from a real muxer, divides into the frames it packed.</b> The third scheme:
    /// one frame count, and the remaining bytes split evenly between them. Reached by no fixture before
    /// this one — <c>MatroskaSampleReader</c>'s <c>ReadFixedLacing</c> had only ever seen hand-built blocks.
    /// <para>
    /// ⚠ <b>Read-side only, and deliberately.</b> Only AAC is carriable as sound (<c>Mp4Carriage</c>), so a
    /// CBR MP3 track — the thing mkvmerge fixed-laces — is never COPIED into a fragment; a writer-side test
    /// would be testing the converter. The parse is the part that had no real-file coverage.
    /// </para>
    /// <para>
    /// ⚠ <b>The count is the discriminator.</b> This file is 23 blocks holding 168 frames, so a reader that
    /// ignored lacing would answer 23 and still look like it worked — every other assertion here would pass
    /// against it. Regenerate with:
    /// <code>
    /// ffmpeg -f lavfi -i sine=frequency=440:duration=4:sample_rate=48000 -c:a libmp3lame -b:a 48k \
    ///        -write_xing 0 cbr.mp3
    /// mkvmerge -o clip-fixed-lacing.mkv cbr.mp3
    /// </code>
    /// </para>
    /// </summary>
    [Fact]
    public void FIXED_laced_sound_from_a_real_muxer_divides_into_its_frames()
    {
        var samples = SamplesOf(FixedLacing, MediaStreamKind.Audio);

        Assert.True(samples.Count > 100,
            $"{samples.Count} sound frames — the file packs 168 into 23 blocks, so this is the block count "
            + "rather than the frame count: the lacing was not parsed at all");

        // Fixed lacing means EQUAL sizes by definition, so an uneven split is the failure it hides.
        var size = samples[0].Length;
        Assert.All(samples, s => Assert.Equal(size, s.Length));

        // ...and the frames tile the payload rather than overlapping it. A division that got the count right
        // and the offsets wrong reads the same bytes twice and still answers the assertions above.
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].Offset >= samples[i - 1].Offset + samples[i - 1].Length,
                $"frame {i} at {samples[i].Offset} overlaps frame {i - 1} "
                + $"at {samples[i - 1].Offset}+{samples[i - 1].Length}");
        }
    }

    /// <summary>
    /// 🔴 <b>LACED sound out of a real muxer keeps its frame durations.</b> Lacing packs several audio frames
    /// into one Matroska block and they arrive sharing that block's timestamp; timing derived straight from
    /// tied times gives zero-length entries, which <c>Flush</c> then clamps to 1 — a soundtrack whose every
    /// frame claims to last one tick, playing as a fraction of a second of noise while every box validates.
    /// <para>
    /// ⚠ <b>The synthetic twin builds the ties by hand</b>
    /// (<c>SegmentRunWriterTests.LACED_audio_keeps_real_frame_durations_rather_than_zero</c>), so it proves
    /// <c>SpreadTies</c> and not that a real file reaches it. This one arrives through
    /// <c>MatroskaSampleReader</c>'s Xiph and EBML lacing parsers, which no committed fixture had ever
    /// exercised — every other clip in the tree is unlaced, ffmpeg's muxer writing no lacing at all.
    /// </para>
    /// <para>
    /// ⚠ <b>The clamp is why "non-zero" is not the assertion.</b> A tied frame's zero duration reaches the
    /// file as 1, so <c>d &gt; 0</c> passes with the fix removed. The real gap is asserted instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LACED_sound_from_a_real_muxer_keeps_its_frame_durations()
    {
        var log = new List<string>();
        var segments = await RunWhole(LacedAudio, log);
        Assert.NotEmpty(segments);

        // trun #1 is the sound — the channel order the init segment declared, picture first.
        var durations = SampleDurations(segments[0], occurrence: 1);
        Assert.True(durations.Count > 20, $"only {durations.Count} sound frames in the first fragment");

        // AAC at 48 kHz is 1024 samples a frame — 21.3 ms, on a millisecond timeline. The last entry has no
        // successor and falls back, so it is excluded; every other frame must carry a real gap.
        var real = durations.Take(durations.Count - 1).ToList();
        Assert.DoesNotContain(1u, real);
        Assert.All(real, d => Assert.InRange(d, 20u, 23u));
    }

    /// <summary>
    /// 🔴 <b>A source whose picture never reaches a second keyframe SPILLS to disk, and spilling must not
    /// cost a sample.</b> A cut needs a keyframe past the segment end (<c>CutIfDue</c>), so a single-keyframe
    /// source offers none and <c>Pending</c> would otherwise grow to the whole file — while the manifest
    /// lists exactly <c>Plan.Count</c> segments, so there is no new number the spill could use. It therefore
    /// appends another fragment to the SAME segment.
    /// <para>
    /// ⚠ <b>Which is why the count is asserted as EQUAL to the plan, not as more than one.</b> The defect
    /// this pins wrote every spill under the segment already published and the rename overwrote it: 240 of
    /// 240 samples read and emitted, 5,253 of 67,672 bytes surviving, in a file that still parsed. A
    /// "more than one segment" assertion would have been satisfied by the broken behaviour on a grid plan.
    /// </para>
    /// <para>
    /// ⚠ The bound is lowered rather than met: proving it at the real 64 MB means allocating ~150 MB inside a
    /// unit test, and a few KB proves the same branch. Restored in <see cref="Dispose"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_real_source_with_no_cut_point_spills_to_disk_without_losing_a_sample()
    {
        SegmentRunWriter.MaxPendingBytes = 8 * 1024;

        var dir = Artifacts;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var log = new List<string>();
        var segments = await RunWhole(OneKeyFrame, log, into: dir);

        // The spill has to have HAPPENED, or every assertion below is about an ordinary run.
        Assert.Contains(log, l => l.Contains("written out in parts", StringComparison.Ordinal));

        // 🔴 EVERY sample byte reaches the segment, and the run produces exactly the segment the plan
        // promised — a spill that took a number of its own would be a segment no manifest lists.
        var expected = SamplesOf(OneKeyFrame, MediaStreamKind.Video).Sum(s => (long)s.Length);
        var written = segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId));
        Assert.True(segments.Count == 1 && written == expected,
            $"{segments.Count} segment(s) carrying {written} of the source's {expected} sample bytes.\n"
            + Accounting(log));

        // It opens where the source does — the spill appends fragments, it does not move the segment's start.
        Assert.Equal(0, Mp4FragmentReader.BaseDecodeTime(segments[0], DefaultSegmentEngine.VideoTrackId));

        // ...and nothing is left half-written: a part-file surviving the run is a segment nobody publishes.
        Assert.Empty(Directory.GetFiles(dir, "*" + SegmentRunRequest.PartialExtension));

        // ⚠ Merged and LEFT BEHIND, because the claim this suite cannot make is the one that matters most:
        // that a decoder takes a segment carrying several fragments. `dev.mjs media-decode` asks it.
        var plan = SegmentPlan.Cuts([0], TimeSpan.FromSeconds(20));
        Assert.NotNull(plan);
        await SegmentMerge.WriteAsync(SegmentMerge.Parts(dir, plan!), Path.Combine(dir, "spill.mp4"),
                                      CancellationToken.None);
    }
}
