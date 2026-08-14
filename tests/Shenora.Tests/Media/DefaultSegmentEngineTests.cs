using Shenora.Modules.Media;
using Shenora.Tests.TestSupport;
using static Shenora.Tests.Media.Mp4RemuxerTests;

namespace Shenora.Tests.Media;

/// <summary>
/// The kit's default <see cref="ISegmentEngine"/> (D71 piece 3.2b) driven end to end by a FAKE
/// <see cref="IMediaStreamConversion"/> over a built Matroska source.
///
/// <para>
/// 🔴 <b>The loop is testable precisely because the codec is a seam.</b> The real encoders live on Android
/// and iOS and this suite runs on Windows, so a run driven by the platform would be untestable here — but
/// nothing in the pump knows which codec it is holding. Substituting one turns "needs a device" into "needs
/// a fake", which is the same move that made the pool, the drop zone and the session request-filter
/// provable. What still needs a device is whether the PLATFORM's codecs behave as this fake does; that is
/// stated in TASKS.md rather than implied by a green suite here.
/// </para>
/// <para>
/// The fixture builder is <see cref="Mp4RemuxerTests"/>'s, shared rather than copied — the reason that
/// builder became <c>internal</c> in the first place.
/// </para>
/// </summary>
public class DefaultSegmentEngineTests
{
    /// <summary>Frames at a steady 250 ms, so a 1-second grid is exactly four frames.</summary>
    private const int FrameMs = 250;

    private static MemoryStream Source(int frames, bool withAudio = true)
    {
        var blocks = new List<byte[]>();
        for (var i = 0; i < frames; i++)
        {
            // Every 4th frame is a keyframe: one per second at 250 ms spacing, mirroring what the kit's
            // encoders are configured to emit.
            blocks.Add(SimpleBlock(1, (short)(i * FrameMs), keyFrame: i % 4 == 0, Frame(i, 40)));
            if (withAudio) blocks.Add(SimpleBlock(2, (short)(i * FrameMs), keyFrame: true, Frame(i, 12)));
        }

        var tracks = withAudio ? new[] { VideoTrack(), AudioTrack() } : [VideoTrack()];
        return Mkv(Info(frames * FrameMs), tracks, Cluster(0, [.. blocks]));
    }

    private static string WriteSource(TempDir dir, int frames, bool withAudio = true)
    {
        var path = dir.Combine("source.mkv");
        using var mkv = Source(frames, withAudio);
        File.WriteAllBytes(path, mkv.ToArray());
        return path;
    }

    private static SegmentRunRequest Request(string source, string directory, int first = 0, double seconds = 1.0,
                                             bool hasPicture = true, int attempt = 0)
        => new(source, directory, hasPicture, first, seconds, attempt);

    /// <summary>Run to completion, or fail loudly rather than hang — a wedged pump must not stall the suite.</summary>
    private static void RunToCompletion(ISegmentRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!run.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(run.HasExited, "the production run did not finish within 10s");
    }

    // ── the answers that need no run ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A shell with no codecs says so, rather than starting a run that produces nothing. This is the DESKTOP
    /// answer today: `Shenora.Windows` implements no `IMediaStreamConversion`.
    /// </summary>
    [Fact]
    public void Without_a_conversion_the_engine_is_unavailable_and_starts_nothing()
    {
        var engine = new DefaultSegmentEngine(conversion: null);

        Assert.False(engine.IsAvailable);
        Assert.Null(engine.Start(Request("x.mkv", "d")));
        Assert.Contains("no segment engine", engine.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔴 A fractional grid is refused BEFORE any work, because the segments it would produce play correctly
    /// and only misbehave when somebody seeks — see <see cref="SegmentGrid"/>.
    /// </summary>
    [Fact]
    public void A_grid_that_cannot_land_on_a_keyframe_is_refused_before_any_work()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        Assert.Null(engine.Start(Request(WriteSource(dir, 8), dir.Root, seconds: 2.5)));
        Assert.Empty(Directory.GetFiles(dir.Root, "*.m4s"));
    }

    /// <summary>
    /// The <c>Attempt</c> ladder exists for an engine with a second encoder to offer. This one has whatever
    /// the platform gave it, so a retry would re-run identical work and fail identically — null tells the
    /// caller to stop asking rather than looping.
    /// </summary>
    [Fact]
    public void A_second_attempt_has_no_candidate_left()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        Assert.Null(engine.Start(Request(WriteSource(dir, 8), dir.Root, attempt: 1)));
    }

