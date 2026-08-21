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

    public SegmentRunWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SegmentRunWriter.MaxPendingBytes = DefaultMaxPendingBytes;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static readonly long DefaultMaxPendingBytes = SegmentRunWriter.MaxPendingBytes;

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

        // It must still have produced segments rather than one enormous one at the end.
        var written = Directory.GetFiles(_dir, "seg*" + SegmentRunRequest.SegmentExtension);
        Assert.True(written.Length > 1,
            $"a source with no cut point produced {written.Length} segment(s) — it buffered the whole run");
    }
}
