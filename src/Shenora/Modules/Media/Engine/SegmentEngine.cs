using Microsoft.Extensions.Logging;

namespace Shenora.Modules.Media;

/// <summary>
/// Produces a stream's segments for a <see cref="SegmentStream"/>: STARTS at an arbitrary point and keeps
/// writing numbered pieces until killed, where a converter answers only once the whole source has been read.
/// A seam because only the process launch is per-platform (D42).
/// </summary>
/// <remarks>
/// ⚠ Not interchangeable with <see cref="IMediaStreamConversion"/> or <c>MediaConversionOptions.Convert</c> —
/// they differ in WHEN output becomes usable (<c>docs/design/media.md</c>, "The four seams"). The kit ships a
/// default engine (D71/D76); implement this only for reach it does not have (D52).
/// </remarks>
public interface ISegmentEngine
{
    /// <summary>True when an engine is present and runnable here.</summary>
    bool IsAvailable { get; }

    /// <summary>What the engine is, for the log. Never null.</summary>
    string Describe();

    /// <summary>
    /// How long the source plays, or null when it cannot be read. ⚠ The manifest is computed from this ALONE:
    /// no segment has to exist for the whole playlist to be declared.
    /// </summary>
    TimeSpan? DurationOf(MediaByteSource source);

    /// <summary>Does the source carry a PICTURE (never an attached cover image)? Decides whether a run needs a video encoder.</summary>
    bool HasPicture(MediaByteSource source);

    /// <summary>
    /// WHERE this engine will cut, or null for "I will hit your grid" — the caller then plans
    /// <see cref="SegmentPlan.Grid"/> itself. A COPYING run cannot promise a grid: copied frames keep the
    /// original encoder's keyframes (D76).
    /// <para>
    /// 🔴 <b>The manifest and the producer must agree about every boundary, and only the engine knows them.</b>
    /// A playlist states each segment's length before any exists; if the run cuts elsewhere, a seek lands at
    /// the wrong moment and NOTHING reports it — the bytes are valid and the player believes the playlist.
    /// </para>
    /// </summary>
    /// <param name="source">The bytes the stream is for.</param>
    /// <param name="lengths">The lengths the caller asked for, head ramp included. A derived plan AIMS at them — a copied track can only be cut where the source already has a keyframe.</param>
    /// <param name="cancellationToken">The request's own token — honour it: the kit's engine may walk the source's frame index, on the request asking for the manifest.</param>
    SegmentPlan? PlanSegments(MediaByteSource source, SegmentLengths lengths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Does a produced SEGMENT actually contain a picture — frames, not merely a declared stream? Takes a path
    /// because it reads a fragment the engine just wrote into <see cref="SegmentRunRequest.Directory"/>.
    /// <para>
    /// 🔴 <b>A DIFFERENT QUESTION from <see cref="HasPicture"/>, and asking the wrong one ships a SILENT bug.</b>
    /// A container declares its streams up front, so a picture-less segment still names <c>Video: h264 …</c>
    /// while the encoder wrote <c>video:0KiB</c> and exited 0. What is missing is the SIZE.
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
/// <param name="Source">The original bytes, already authorised by the caller. ⚠ Print <see cref="MediaByteSource.Label"/>, never an address.</param>
/// <param name="Directory">
/// Where to write the output, created by the caller and re-created per restart. A run writes
/// <c>seg{k}.m4s</c> per segment (<see cref="SegmentRunRequest.SegmentExtension"/>) AND one <c>init.mp4</c>
/// (<see cref="SegmentRunRequest.InitSegmentName"/>); both names are part of this contract.
/// <para>
/// 🔴 <b>EVERY PART MUST BE PUBLISHED ATOMICALLY: write <c>{name}.part</c>
/// (<see cref="SegmentRunRequest.PartialExtension"/>) and RENAME it into place once it is whole.</b> The
/// consumer serves a part the moment it EXISTS, so a progressively-written file is served truncated — which
/// plays for a second and stops, with nothing to report.
/// </para>
/// <para>
/// ⚠ <b>The init segment is written BESIDE THE FIRST FRAGMENT, not ahead of the run</b> — its decoder
/// configuration is knowable only once an encoder has produced output — so a consumer must tolerate "not
/// yet" for it exactly as it does for a segment.
/// </para>
/// </param>
/// <param name="HasPicture">From <see cref="ISegmentEngine.HasPicture"/> — asked once, so a restart does not re-probe.</param>
/// <param name="FirstSegment">Where to start. The run seeks to <see cref="SegmentPlan.StartOf"/> and numbers its output from there.</param>
/// <param name="Plan">
/// The boundaries the manifest already declared. <b>Not negotiable, and never re-derived by the engine</b>:
/// a run that cuts elsewhere produces segments whose numbers agree with the playlist and whose content does
/// not, and nothing reports it.
/// </param>
/// <param name="Attempt">0 for the first try; bumped when a run produced nothing usable, so an engine with a second candidate can offer it.</param>
public sealed record SegmentRunRequest(
    MediaByteSource Source,
    string Directory,
    bool HasPicture,
    int FirstSegment,
    SegmentPlan Plan,
    int Attempt)
{
    /// <summary>
    /// The initialisation segment, written once per run into <see cref="Directory"/>: it declares the tracks
    /// and their decoder configuration, which the numbered segments do not repeat. ⚠ A run that never writes
    /// it produces segments nothing can decode.
    /// </summary>
    public const string InitSegmentName = "init.mp4";

    /// <summary>
    /// What a numbered segment is called: <c>seg{k}</c> plus this.
    /// <para>
    /// 🔴 <b>fMP4 (<c>.m4s</c>), never MPEG-TS</b> — it is what every
    /// <c>MediaSource</c>/<c>ManagedMediaSource</c> consumes, and it makes
    /// <see cref="ISegmentEngine.HasRenderedPicture"/> answerable, the sample sizes being in the file where
    /// MPEG-TS only declares the stream in its PMT. ⚠ Do not trust <c>isTypeSupported('video/mp2t')</c>: it
    /// answers <c>true</c> on both mobile shells, and a MediaSource append failure is SILENT.
    /// </para>
    /// </summary>
    public const string SegmentExtension = ".m4s";

    /// <summary>
    /// Appended to a part's name while it is still being written: <c>seg3.m4s.part</c>, renamed to
    /// <c>seg3.m4s</c> once whole (<see cref="Directory"/> states the contract). A process killed mid-write
    /// leaves a <c>.part</c>, which is swept rather than served. ⚠ It does NOT end in
    /// <see cref="SegmentExtension"/>, so a <c>seg*.m4s</c> enumeration and the route's own resource parsing
    /// both skip it.
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
    /// The kit's own engine: it copies every stream MP4 can carry and re-encodes only what it cannot, using
    /// the codecs <paramref name="conversion"/> supplies (D76).
    /// </summary>
    /// <param name="conversion">
    /// The shell's codecs — normally resolved from DI as <see cref="IMediaStreamConversion"/>.
    /// ⚠ <b>Null is accepted and means the engine reports <see cref="ISegmentEngine.IsAvailable"/> =
    /// false</b>, so <c>UseSegmentStream</c> mounts a route that answers "not complete" rather than throwing.
    /// </param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink cannot kill a production run.</param>
    /// <returns>An engine to hand to <see cref="SegmentStreamExtensions.UseSegmentStream"/>.</returns>
    public static ISegmentEngine Default(IMediaStreamConversion? conversion, ILogger? log = null) =>
        new DefaultSegmentEngine(conversion, log);
}