    [Fact]
    public void Duration_and_picture_come_from_the_source()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        var source = WriteSource(dir, 8);

        Assert.NotNull(engine.DurationOf(source));
        Assert.True(engine.HasPicture(source));
        // An unreadable source is an absent answer, not a throw — both members promise that.
        Assert.Null(engine.DurationOf(dir.Combine("missing.mkv")));
        Assert.False(engine.HasPicture(dir.Combine("missing.mkv")));
    }

    // ── producing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole loop: source in, an init segment and numbered fragments out, each readable by the reader
    /// that answers <see cref="ISegmentEngine.HasRenderedPicture"/>.
    /// </summary>
    [Fact]
    public void It_writes_an_init_segment_and_numbered_fragments()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        using (var run = engine.Start(Request(WriteSource(dir, frames: 16), dir.Root))!)
        {
            Assert.NotNull(run);
            RunToCompletion(run);
        }

        Assert.True(File.Exists(dir.Combine("init.mp4")), "no init segment was written");
        var segments = Directory.GetFiles(dir.Root, "seg*.m4s").OrderBy(p => p).ToList();
        Assert.NotEmpty(segments);

        // Every segment the run claims to have produced carries real picture bytes — the check the whole
        // feature turns on, run against the engine's own output rather than a fixture.
        foreach (var segment in segments) Assert.True(engine.HasRenderedPicture(segment), $"{segment} has no picture");
    }

    /// <summary>
    /// 🔴 <b>A run asked for segment N numbers from N, not from zero.</b> Getting this wrong produces
    /// segment 0's content in a file called <c>seg40.m4s</c> — the numbers agree with the manifest, the
    /// content does not, and a seek plays the opening of the film.
    /// </summary>
    [Fact]
    public void A_run_that_starts_late_numbers_its_output_from_the_index_it_was_asked_for()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        using (var run = engine.Start(Request(WriteSource(dir, frames: 16), dir.Root, first: 2))!)
        {
            RunToCompletion(run);
        }

        var names = Directory.GetFiles(dir.Root, "seg*.m4s").Select(Path.GetFileName).ToList();
        Assert.DoesNotContain("seg0.m4s", names);
        Assert.DoesNotContain("seg1.m4s", names);
        Assert.Contains("seg2.m4s", names);
    }

    /// <summary>A sound-only source produces sound-only segments rather than refusing or writing an empty picture.</summary>
    [Fact]
    public void A_source_with_no_picture_still_produces_segments()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        var path = dir.Combine("audio.mkv");
        using (var mkv = Mkv(Info(4 * FrameMs), [AudioTrack()],
                             Cluster(0, [.. Enumerable.Range(0, 8).Select(i =>
                                 SimpleBlock(2, (short)(i * FrameMs), true, Frame(i, 12)))])))
        {
            File.WriteAllBytes(path, mkv.ToArray());
        }

        using (var run = engine.Start(Request(path, dir.Root, hasPicture: false))!)
        {
            Assert.NotNull(run);
            RunToCompletion(run);
        }

        var segments = Directory.GetFiles(dir.Root, "seg*.m4s");
        Assert.NotEmpty(segments);
        // No picture was asked for, so none was written — and the check reports that honestly.
        Assert.False(engine.HasRenderedPicture(segments[0]));
    }

    /// <summary>
    /// 🔴 <b>Disposing must KILL the run.</b> A rolling window whose producer outlives its consumer holds a
    /// hardware codec — a device has a handful — plus a file handle and a CPU, invisibly, on a phone. This
    /// asserts the codecs were RELEASED, not merely that the task stopped: a run that exits without
    /// disposing its codecs is the leak the contract warns about, and it looks identical from outside.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>The codec is made SLOW on purpose, and the first version of this test was worthless without
    /// it.</b> With an instant fake the run finished before <c>Dispose</c> was ever called, so the test
    /// asserted that a COMPLETED run releases its codecs — true, and not what this is about. It passed a
    /// deliberate sabotage because of it. A frame delay keeps the run genuinely in flight, so disposal is a
    /// cancellation rather than a formality.
    /// </remarks>
    [Fact]
    public void Disposing_the_run_releases_the_codecs()
    {
        using var dir = TempDir.Create();
        var conversion = new FakeConversion { FrameDelayMs = 20 };
        var engine = new DefaultSegmentEngine(conversion);

        var run = engine.Start(Request(WriteSource(dir, frames: 400), dir.Root))!;

        // Wait until a codec is actually OPEN, or disposal releases nothing because nothing was taken —
        // which proves the opposite of what this asserts. The bound fails loudly rather than hanging.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (conversion.Runs.Count == 0 && !run.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.NotEmpty(conversion.Runs);
        Assert.False(run.HasExited, "the run finished before it could be cancelled — the fake is too fast");

        run.Dispose();

        Assert.True(run.HasExited);
        Assert.All(conversion.Runs, r => Assert.True(r.Disposed, "a codec was not released"));
    }

    /// <summary>A codec the device declines is reported and produces nothing, rather than throwing.</summary>
    [Fact]
    public void A_device_that_declines_every_codec_produces_nothing()
    {
        using var dir = TempDir.Create();
        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new FakeConversion { Accept = false }, lines.Add);

        var run = engine.Start(Request(WriteSource(dir, frames: 8), dir.Root));
        if (run is not null)
        {
            RunToCompletion(run);
            run.Dispose();
        }

        Assert.Empty(Directory.GetFiles(dir.Root, "*.m4s"));
        Assert.Contains(lines, l => l.Contains("converter", StringComparison.OrdinalIgnoreCase));
    }

    // ── the fake ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A codec that emits one output per input, immediately. Real codecs buffer — which the pump already
    /// tolerates, since zero outputs is documented as normal — but a fake that buffered would make every
    /// assertion here depend on how much it held back.
    /// </summary>
    private sealed class FakeConversion : IMediaStreamConversion
    {
        public bool Accept { get; init; } = true;

        /// <summary>Per-frame cost, so a run can be caught mid-flight. Zero for the tests that want it fast.</summary>
        public int FrameDelayMs { get; init; }

        public List<FakeRun> Runs { get; } = [];

        public bool CanConvert(MediaStreamKind kind, string codec) => Accept;

        public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
        {
            if (!Accept) return null;
            var run = new FakeRun(source) { FrameDelayMs = FrameDelayMs };
            lock (Runs) Runs.Add(run);
            return run;
        }
    }

    private sealed class FakeRun(MediaStreamInfo source) : IMediaStreamConversionRun
    {
        public int FrameDelayMs { get; init; }

        public bool Disposed { get; private set; }

        public MediaStreamInfo OutputFormat { get; } = source with
        {
            Codec = source.Kind is MediaStreamKind.Video ? "h264" : "aac",
        };

        // Non-empty from the start: the pump reads this when it writes the init segment, and an empty
        // configuration produces a movie that opens and plays nothing.
        public ReadOnlyMemory<byte> OutputConfig { get; } = source.Kind is MediaStreamKind.Video
            ? AvcConfig
            : AacConfig;

        public int OutputFramesPerPacket => source.Kind is MediaStreamKind.Video ? 0 : 1024;

        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            if (FrameDelayMs > 0) Thread.Sleep(FrameDelayMs);
            return [frame];
        }

        public IReadOnlyList<MediaFrame> Drain() => [];

        public void Dispose() => Disposed = true;
    }
}
