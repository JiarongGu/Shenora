using Microsoft.Extensions.Logging;

namespace Shenora.Modules.Media;

/// <summary>
/// The engine that PRODUCES a stream's segments, in the shape a <see cref="SegmentStream"/> needs it. Not a
/// converter: it STARTS at an arbitrary point and keeps writing numbered pieces until killed, where a
/// converter answers only once the whole source has been read.
/// <para>
/// A seam because only the process launch is per-platform: iOS forbids <c>fork</c>/<c>exec</c>, so an engine
/// there must be an in-process shim behind this same interface (D42).
/// </para>
/// </summary>
/// <remarks>
/// ⚠ <b>Three different things here are called "the app plugs in an encoder" and are NOT interchangeable</b> —
/// this one, <see cref="IMediaStreamConversion"/> and <c>MediaConversionOptions.Convert</c>; they differ in
/// WHEN output becomes usable (<c>docs/design/media.md</c>, "The four seams"). The kit SHIPS a default engine
/// (D71) that copies every stream MP4 can carry and re-encodes only what it cannot (D76), so implement this
/// only for reach the default does not have (D52).
/// </remarks>
public interface ISegmentEngine
{
    /// <summary>True when an engine is actually present and runnable here. A route is worth registering only when it is.</summary>
    bool IsAvailable { get; }

    /// <summary>What the engine is, for the log. Never null.</summary>
    string Describe();

    /// <summary>
    /// How long the source plays, or null when it cannot be read. ⚠ The manifest is computed from this ALONE:
    /// no segment has to exist for the whole playlist to be declared.
    /// </summary>
    TimeSpan? DurationOf(MediaByteSource source);

    /// <summary>
    /// Does the source carry a PICTURE worth keeping (never an attached cover image)? Decides whether a run
    /// needs a video encoder.
    /// </summary>
    bool HasPicture(MediaByteSource source);

