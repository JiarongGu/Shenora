using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// <c>SegmentRunWriter</c> against SYNTHETIC track shapes.
/// <para>
/// 🔴 <b>Nothing constructed this type before, which is why its defect cluster survived.</b> The
/// 2026-08-21 review found the root cause: the segment tier had only ever been exercised against media
/// THIS KIT produced, so every shape a foreign muxer emits — a track that starts late, lacing, a source
/// with no cut point — went untested. These build the shapes directly rather than muxing a file, because
/// the shape is the thing under test: a fixture would carry it incidentally and prove less.
/// </para>
/// </summary>
public class SegmentRunWriterTests : IDisposable
{
    /// <summary>Matroska ticks are MILLISECONDS here, and the mp4 timescale matches, so Factor is 1.</summary>
    private static readonly SourceTimeline Timeline = new(1000, 1);

    private const int SampleBytes = 16;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shenora-runwriter-" + Guid.NewGuid().ToString("N")[..8]);

    /// <inheritdoc cref="RealSourceShapeTests"/>
    /// <remarks>
    /// 🔴 Captured per TEST rather than into a <c>static readonly</c>, which is <c>beforefieldinit</c> and so
    /// may not initialize until the first <c>Dispose</c> READS it — capturing the bound that test just
    /// lowered, and leaving every later test under it. Caught by the shape suite, whose fixtures are large
    /// enough to notice; these synthetic ones all fit under the lowered bound and passed either way.
    /// </remarks>
    private readonly long _maxPendingBytes = SegmentRunWriter.MaxPendingBytes;

