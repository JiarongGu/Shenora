using Shenora.Modules.Media;
using Shenora.Tests.TestSupport;
using static Shenora.Tests.Media.Mp4RemuxerTests;

using Shenora;
namespace Shenora.Tests.Media;

/// <summary>
/// The kit's default <see cref="ISegmentEngine"/> (D71 piece 3) driven end to end over a built Matroska
/// source — copying what MP4 can carry, and driving a FAKE <see cref="IMediaStreamConversion"/> for the rest.
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
/// ⚠ <b>Every fixture below names its codecs deliberately, because the codec IS the branch</b> (D76): a
/// carriable one is copied and never reaches the fake at all. A source built with "whatever the default was"
/// would exercise whichever path that happened to be, and would silently change path the day a predicate
/// moves.
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

    /// <summary>Bytes per picture frame in every fixture — what a COPIED segment's payload is counted against.</summary>
    private const int PictureFrameBytes = 40;

    /// <summary>Bytes per sound frame in every fixture.</summary>
    private const int SoundFrameBytes = 12;

    /// <summary>A codec MP4 cannot carry, so the track has to be re-encoded. MPEG-4 ASP: real, and uncarriable.</summary>
    private const string UncarriableVideo = "V_MPEG4/ISO/ASP";

    /// <summary>Likewise for sound — the common real case beside an H.264 picture.</summary>
    private const string UncarriableAudio = "A_AC3";

    /// <summary>
    /// A source whose picture is H.264 with its <c>avcC</c>: the engine COPIES it. Every 4th frame is a
    /// keyframe (one per second at 250 ms spacing) unless <paramref name="keyEvery"/> says otherwise.
    /// </summary>
    private static MemoryStream Carriable(int frames, bool withAudio = true, int keyEvery = 4,
                                          IReadOnlyList<int>? presentation = null)
    {
        var blocks = new List<byte[]>();
        for (var i = 0; i < frames; i++)
        {
            var at = presentation is null ? i * FrameMs : presentation[i];
            blocks.Add(SimpleBlock(1, (short)at, keyFrame: i % keyEvery == 0, Frame(i, PictureFrameBytes)));
            if (withAudio) blocks.Add(SimpleBlock(2, (short)(i * FrameMs), keyFrame: true, Frame(i, SoundFrameBytes)));
        }

        var tracks = withAudio
            ? new[] { VideoTrack(config: AvcConfig), AudioTrack(config: AacConfig) }
            : [VideoTrack(config: AvcConfig)];
        return Mkv(Info(frames * FrameMs), tracks, Cluster(0, [.. blocks]));
    }

    /// <summary>A source neither stream of which MP4 can carry: both go through the conversion seam.</summary>
    private static MemoryStream Convertible(int frames, bool withAudio = true)
    {
        var blocks = new List<byte[]>();
        for (var i = 0; i < frames; i++)
        {
            blocks.Add(SimpleBlock(1, (short)(i * FrameMs), keyFrame: i % 4 == 0, Frame(i, PictureFrameBytes)));
            if (withAudio) blocks.Add(SimpleBlock(2, (short)(i * FrameMs), keyFrame: true, Frame(i, SoundFrameBytes)));
        }

        var tracks = withAudio
            ? new[] { VideoTrack(codec: UncarriableVideo), AudioTrack(codec: UncarriableAudio) }
            : [VideoTrack(codec: UncarriableVideo)];
        return Mkv(Info(frames * FrameMs), tracks, Cluster(0, [.. blocks]));
    }

    /// <summary>The real-world shape: an H.264 picture with a soundtrack MP4 cannot carry.</summary>
    private static MemoryStream Mixed(int frames)
    {
        var blocks = new List<byte[]>();
        for (var i = 0; i < frames; i++)
        {
            blocks.Add(SimpleBlock(1, (short)(i * FrameMs), keyFrame: i % 4 == 0, Frame(i, PictureFrameBytes)));
            blocks.Add(SimpleBlock(2, (short)(i * FrameMs), keyFrame: true, Frame(i, SoundFrameBytes)));
        }

        return Mkv(Info(frames * FrameMs),
                   [VideoTrack(config: AvcConfig), AudioTrack(codec: UncarriableAudio)],
                   Cluster(0, [.. blocks]));
    }

    private static string Write(TempDir dir, MemoryStream mkv, string name = "source.mkv")
    {
        var path = dir.Combine(name);
        using (mkv) File.WriteAllBytes(path, mkv.ToArray());
        return path;
    }

    /// <summary>A local fixture as the engine now takes it: an opener, not a path.</summary>
    private static MediaByteSource Bytes(string path) => MediaByteSource.ForFile(path);

    private static SegmentRunRequest Request(string source, string directory, int first = 0, double seconds = 1.0,
                                             bool hasPicture = true, int attempt = 0, SegmentPlan? plan = null)
        => new(Bytes(source), directory, hasPicture, first,
               plan ?? SegmentPlan.Grid(seconds, TimeSpan.FromHours(1)), attempt);

    /// <summary>Run to completion, or fail loudly rather than hang — a wedged pump must not stall the suite.</summary>
    private static void RunToCompletion(ISegmentRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!run.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(run.HasExited, "the production run did not finish within 10s");
    }

    private static List<string> Segments(TempDir dir) =>
        [.. Directory.GetFiles(dir.Root, "seg*.m4s").OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)];

    // ── the answers that need no run ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A shell with no codecs says so, rather than starting a run that produces nothing. This is the DESKTOP
    /// answer today: `Shenora.Windows` implements no `IMediaStreamConversion`.
    /// <para>
    /// ⚠ Even though a COPY needs no codec — a source every stream of which can be copied belongs on the
    /// computed-remux route, so this engine declining is what keeps the two from competing (D76).
    /// </para>
    /// </summary>
    [Fact]
    public void Without_a_conversion_the_engine_is_unavailable_and_starts_nothing()
    {
        var engine = new DefaultSegmentEngine(conversion: null);

        Assert.False(engine.IsAvailable);
        Assert.Null(engine.Start(Request("x.mkv", "d")));
        Assert.Null(engine.PlanSegments(Bytes("x.mkv"), SegmentLengths.Of(6.0)));
        Assert.Contains("no segment engine", engine.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 🔴 A fractional GRID is refused BEFORE any work, because the segments it would produce play correctly
    /// and only misbehave when somebody seeks — see <see cref="SegmentGrid"/>.
    /// </summary>
    [Fact]
    public void A_grid_that_cannot_land_on_a_keyframe_is_refused_before_any_work()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        Assert.Null(engine.Start(Request(Write(dir, Convertible(8)), dir.Root, seconds: 2.5)));
        Assert.Empty(Directory.GetFiles(dir.Root, "*.m4s"));
    }

    /// <summary>
    /// ⚠ <b>And a DERIVED plan is exempt, because the refusal is about the kit's own encoders.</b> A grid is
    /// unhittable when nothing puts a keyframe on it; boundaries taken from the source's keyframes are real by
    /// construction, so the same 2.5-second target that is refused above is served here.
    /// </summary>
    [Fact]
    public void A_plan_cut_on_the_source_s_own_keyframes_is_not_subject_to_the_grid_refusal()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        var path = Write(dir, Carriable(frames: 40));

        var plan = engine.PlanSegments(Bytes(path), SegmentLengths.Of(2.5));

        Assert.NotNull(plan);
        Assert.Null(plan!.GridSeconds);
        using var run = engine.Start(Request(path, dir.Root, plan: plan))!;
        Assert.NotNull(run);
        RunToCompletion(run);
        Assert.NotEmpty(Segments(dir));
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

        Assert.Null(engine.Start(Request(Write(dir, Carriable(8)), dir.Root, attempt: 1)));
    }

    [Fact]
    public void Duration_and_picture_come_from_the_source()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        var source = Write(dir, Carriable(8));

        Assert.NotNull(engine.DurationOf(Bytes(source)));
        Assert.True(engine.HasPicture(Bytes(source)));
        // An unreadable source is an absent answer, not a throw — every member promises that.
        Assert.Null(engine.DurationOf(Bytes(dir.Combine("missing.mkv"))));
        Assert.False(engine.HasPicture(Bytes(dir.Combine("missing.mkv"))));
        Assert.Null(engine.PlanSegments(Bytes(dir.Combine("missing.mkv")), SegmentLengths.Of(6.0)));
    }

    // ── the source is an OPENER, not a path ───────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>The whole point of <see cref="MediaByteSource"/>: a source that is not a file is planned and
    /// produced identically.</b> Every other fixture here goes through <see cref="Write"/> and would pass
    /// just as well against a hard-coded <c>File.OpenRead</c> — which is what the engine did until now, and
    /// is why a remote or LAN source could be described but never produced from.
    /// <para>
    /// ⚠ The bytes never reach the disk, so nothing here can fall back to a path.
    /// </para>
    /// </summary>
    [Fact]
    public void A_source_that_is_not_a_file_is_planned_and_produced_through_its_opener()
    {
        using var dir = TempDir.Create();
        using var mkv = Carriable(frames: 16);
        var raw = mkv.ToArray();
        var opened = 0;

        var source = new MediaByteSource
        {
            Label = "in-memory",
            // A fresh stream per call: the probe, the plan and the run each open their own.
            Open = _ => { Interlocked.Increment(ref opened); return new MemoryStream(raw, writable: false); },
        };

        var engine = new DefaultSegmentEngine(new FakeConversion());
        var plan = engine.PlanSegments(source, SegmentLengths.Of(1.0));
        Assert.NotNull(plan);

        using (var run = engine.Start(new SegmentRunRequest(source, dir.Root, HasPicture: true,
                                                            FirstSegment: 0, plan, Attempt: 0))!)
        {
            Assert.NotNull(run);
            RunToCompletion(run);
        }

        Assert.True(opened > 0, "the opener was never called — something still resolved a path");
        var segments = Segments(dir);
        Assert.Equal(4, segments.Count);
        // The SOURCE's own frame bytes came through, so this is a real production and not an empty success.
        Assert.Equal(16 * PictureFrameBytes,
            segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId)));
    }

    /// <summary>
    /// A stream that cannot SEEK is refused by name. Matroska states where a frame lives rather than
    /// streaming it in order, so a forward-only stream is not a slow source — it is an unreadable one, and
    /// without this it reads as "not readable Matroska" about a file that is perfectly well-formed.
    /// </summary>
    [Fact]
    public void A_source_whose_stream_cannot_seek_is_refused_with_a_reason()
    {
        using var mkv = Carriable(frames: 8);
        var raw = mkv.ToArray();
        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new FakeConversion(), AppCallback.Logger(lines.Add));

        var source = new MediaByteSource
        {
            Label = "forward-only",
            Open = _ => new ForwardOnlyStream(raw),
        };

        Assert.Null(engine.DurationOf(source));
        Assert.Null(engine.PlanSegments(source, SegmentLengths.Of(1.0)));
        Assert.Contains(lines, l => l.Contains("seekable adapter", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("forward-only", StringComparison.Ordinal));
    }

    /// <summary>What a naive ranged HTTP body is: readable, and not seekable.</summary>
    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override bool CanSeek => false;
    }

    // ── planning: where a copied run will cut ──────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>A copied picture is cut on the SOURCE's keyframes, not on the length that was asked for.</b> With
    /// a keyframe every 3 seconds and a 1-second target, every segment is 3 seconds — because nothing in a
    /// copy can move a keyframe, and a boundary anywhere else produces a segment no player can start at.
    /// </summary>
    [Fact]
    public void A_copied_run_plans_its_boundaries_from_the_source_keyframes()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        // 24 frames at 250 ms = 6 s, keyframes every 12 frames = every 3 s.
        var plan = engine.PlanSegments(Bytes(Write(dir, Carriable(frames: 24, keyEvery: 12))), SegmentLengths.Of(1.0));

        Assert.NotNull(plan);
        Assert.Null(plan!.GridSeconds);
        Assert.Equal(2, plan.Count);
        Assert.Equal(0, plan.StartOf(0), 6);
        Assert.Equal(3.0, plan.StartOf(1), 6);
        Assert.Equal(3.0, plan.LengthOf(0), 6);
        Assert.Equal(3.0, plan.LengthOf(1), 6);
    }

    /// <summary>
    /// 🔴 <b>A head ramp reaches the COPY path too, and stays cut on the source's own keyframes.</b> This is
    /// the shape an iPhone actually gets — an H.264 picture copied beside a soundtrack it must convert — so
    /// a ramp that only worked when the picture was re-encoded would miss the common case entirely.
    /// <para>
    /// ⚠ The plan is still <see cref="SegmentBoundaries.SourceKeyFrames"/>: the ramp changed which keyframes
    /// are CHOSEN, not where they are. A copy is still legal on it, which is the whole difference from a
    /// synthetic ramp.
    /// </para>
    /// </summary>
    [Fact]
    public void A_head_ramp_shortens_the_first_COPIED_segments_without_moving_a_keyframe()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        // 40 frames at 250 ms = 10 s, a keyframe every second.
        var path = Write(dir, Carriable(frames: 40, keyEvery: 4));

        var plan = engine.PlanSegments(Bytes(path), new SegmentLengths(4.0, [1.0, 2.0]));

        Assert.NotNull(plan);
        Assert.Equal(SegmentBoundaries.SourceKeyFrames, plan!.Origin);
        // 1 s, then 2 s, then the steady 4 s — every boundary a real keyframe of the source.
        Assert.Equal([0.0, 1.0, 3.0, 7.0], [.. Enumerable.Range(0, plan.Count).Select(plan.StartOf)]);

        // The control: the SAME source with no ramp opens with a full-length segment.
        var uniform = engine.PlanSegments(Bytes(path), SegmentLengths.Of(4.0));
        Assert.Equal(4.0, uniform!.LengthOf(0), 6);
    }

    /// <summary>
    /// 🔴 <b>A source with no index is planned by WALKING it, and says so.</b> Cues are optional in
    /// Matroska — a live mux, an interrupted recording and a truncated download all lack them — so the walk
    /// can never stop being the answer. ⚠ Every built fixture in this file is index-less, which is why the
    /// real-file suite carries the other half: this one alone would leave the fast path unexercised and
    /// look like full coverage.
    /// </summary>
    [Fact]
    public void A_source_with_no_index_falls_back_to_the_cluster_walk_and_reports_it()
    {
        using var dir = TempDir.Create();
        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new FakeConversion(), AppCallback.Logger(lines.Add));
        var path = Write(dir, Carriable(frames: 24, keyEvery: 12));

        var plan = engine.PlanSegments(Bytes(path), SegmentLengths.Of(1.0));

        // The fallback is a WORKING plan, not a refusal — the same 3 s cuts the walk has always produced.
        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Count);
        Assert.Contains(lines, l => l.Contains("no usable keyframe index", StringComparison.Ordinal));
    }

    /// <summary>
    /// A picture that must be RE-ENCODED answers no plan at all: the kit's encoders emit a keyframe every
    /// second, so the caller's whole-second grid is hittable and is the right answer.
    /// </summary>
    [Fact]
    public void An_uncarriable_picture_answers_no_plan_and_takes_the_grid()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        Assert.Null(engine.PlanSegments(Bytes(Write(dir, Convertible(frames: 24))), SegmentLengths.Of(1.0)));
    }

    /// <summary>
    /// 🔴 <b>A source whose keyframes are farther apart than a fragment may be is re-encoded instead.</b> A
    /// fragment is held whole in memory, so copying a stream with a one-keyframe-per-minute GOP would build a
    /// single buffer of hundreds of megabytes on a phone. Declining the plan sends the run to the grid, where
    /// the kit's own keyframes make every boundary hittable.
    /// </summary>
    [Fact]
    public void Keyframes_farther_apart_than_a_fragment_may_be_are_declined_with_a_reason()
    {
        using var dir = TempDir.Create();
        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new FakeConversion(), AppCallback.Logger(lines.Add));
        // 200 frames at 250 ms = 50 s, with exactly ONE keyframe — past the 30 s a copied fragment may be.
        var path = Write(dir, Carriable(frames: 200, withAudio: false, keyEvery: 1_000));

        Assert.Null(engine.PlanSegments(Bytes(path), SegmentLengths.Of(6.0)));
        Assert.Contains(lines, l => l.Contains("re-encoding instead", StringComparison.Ordinal));
    }

    // ── producing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🔴 <b>THE HEADLINE OF D76: a carriable picture reaches the fragments without meeting a codec at all.</b>
    /// The engine re-encoded everything until 2026-08-14, and since the platform video encoders offer only
    /// h263/mpeg4/mpeg2video — none of which a webview decodes — every real film came back sound-only. This
    /// asserts BOTH halves: no video codec was opened, and the segments carry the source's own frame bytes.
    /// </summary>
    [Fact]
    public void A_carriable_picture_is_COPIED_rather_than_re_encoded()
    {
        using var dir = TempDir.Create();
        var conversion = new FakeConversion();
        var engine = new DefaultSegmentEngine(conversion);
        var path = Write(dir, Carriable(frames: 16));

        using (var run = engine.Start(Request(path, dir.Root, plan: engine.PlanSegments(Bytes(path), SegmentLengths.Of(1.0))))!)
        {
            Assert.NotNull(run);
            RunToCompletion(run);
        }

        // Not one codec was taken — for either track, since AAC is carriable too.
        Assert.Empty(conversion.Runs);

        var segments = Segments(dir);
        Assert.Equal(4, segments.Count);
        foreach (var segment in segments) Assert.True(engine.HasRenderedPicture(segment), $"{segment} has no picture");

        // The bytes are the SOURCE's, not an encoder's: four 40-byte frames per one-second segment.
        Assert.Equal(4 * PictureFrameBytes, Mp4FragmentReader.SampleBytes(segments[0], DefaultSegmentEngine.VideoTrackId));
        // And every frame is accounted for across the run — a copy that dropped the tail would still play.
        Assert.Equal(16 * PictureFrameBytes,
            segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId)));
    }

    /// <summary>
    /// 🔴 <b>A copied picture requires the PLAN that says where its keyframes are; handed a GRID it is
    /// re-encoded instead.</b> The two decisions have to agree, and they are made in different places — the
    /// plan when the manifest is built, the copy when the run starts. Let them disagree and every cut SLIPS
    /// to the first source keyframe after the boundary, so each segment holds the wrong stretch of film while
    /// the playlist declares the right one. With a real GOP the slip is whole seconds and an index it skips
    /// is never written at all, which the page can only answer by waiting out its budget and restarting.
    /// <para>
    /// It is reachable, not theoretical: <c>PlanSegments</c> declines a source whose keyframes are too far
    /// apart, and its own message says "re-encoding instead" — which is only true if this holds.
    /// </para>
    /// </summary>
    [Fact]
    public void A_carriable_picture_handed_a_GRID_is_re_encoded_rather_than_copied_where_it_cannot_cut()
    {
        using var dir = TempDir.Create();
        var conversion = new FakeConversion();
        var engine = new DefaultSegmentEngine(conversion);

        // A source the engine WOULD copy, given the plan for it — but the request carries a plain grid.
        // ⚠ Keyframes every 750 ms against a 1 s grid, so NO source keyframe sits on a boundary: this is the
        // fixture that makes the consequence visible rather than merely argued. Copying here cuts nowhere.
        using (var run = engine.Start(Request(Write(dir, Carriable(frames: 16, keyEvery: 3)), dir.Root, seconds: 1.0))!)
        {
            RunToCompletion(run);
        }

        Assert.Contains(conversion.Runs, r => r.Kind is MediaStreamKind.Video);
        // Four segments, because the fallback is a working re-encode rather than a refusal...
        Assert.Equal(4, Segments(dir).Count);
        // ...and seg0 holds exactly the first second, which is the assertion that shows the CONSEQUENCE.
        // ⚠ A segment COUNT does not discriminate here — allow the copy on a grid and four files still
        // appear, each holding the wrong stretch: seg0 runs to the keyframe at 1.5 s, six frames not four.
        Assert.Equal(4 * PictureFrameBytes,
            Mp4FragmentReader.SampleBytes(Segments(dir)[0], DefaultSegmentEngine.VideoTrackId));
    }

    /// <summary>
    /// The common real case — an H.264 picture with an AC-3 soundtrack — spends EXACTLY ONE codec, on the
    /// sound. A phone has a handful of hardware codecs, so a picture copied is a codec not taken.
    ///
    /// <para>
    /// 🔴 <b>It is also the only shape that catches a cut measured in the WRONG CLOCK</b>, which is why the
    /// soundtrack's bytes are counted here. A copied picture is timed on the source's ticks and a converted
    /// soundtrack on its own sample rate, so a boundary passed to both as one number compares 48 000 against
    /// 1 000 — and the sound side of every cut lands wherever the round-robin had reached. The segments still
    /// play, and only the sound drifts against the picture.
    /// </para>
    /// </summary>
    [Fact]
    public void The_real_case_copies_the_picture_and_converts_only_the_sound()
    {
        using var dir = TempDir.Create();
        var conversion = new FakeConversion();
        var engine = new DefaultSegmentEngine(conversion);
        var path = Write(dir, Mixed(frames: 16));

        using (var run = engine.Start(Request(path, dir.Root, plan: engine.PlanSegments(Bytes(path), SegmentLengths.Of(1.0))))!)
        {
            RunToCompletion(run);
        }

        var opened = Assert.Single(conversion.Runs);
        Assert.Equal(MediaStreamKind.Audio, opened.Kind);
        var segments = Segments(dir);
        Assert.Equal(4, segments.Count);
        Assert.Equal(16 * PictureFrameBytes,
            segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId)));

        // The first second's SOUND, all four packets of it (the fake emits one per input frame). Measured in
        // the audio channel's own clock — a boundary compared in the picture's would take one packet.
        Assert.Equal(4 * SoundFrameBytes, Mp4FragmentReader.SampleBytes(segments[0], DefaultSegmentEngine.AudioTrackId));
        Assert.Equal(16 * SoundFrameBytes,
            segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.AudioTrackId)));
    }

    /// <summary>
    /// 🔴 <b>A run that starts PAST segment zero must time its converted sound from where it started.</b>
    ///
    /// <para>
    /// Measured on an iPhone 2026-08-15 and invisible to every other test here, because they all start at
    /// segment 0 — where a relative clock and an absolute one agree. A converted soundtrack is timed by
    /// COUNTING PACKETS, and the count begins at zero for each run, so a run producing segment 2 wrote its
    /// sound at 0.0 s while its copied picture sat at segment 2's real start. Both fragments are
    /// well-formed and both append without error; the page sees the INTERSECTION of the two tracks and
    /// stalls with a fraction of a second buffered.
    /// </para>
    /// <para>
    /// ⚠ Asserted on the fragment's DECODE TIME rather than its size — the bytes were always right.
    /// </para>
    /// </summary>
    [Fact]
    public void A_run_starting_past_segment_zero_times_its_converted_sound_from_there()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        var path = Write(dir, Mixed(frames: 16));            // 4 s at 250 ms a frame, 1 s segments
        var plan = engine.PlanSegments(Bytes(path), SegmentLengths.Of(1.0));

        const int first = 2;                                  // start at 2 s, not at zero
        using (var run = engine.Start(Request(path, dir.Root, first: first, plan: plan))!)
        {
            RunToCompletion(run);
        }

        var segments = Segments(dir);
        Assert.NotEmpty(segments);

        // The audio track's timescale IS its sample rate — 48000, the fixture's, carried through the fake's
        // OutputFormat — so segment 2 begins at 2 × 48000 ticks. The picture is the control: copied, it was
        // always correct, so a test checking only it would have passed throughout.
        var sound = Mp4FragmentReader.BaseDecodeTime(segments[0], DefaultSegmentEngine.AudioTrackId);
        Assert.NotNull(sound);
        Assert.InRange(sound.Value, (long)(1.9 * 48000), (long)(2.1 * 48000));

        var picture = Mp4FragmentReader.BaseDecodeTime(segments[0], DefaultSegmentEngine.VideoTrackId);
        Assert.NotNull(picture);
        Assert.True(picture.Value > 0, "the copied picture should start at segment 2, not at zero");
    }

    /// <summary>
    /// ⚠ <b>A copied picture with B-frames: the frames are stored in DECODE order and shown out of it.</b>
    /// Matroska states when a frame is SHOWN and MP4 states when it is DECODED, so a copy has to derive one
    /// from the other — the calculation <c>Mp4Remuxer</c> already owns. Getting it wrong loses frames or
    /// writes negative durations; every frame surviving is the observable half of it.
    /// </summary>
    [Fact]
    public void A_copied_picture_whose_frames_are_shown_out_of_order_keeps_every_frame()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        // I P B B per second, twice: shown 0,750,250,500 — stored in the order a decoder needs them.
        int[] shown = [0, 750, 250, 500, 1_000, 1_750, 1_250, 1_500];
        var path = Write(dir, Carriable(frames: 8, withAudio: false, keyEvery: 4, presentation: shown));

        using (var run = engine.Start(Request(path, dir.Root, plan: engine.PlanSegments(Bytes(path), SegmentLengths.Of(1.0))))!)
        {
            RunToCompletion(run);
        }

        var segments = Segments(dir);
        Assert.NotEmpty(segments);
        Assert.Equal(8 * PictureFrameBytes,
            segments.Sum(s => Mp4FragmentReader.SampleBytes(s, DefaultSegmentEngine.VideoTrackId)));
    }

    /// <summary>
    /// The whole loop on the CONVERT path: source in, an init segment and numbered fragments out, each
    /// readable by the reader that answers <see cref="ISegmentEngine.HasRenderedPicture"/>.
    /// </summary>
    [Fact]
    public void It_writes_an_init_segment_and_numbered_fragments()
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());

        using (var run = engine.Start(Request(Write(dir, Convertible(frames: 16)), dir.Root))!)
        {
            Assert.NotNull(run);
            RunToCompletion(run);
        }

        Assert.True(File.Exists(dir.Combine("init.mp4")), "no init segment was written");
        var segments = Segments(dir);
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_run_that_starts_late_numbers_its_output_from_the_index_it_was_asked_for(bool copied)
    {
        using var dir = TempDir.Create();
        var engine = new DefaultSegmentEngine(new FakeConversion());
        var path = Write(dir, copied ? Carriable(frames: 16) : Convertible(frames: 16));

        using (var run = engine.Start(Request(path, dir.Root, first: 2, plan: engine.PlanSegments(Bytes(path), SegmentLengths.Of(1.0))))!)
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
        using (var mkv = Mkv(Info(4 * FrameMs), [AudioTrack(config: AacConfig)],
                             Cluster(0, [.. Enumerable.Range(0, 8).Select(i =>
                                 SimpleBlock(2, (short)(i * FrameMs), true, Frame(i, SoundFrameBytes)))])))
        {
            File.WriteAllBytes(path, mkv.ToArray());
        }

        using (var run = engine.Start(Request(path, dir.Root, hasPicture: false))!)
        {
            Assert.NotNull(run);
            RunToCompletion(run);
        }

        var segments = Segments(dir);
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
    /// cancellation rather than a formality. ⚠ It also needs a source that is CONVERTED: a copied one takes
    /// no codec at all, so there would be nothing to release and the assertion would pass vacuously.
    /// </remarks>
    [Fact]
    public void Disposing_the_run_releases_the_codecs()
    {
        using var dir = TempDir.Create();
        var conversion = new FakeConversion { FrameDelayMs = 20 };
        var engine = new DefaultSegmentEngine(conversion);

        var run = engine.Start(Request(Write(dir, Convertible(frames: 400)), dir.Root))!;

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

    /// <summary>
    /// A codec the device declines is reported and produces nothing, rather than throwing. ⚠ The source is
    /// deliberately one MP4 can carry NEITHER stream of: a carriable track needs no device to agree, so a
    /// fixture with one would produce segments here and this test would be asserting nothing.
    /// </summary>
    [Fact]
    public void A_device_that_declines_every_codec_produces_nothing()
    {
        using var dir = TempDir.Create();
        var lines = new List<string>();
        var engine = new DefaultSegmentEngine(new FakeConversion { Accept = false }, AppCallback.Logger(lines.Add));

        var run = engine.Start(Request(Write(dir, Convertible(frames: 8)), dir.Root));
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

        /// <summary>Which kind this run was opened for — so a test can assert WHICH codec was spent.</summary>
        public MediaStreamKind Kind => source.Kind;

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

        /// <summary>
        /// One output per input — but a PICTURE is re-keyed on the encoder's own interval rather than by
        /// echoing the source's flags.
        /// <para>
        /// 🔴 <b>That difference is the coupling the whole grid rests on</b> (D75): both platform encoders
        /// emit a keyframe every second, which is what makes a whole-second boundary hittable at all. A fake
        /// that echoed the source's keyframes would make a grid look hittable exactly when it is not — and a
        /// test asking "was this cut where the playlist says" would pass for the wrong reason.
        /// </para>
        /// </summary>
        public IReadOnlyList<MediaFrame> Push(MediaFrame frame)
        {
            if (FrameDelayMs > 0) Thread.Sleep(FrameDelayMs);
            if (source.Kind is not MediaStreamKind.Video) return [frame];

            // One second at the fixtures' 250 ms frame spacing.
            var keyFrame = _pushed++ % 4 == 0;
            return [new MediaFrame(frame.Data, frame.PresentationTimeUs, keyFrame)];
        }

        private int _pushed;

        public IReadOnlyList<MediaFrame> Drain() => [];

        public void Dispose() => Disposed = true;
    }
}