    /// <summary>
    /// WHERE this engine will cut, when it will not cut on the caller's grid — said HERE, once.
    /// <para>
    /// 🔴 <b>The manifest and the producer must agree about every boundary, and only the engine knows them.</b>
    /// A playlist states each segment's length before any exists; if the run cuts elsewhere, a seek lands at
    /// the wrong moment and NOTHING reports it — the bytes are valid and the player believes the playlist.
    /// </para>
    /// <para>
    /// <b>Return null for "I will hit your grid"</b>, as a re-encoding engine always can; the caller then plans
    /// <see cref="SegmentPlan.Grid"/> itself. A COPYING run cannot: copied frames keep the original encoder's
    /// keyframes (D76). ⚠ May be expensive — the kit's engine walks the source's frame index — and it runs on
    /// the request asking for the manifest, so honour the token.
    /// </para>
    /// </summary>
    /// <param name="source">The bytes the stream is for.</param>
    /// <param name="segmentSeconds">The length the caller asked for. A derived plan aims at it rather than hitting it.</param>
    /// <param name="cancellationToken">The request's own token.</param>
    SegmentPlan? PlanSegments(MediaByteSource source, double segmentSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Does a produced SEGMENT actually contain a picture — frames, not merely a declared stream?
    /// <para>
    /// ⚠ Still a PATH where the rest of this interface takes a <see cref="MediaByteSource"/>, and deliberately:
    /// this reads a fragment the engine itself just wrote into <see cref="SegmentRunRequest.Directory"/>, which
    /// is always local however the source arrived.
    /// </para>
    /// <para>
    /// 🔴 <b>A DIFFERENT QUESTION from <see cref="HasPicture"/>, and asking the wrong one ships a SILENT bug.</b>
    /// A hardware encoder can open cleanly, accept every frame, write <c>video:0KiB</c> and exit 0, with every
    /// capability check an app can make still saying the encoder is there. ⚠ <b>"Has a video stream" is the
    /// wrong test</b>: a container declares its streams up front, so a picture-less segment still names
    /// <c>Video: h264 …</c>. What is missing is the SIZE.
    /// </para>
    /// </summary>
    bool HasRenderedPicture(string segment);

    /// <summary>
    /// Begin producing segments, or null when this <see cref="SegmentRunRequest.Attempt"/> has no candidate
    /// left to try — the caller then stops asking for this request. The run keeps writing until it reaches the
    /// end of the source or is disposed. <b>Disposing must KILL it</b>: a rolling window that leaks a process
    /// leaks a CPU and a file handle, on a phone, invisibly.
    /// </summary>
    ISegmentRun? Start(SegmentRunRequest request);
}

/// <summary>What one production run is asked to do.</summary>
/// <param name="Source">
/// The original bytes. Already authorised by the caller — containment for a path, an issued handle for a
/// remote source. ⚠ Print <see cref="MediaByteSource.Label"/> and nothing else.
/// </param>
/// <param name="Directory">
/// Where to write the output. Created by the caller, and re-created per restart. A run writes
/// <c>seg{k}.m4s</c> per segment (<see cref="SegmentRunRequest.SegmentExtension"/>) AND one <c>init.mp4</c>
/// (<see cref="SegmentRunRequest.InitSegmentName"/>); both names are part of this contract.
/// <para>
/// 🔴 <b>EVERY PART MUST BE PUBLISHED ATOMICALLY: write <c>{name}.part</c>
/// (<see cref="SegmentRunRequest.PartialExtension"/>) and RENAME it into place once it is whole.</b> The
/// consumer serves a part the moment it EXISTS, so a progressively-written file is served truncated — which
/// plays for a second and stops, with nothing to report. Renaming within one directory is atomic on every
/// platform this ships to, so a reader sees the old file or the finished one and never a partial.
/// </para>
/// <para>
/// ⚠ <b>The init segment is written BESIDE THE FIRST FRAGMENT, not ahead of the run</b>, so a consumer must
/// wait for it as it waits for a segment: its decoder configuration is knowable only once an encoder has
/// produced output, and writing it early yields a movie that opens and plays nothing.
/// </para>
/// </param>
/// <param name="HasPicture">
/// From <see cref="ISegmentEngine.HasPicture"/> — asked once and passed in, so a restart does not re-probe.
/// </param>
/// <param name="FirstSegment">
/// The segment index to start at. The run seeks to <see cref="SegmentPlan.StartOf"/> and numbers its output
/// from there, so a seek anywhere costs one restart and nothing else.
/// </param>
/// <param name="Plan">
/// The boundaries the manifest already declared — the engine's OWN answer coming back (whatever
/// <see cref="ISegmentEngine.PlanSegments"/> returned, or the caller's grid when that was null).
/// <b>Not negotiable, and never re-derived by the engine</b>: a run that cuts elsewhere produces segments
/// whose numbers agree with the playlist and whose content does not, and nothing reports it.
/// </param>
/// <param name="Attempt">
/// 0 for the first try. Bumped by the caller when a run produced nothing usable, so an engine with more than
/// one encoder candidate can offer the next.
/// </param>
public sealed record SegmentRunRequest(
    MediaByteSource Source,
    string Directory,
    bool HasPicture,
    int FirstSegment,
    SegmentPlan Plan,
    int Attempt)
{
    /// <summary>
    /// The initialisation segment's file name, written once per run into <see cref="Directory"/>. It declares
    /// the tracks and their decoder configuration, which the numbered segments do not repeat. ⚠ A run that
    /// never writes it produces segments nothing can decode.
    /// </summary>
    public const string InitSegmentName = "init.mp4";

    /// <summary>
    /// What a numbered segment is called: <c>seg{k}</c> plus this.
    /// <para>
    /// 🔴 <b>fMP4 (<c>.m4s</c>), never MPEG-TS.</b> fMP4 is what every
    /// <c>MediaSource</c>/<c>ManagedMediaSource</c> consumes, and it makes
    /// <see cref="ISegmentEngine.HasRenderedPicture"/> answerable — the sample sizes are in the file, where
    /// MPEG-TS only declares the stream in its PMT. ⚠ Do not trust <c>isTypeSupported('video/mp2t')</c>: it
    /// answers <c>true</c> on both mobile shells, and a MediaSource append failure is SILENT.
    /// </para>
    /// </summary>
    public const string SegmentExtension = ".m4s";

    /// <summary>
    /// Appended to a part's name while it is still being written: <c>seg3.m4s.part</c>, renamed to
    /// <c>seg3.m4s</c> once whole. See <see cref="Directory"/> for why this is a contract and not a
    /// convention.
    /// <para>
    /// 🔴 <b>It is what lets a consumer treat EXISTENCE as completeness</b>, and that is worth a whole
    /// segment of startup latency. Without it the only way to know a part had finished was that the NEXT one
    /// had appeared — so nothing could play until segment 0 <i>and</i> the opening of segment 1 had been
    /// produced. (The same rule, and the same cost, is visible in other just-in-time transcoders; ffmpeg's
    /// answer is <c>-hls_flags +temp_file</c>.)
    /// </para>
    /// <para>
    /// ⚠ It also replaces crash recovery with a sweep: a process killed mid-write leaves a <c>.part</c>,
    /// which is deleted rather than served. Nothing has to guess which finished file might be truncated.
    /// ⚠ Deliberately does NOT end in <see cref="SegmentExtension"/>, so a <c>seg*.m4s</c> enumeration and
    /// the route's own resource parsing both skip it.
    /// </para>
    /// </summary>
    public const string PartialExtension = ".part";
}

/// <summary>A live production run. Dispose to kill it.</summary>
public interface ISegmentRun : IDisposable
{
    /// <summary>True once the run has finished or died — nothing more will ever appear on disk from it.</summary>
    bool HasExited { get; }
}

/// <summary>How to get the engine the kit ships (D71), for <see cref="SegmentStreamExtensions.UseSegmentStream"/>.</summary>
public static class SegmentEngine
{
    /// <summary>
    /// The kit's own engine: it copies every stream MP4 can carry and re-encodes only what it cannot,
    /// using the codecs <paramref name="conversion"/> supplies (D76).
    /// </summary>
    /// <param name="conversion">
    /// The shell's codecs — normally resolved from DI as <see cref="IMediaStreamConversion"/>.
    /// ⚠ <b>Null is accepted and means the engine reports <see cref="ISegmentEngine.IsAvailable"/> =
    /// false</b>, so <c>UseSegmentStream</c> mounts a route that answers "not complete" rather than
    /// throwing. That is the honest answer on a platform with no codecs, and it means an app needs no
    /// platform branch to ask the question.
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink cannot kill a production run.</param>
    /// <returns>An engine to hand to <see cref="SegmentStreamExtensions.UseSegmentStream"/>.</returns>
    /// <remarks>
    /// A factory rather than a public class, so the engine's shape stays out of the SemVer surface while
    /// the capability is reachable: an app needs an <see cref="ISegmentEngine"/> to mount the route, not
    /// the concrete type. Implement <see cref="ISegmentEngine"/> yourself only for reach this does not
    /// have (D52).
    /// </remarks>
    public static ISegmentEngine Default(IMediaStreamConversion? conversion, ILogger? log = null) =>
        new DefaultSegmentEngine(conversion, log);
}