    public SegmentRunWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SegmentRunWriter.MaxPendingBytes = _maxPendingBytes;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Captures what the engine reported, which is where a dropped track announces itself.</summary>
    private sealed class Recorder : ILogger
    {
        public List<string> Lines { get; } = [];
        public string All => string.Join("\n", Lines);
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, error));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// A track whose samples are evenly spaced from <paramref name="startMs"/>. Offsets are distinct so a
    /// mis-addressed read is visible rather than silently reading the same bytes twice.
    /// </summary>
    private static MatroskaTrack Track(MediaStreamKind kind, int count, long stepMs, long startMs = 0,
                                       int keyEvery = 1, int offsetBase = 0)
    {
        var track = new MatroskaTrack
        {
            Number = kind is MediaStreamKind.Video ? 1u : 2u,
            Kind = kind,
            CodecId = kind is MediaStreamKind.Video ? "V_MPEG4/ISO/AVC" : "A_AAC",
            CodecPrivate = kind is MediaStreamKind.Video ? Mp4RemuxerTests.AvcConfig : Mp4RemuxerTests.AacConfig,
            Width = kind is MediaStreamKind.Video ? 640 : 0,
            Height = kind is MediaStreamKind.Video ? 360 : 0,
            SampleRate = kind is MediaStreamKind.Video ? 0 : 48_000,
            Channels = kind is MediaStreamKind.Video ? 0 : 2,
            DefaultDurationNs = stepMs * 1_000_000,
        };
        for (var i = 0; i < count; i++)
        {
            track.Samples.Add(new MatroskaSample(
                Offset: (offsetBase + i) * SampleBytes,
                Length: SampleBytes,
                Ticks: startMs + (i * stepMs),
                KeyFrame: kind is MediaStreamKind.Audio || i % keyEvery == 0));
        }
        return track;
    }

    private (Recorder Log, DefaultSegmentEngine Engine, SegmentRunRequest Request) Fixture(double totalSeconds)
    {
        var log = new Recorder();
        var engine = new DefaultSegmentEngine(conversion: null, log);
        var request = new SegmentRunRequest(
            Source: new MediaByteSource { Label = "synthetic", Open = _ => new MemoryStream() },
            Directory: _dir,
            HasPicture: true,
            FirstSegment: 0,
            Plan: SegmentPlan.Grid(2.0, TimeSpan.FromSeconds(totalSeconds)),
            Attempt: 1);
        return (log, engine, request);
    }

    /// <summary>Sample bytes for every offset any track will ask for.</summary>
    private static Stream Source(int samples) => new MemoryStream(new byte[(samples + 8) * SampleBytes]);


    /// <summary>
    /// Per-sample durations from a fragment's ONE <c>trun</c> — audio-only runs, so there is no ambiguity
    /// about which track is being read. Records are <c>duration, size, flags</c> (12 bytes) after the
    /// box's version/flags, sample count and data offset.
    /// </summary>
    private static List<uint> SampleDurations(string segmentPath, int occurrence = 0)
    {
        var bytes = File.ReadAllBytes(segmentPath);
        var at = IndexOf(bytes, "trun", occurrence);
        Assert.True(at > 0, "the fragment carries no trun");
        var body = at + 4;                                     // past the fourcc, at version/flags
        var count = (int)U32(bytes, body + 4);
        var durations = new List<uint>(count);
        for (var i = 0; i < count; i++) durations.Add(U32(bytes, body + 12 + (i * 12)));
        return durations;
    }

    private static uint U32(byte[] b, int at) =>
        ((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

    private static int IndexOf(byte[] haystack, string fourcc, int occurrence = 0)
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


    [Fact]
    public void The_FIRST_track_to_run_out_still_gets_its_real_gap_not_the_declared_one()
    {
        // 🔴 More source is indexed when the track about to be consumed is one sample from its end, so the
        // frame being written has a successor to be timed against. The condition asked whether EVERY track
        // was near its end — and two tracks rarely run out together, because a soundtrack has far more
        // frames than a picture track. So the picture track was consumed while the sound still had plenty,
        // no extension happened, and its frame took the DECLARED duration instead of its real gap.
        var (log, engine, request) = Fixture(totalSeconds: 20);
        using var writer = new SegmentRunWriter(engine, request, Timeline);

        // Picture: 4 frames, declared 200 ms. Sound: 40 frames — nowhere near ITS end.
        var video = Track(MediaStreamKind.Video, count: 4, stepMs: 200, keyEvery: 1);
        var audio = Track(MediaStreamKind.Audio, count: 40, stepMs: 100, offsetBase: 100);

        // The frame `extend` reveals sits 500 ms after the last known one — NOT the declared 200 ms, so
        // "took the fallback" and "took the real gap" are distinguishable.
        var extended = false;
        bool Extend(double reach)
        {
            if (extended) return false;
            extended = true;
            var last = video.Samples[^1];
            video.Samples.Add(new MatroskaSample(last.Offset + SampleBytes, SampleBytes, last.Ticks + 500, true));
            return true;
        }

        writer.Run(
            Source(200), new SegmentTrack(video, Copy: true), new SegmentTrack(audio, Copy: true),
            from: 0, startSeconds: 0, conversionOf: null, extend: Extend, CancellationToken.None);

        Assert.True(extended, "the run never asked for more source, so the guard never fired");

        // trun[0] is the picture track — `data` follows the channel order, video first.
        var segments = Directory.GetFiles(_dir, "seg*" + SegmentRunRequest.SegmentExtension).Order().ToList();
        var picture = segments.SelectMany(seg => SampleDurations(seg, occurrence: 0)).ToList();
        Assert.Contains(500u, picture);
    }

    [Fact]
    public void LACED_audio_keeps_real_frame_durations_rather_than_zero()
    {
        // 🔴 Lacing packs several audio frames into ONE Matroska block, and they arrive sharing that
        // block's timestamp. Deriving timing straight from tied times gives zero-length `stts` entries —
        // a soundtrack whose every frame claims to last no time, which plays as a fraction of a second of
        // noise while every box in the file still validates. `Mp4Remuxer` spreads ties before deriving;
        // this writer did not, on the same data, two files away.
        var (log, engine, request) = Fixture(totalSeconds: 4);
        using var writer = new SegmentRunWriter(engine, request, Timeline);

        // 24 frames in groups of 4 sharing a timestamp: 0,0,0,0, 100,100,100,100, ... — real lacing.
        var audio = new MatroskaTrack
        {
            Number = 2, Kind = MediaStreamKind.Audio, CodecId = "A_AAC",
            CodecPrivate = Mp4RemuxerTests.AacConfig, SampleRate = 48_000, Channels = 2,
            DefaultDurationNs = 25 * 1_000_000,                 // 25 ms per frame, four to a 100 ms block
        };
        for (var i = 0; i < 24; i++)
            audio.Samples.Add(new MatroskaSample(i * SampleBytes, SampleBytes, (i / 4) * 100L, KeyFrame: true));

        writer.Run(
            Source(64), video: null, new SegmentTrack(audio, Copy: true),
            from: 0, startSeconds: 0, conversionOf: null, extend: _ => false, CancellationToken.None);

        var segments = Directory.GetFiles(_dir, "seg*" + SegmentRunRequest.SegmentExtension);
        Assert.NotEmpty(segments);

        var durations = SampleDurations(segments.Order().First());

        // ⚠ ASSERT THE REAL GAP, NOT "non-zero". `Flush` writes `Math.Max(duration, 1)`, so an untied
        // frame's zero duration arrives as 1 and a `d > 0` assertion can NEVER fail — a vacuous test that
        // passes with the fix removed. The declared frame duration here is 25 ticks; a laced frame that
        // kept its tie shows up as the clamp instead.
        var real = durations.Take(durations.Count - 1).ToList();
        Assert.DoesNotContain(1u, real);
        Assert.All(real, d =>
            Assert.True(d == 25, $"a laced frame lost its duration: [{string.Join(", ", durations)}]"));
    }

    [Fact]
    public void A_track_that_starts_LATE_is_still_declared_and_carried()
    {
        // 🔴 The init segment is written beside the FIRST fragment and declares only the tracks that had
        // produced by then. A copied track produces from its first frame while an encoder may hold a whole
        // segment's worth, so a track whose samples begin after segment 0 was undeclared — and every later
        // fragment then DROPPED it, for the whole run. `VerifyPicture` only checks for picture, so nothing
        // downstream notices: a film that is silent from beginning to end, with no error anywhere.
        var (log, engine, request) = Fixture(totalSeconds: 12);
        using var writer = new SegmentRunWriter(engine, request, Timeline);

        var video = Track(MediaStreamKind.Video, count: 60, stepMs: 200, keyEvery: 10);
        // Sound arrives only from 5 s — past segment 0 and segment 1.
        var audio = Track(MediaStreamKind.Audio, count: 40, stepMs: 100, startMs: 5_000, offsetBase: 100);

        writer.Run(
            Source(200), new SegmentTrack(video, Copy: true), new SegmentTrack(audio, Copy: true),
            from: 0, startSeconds: 0, conversionOf: null, extend: _ => false, CancellationToken.None);

        Assert.DoesNotContain("dropping its samples", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lead_track_that_never_cuts_does_not_buffer_the_whole_source()
    {
        // 🔴 `CutIfDue` only cuts at a KEYFRAME past the segment end, so a lead track with one keyframe at
        // the start never cuts and `Pending` grows to the whole source — then is doubled by the final
        // flush. On a phone that is an OOM, and `StalledWithoutPicture` explicitly cannot stop it.
        var (log, engine, request) = Fixture(totalSeconds: 120);
        using var writer = new SegmentRunWriter(engine, request, Timeline);

        // 600 frames, ONE keyframe — the shape a long GOP or a damaged stream produces.
        var video = Track(MediaStreamKind.Video, count: 600, stepMs: 200, keyEvery: 10_000);

        // The guard trips at 64 MB in production; proving it there would mean allocating ~150 MB in a unit
        // test, and 4 KB proves the same branch. Restored in Dispose.
        SegmentRunWriter.MaxPendingBytes = 4 * 1024;

        writer.Run(
            Source(700), new SegmentTrack(video, Copy: true), audio: null,
            from: 0, startSeconds: 0, conversionOf: null, extend: _ => false, CancellationToken.None);

        // 🔴 THE EVIDENCE IS FRAGMENTS, NOT FILES. The bytes must have reached disk in several goes — that is
        // what says memory was bounded — and they must all be in ONE segment, because no keyframe ever
        // offered a boundary and the manifest lists no other number to write under. This asserted `> 1
        // segment FILE` until a real single-keyframe source showed what that meant: each spill republished
        // the segment and the rename overwrote it, losing 92 % of the run.
        var written = Directory.GetFiles(_dir, "seg*" + SegmentRunRequest.SegmentExtension);
        Assert.Single(written);

        var bytes = File.ReadAllBytes(written[0]);
        var fragments = 0;
        while (IndexOf(bytes, "moof", fragments) >= 0) fragments++;
        Assert.True(fragments > 1, $"the segment holds {fragments} fragment(s) — it buffered the whole run");

        // Nothing was dropped on the way: every sample's bytes are in that one segment.
        Assert.Equal(600 * SampleBytes,
                     Mp4FragmentReader.SampleBytes(written[0], DefaultSegmentEngine.VideoTrackId));
    }
}
