namespace Shenora.Media;

/// <summary>
/// The engine that PRODUCES a stream's segments — the half this kit does not ship, in the shape a
/// <see cref="SegmentStream"/> needs it.
///
/// <para>
/// <b>Deliberately not a converter, and the difference is the whole point of the feature.</b> A converter is
/// asked for one finished file and answers when the entire source has been read; a segment engine is asked
/// to START at an arbitrary point and keep writing numbered pieces until it is killed. An hour-long source is
/// an hour-long wait through the first shape and a few seconds through this one.
/// </para>
///
/// <para>
/// ⚠ <b>Why a seam instead of the route just launching a tool:</b> the route, the manifest, the cache and the
/// rolling-window policy are portable; the process launch is not. iOS forbids <c>fork</c>/<c>exec</c>
/// outright, so an engine there cannot be a process at any price — it has to be an in-process shim behind
/// this same interface. Keeping the split here means the policy is written once and only the launch is
/// per-platform, which is the same split <c>MediaConversionOptions.Convert</c> already makes (D42).
/// </para>
///
/// <para>
/// <b>The kit ships a DEFAULT that works, and this seam is the escape hatch — not the only door</b> (D52).
/// The default is built from things that cost nothing: a managed remux (no decoding at all) and the
/// PLATFORM's own codecs, which encode as well as decode. So an app gets working playback without
/// supplying anything, and implements this only when it needs reach the default does not have.
/// </para>
/// <para>
/// ⚠ What the kit still never does is VENDOR a third-party engine — that is D42's actual objection
/// (megabytes every consumer pays for) and D51's (a licence every consumer inherits). A default costing
/// zero bytes and zero obligations contradicts neither.
/// </para>
/// </summary>
public interface ISegmentEngine
{
    /// <summary>True when an engine is actually present and runnable here. A route is worth registering only when it is.</summary>
    bool IsAvailable { get; }

    /// <summary>What the engine is, for the log. Never null.</summary>
    string Describe();

    /// <summary>
    /// How long the source plays, or null when it cannot be read.
    /// <para>
    /// ⚠ The manifest is computed from this ALONE — no segment has to exist for the whole playlist to be
    /// declared, which is what makes the scrub bar the right length and a seek anywhere expressible.
    /// </para>
    /// </summary>
    TimeSpan? DurationOf(string source);

    /// <summary>
    /// Does the source carry a PICTURE worth keeping (never an attached cover image)? Decides whether a run
    /// needs a video encoder at all.
    /// </summary>
    bool HasPicture(string source);

    /// <summary>
    /// Does a produced SEGMENT actually contain a picture — frames, not merely a declared stream?
    ///
    /// <para>
    /// 🔴 <b>A DIFFERENT QUESTION from <see cref="HasPicture"/>, and asking the wrong one ships a silent bug.
    /// This is the single most valuable thing in this whole feature.</b> An encoder can accept every frame,
    /// write nothing, and exit 0 — measured, not theorised: a hardware H.264 encoder advertised by both the
    /// tool's own encoder list and the platform's codec list opened cleanly, mapped the stream, took every
    /// frame, wrote <c>video:0KiB</c>, and exited 0. Every capability check an app can make said the encoder
    /// was there.
    /// </para>
    /// <para>
    /// ⚠ And <b>"has a video stream" is the wrong test</b>, because MPEG-TS names its streams in the PMT — so
    /// a picture-less segment still declares <c>Video: h264 …</c>. What is missing is the SIZE. One bug, two
    /// distinct predicates, which is why this is a separate member instead of a parameter.
    /// </para>
    /// </summary>
    bool HasRenderedPicture(string segment);

    /// <summary>
    /// Begin producing segments, or null when this <see cref="SegmentRunRequest.Attempt"/> has no candidate
    /// left to try — the caller then stops asking for this request.
    /// <para>
    /// The run keeps writing until it reaches the end of the source or is disposed. <b>Disposing must KILL
    /// it</b>: a rolling window that leaks a process leaks a CPU and a file handle, on a phone, invisibly.
    /// </para>
    /// </summary>
    ISegmentRun? Start(SegmentRunRequest request);
}

/// <summary>What one production run is asked to do.</summary>
/// <param name="SourcePath">The original file. Already authorised against the allowed roots by the caller.</param>
/// <param name="Directory">Where to write <c>seg{k}.ts</c>. Created by the caller, and re-created per restart.</param>
/// <param name="HasPicture">
/// From <see cref="ISegmentEngine.HasPicture"/> — asked once and passed in, so a restart does not re-probe.
/// </param>
/// <param name="FirstSegment">
/// The segment index to start at. The run seeks to <c>FirstSegment * SegmentSeconds</c> and numbers its
/// output from there, so a seek anywhere in the source costs one restart and nothing else.
/// </param>
/// <param name="SegmentSeconds">
/// The grid the manifest already declared. <b>Not negotiable by the engine</b> — the manifest, the muxer's
/// segment time and any forced-keyframe expression must agree or the cuts are not where the playlist says.
/// ⚠ A copy-without-re-encode mode cannot hit a fixed grid at all: it lands on the SOURCE's keyframes, whose
/// durations are unknowable in advance, which makes a synthetic manifest illegal. Force keyframes, or do not
/// claim the grid.
/// </param>
/// <param name="Attempt">
/// 0 for the first try. Bumped by the caller when a run produced nothing usable, so an engine with more than
/// one encoder candidate can offer the next — a hardware-then-software ladder spread across restarts rather
/// than walked inside one call.
/// </param>
public sealed record SegmentRunRequest(
    string SourcePath,
    string Directory,
    bool HasPicture,
    int FirstSegment,
    double SegmentSeconds,
    int Attempt);

/// <summary>A live production run. Dispose to kill it.</summary>
public interface ISegmentRun : IDisposable
{
    /// <summary>True once the run has finished or died — nothing more will ever appear on disk from it.</summary>
    bool HasExited { get; }
}
