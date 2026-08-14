namespace Shenora.Modules.Media;

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
/// <remarks>
/// 🔴 <b>WHERE THIS SITS relative to the other seams, because "the app plugs in an encoder" describes three
/// things in this package and they are NOT interchangeable.</b> One primitive, two compositions:
/// <list type="table">
/// <item>
///   <term><see cref="IMediaStreamConversion"/></term>
///   <description>THE PRIMITIVE. One stream in, one stream out, frame by frame. Knows nothing about
///   containers, files or routes.</description>
/// </item>
/// <item>
///   <term><c>MediaConversionOptions.Convert</c></term>
///   <description>ONE FINISHED FILE. A delegate, so an app that wants a native muxer
///   (<c>AVAssetWriter</c>, <c>MediaMuxer</c>) simply supplies its own.
///   <c>new Mp4Remuxer().ToConverter()</c> is the kit's DEFAULT for it, built on the primitive above.</description>
/// </item>
/// <item>
///   <term><see cref="ISegmentEngine"/> (this)</term>
///   <description>A ROLLING WINDOW of numbered pieces, started at an arbitrary index and killed on
///   dispose. Not a converter: a converter answers when the whole source has been read, and an hour-long
///   file is an hour-long wait through that shape and a few seconds through this one.</description>
/// </item>
/// </list>
/// <para>
/// ⚠ <b>So the kit has ONE encoder seam, not two.</b> This interface and the conversion delegate look alike
/// and are not — they differ in WHEN output is usable, which is the whole reason both exist. A default
/// segment engine would be a composition of the primitive plus a transport-stream writer.
/// </para>
/// <para>
/// 🔴 <b>REVERSED 2026-08-12 BY D71: the kit WILL ship a default segment engine, because something asked.</b>
/// This remark used to say the absence was "a DECISION rather than a backlog item (D54)" — a native player
/// opens the source file directly and never needs a rolling window, so segmentation was only for PROGRESSIVE
/// STREAMING to a page that wants it. It made itself falsifiable in the next breath: <i>"something must ASK
/// before one is written (D63)"</i>. The owner asked, and the requirement is that an adopting frontend never
/// feels the layer at all — which an app-supplied-only engine cannot deliver.
/// <para>
/// The default is the composition this remark already predicted: the platform codecs behind
/// <see cref="IMediaStreamConversion"/> plus a fragment writer. <b>D51 is untouched</b> — no engine bytes
/// ship, and an app past the platform's reach still supplies its own. ⚠ <b>Nothing implements this
/// interface YET</b>, so it remains a capability nothing consults until that lands (D63); the difference is
/// that it is now scheduled work rather than a settled no.
/// </para>
/// </para>
/// </remarks>
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
/// <param name="Directory">
/// Where to write the output. Created by the caller, and re-created per restart.
/// <para>
/// 🔴 <b>TWO kinds of file, and both names are part of this contract</b> — see
/// <see cref="SegmentRunRequest.InitSegmentName"/> and <see cref="SegmentRunRequest.SegmentExtension"/>. A
/// run writes <c>seg{k}.m4s</c> for each segment AND one <c>init.mp4</c> carrying the tracks and their
/// decoder configuration, because a fragment deliberately repeats neither.
/// </para>
/// <para>
/// ⚠ <b>The init segment is written BESIDE THE FIRST FRAGMENT, not ahead of the run</b>, and a consumer must
/// be prepared to wait for it exactly as it waits for a segment. The configuration it carries is knowable
/// only once an encoder has produced output — writing it early yields a movie that opens and plays nothing.
/// </para>
/// </param>
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
    int Attempt)
{
    /// <summary>
    /// The initialisation segment's file name, written once per run into <see cref="Directory"/>.
    /// <para>
    /// It declares the tracks and carries their decoder configuration, which the numbered segments
    /// deliberately do not repeat — so a consumer fetches this once and every segment afterwards is only
    /// media. ⚠ A run that never writes it produces segments nothing can decode.
    /// </para>
    /// </summary>
    public const string InitSegmentName = "init.mp4";

    /// <summary>
    /// What a numbered segment is called: <c>seg{k}</c> plus this.
    /// <para>
    /// 🔴 <b>fMP4 (<c>.m4s</c>) and NOT MPEG-TS, which this contract assumed until 2026-08-14.</b>
    /// <c>isTypeSupported('video/mp2t')</c> answered <c>true</c> on both mobile shells and that claim is not
    /// trusted — <c>canPlayType</c> produced exactly such a <c>true</c> for HLS the same day, and a
    /// MediaSource append failure is SILENT. fMP4 is what every <c>MediaSource</c>/<c>ManagedMediaSource</c>
    /// actually consumes, and it makes <see cref="ISegmentEngine.HasRenderedPicture"/> answerable: the
    /// sample sizes are in the file, where MPEG-TS only ever declared the stream in its PMT.
    /// </para>
    /// </summary>
    public const string SegmentExtension = ".m4s";
}

/// <summary>A live production run. Dispose to kill it.</summary>
public interface ISegmentRun : IDisposable
{
    /// <summary>True once the run has finished or died — nothing more will ever appear on disk from it.</summary>
    bool HasExited { get; }
}
