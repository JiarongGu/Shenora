using Shenora.Engine.Files;

namespace Shenora.Modules.Media;

/// <summary>What has to happen before a player can open a file.</summary>
public enum MediaPlaybackAction
{
    /// <summary>Serve the bytes untouched. The container opens and every stream decodes.</summary>
    Direct,

    /// <summary>
    /// Repackage into a container the player can open, COPYING every stream. No re-encoding, so it is
    /// fast and lossless.
    /// <para>
    /// This verdict is the reason a per-file boolean is not good enough: H.264-in-MKV is the common case,
    /// and it needs only a new container while its picture and sound are already fine.
    /// </para>
    /// </summary>
    Remux,

    /// <summary>
    /// Re-encode at least one stream. Slow and lossy, so the planner reaches for it only when a stream
    /// genuinely cannot be copied — see <see cref="MediaPlaybackPlan.Streams"/> for which one forced it.
    /// </summary>
    Transcode,

    /// <summary>
    /// Nothing here can fix it: a stream needs re-encoding and the policy offers no encoder for its kind.
    /// An honest refusal, so the app can hand the file to an external player instead of failing silently.
    /// </summary>
    Unsupported,
}

/// <summary>
/// What the app's player can open, expressed as sets the app owns.
/// <para>
/// ⚠ <b>The kit ships NO default and that is deliberate</b> (D42, and `generic-library.md`'s rule that the
/// mechanism is the kit's while the policy is the consumer's). There is no correct universal list: a
/// browser's set differs from a bundled engine's, Android's differs PER DEVICE because codec support is
/// vendor-declared (<c>MediaCodecList</c> is a runtime query for that reason), and a licensed codec like
/// AC-3 may be present on one handset and absent on the next. A baked-in list would be one app's guess
/// frozen into everyone's planner, and confidently wrong on the case that matters.
/// </para>
/// </summary>
public sealed record MediaPlaybackPolicy
{
    /// <summary>
    /// Containers the player can OPEN, as lowercase extensions including the dot.
    /// <para>
    /// Checked first and separately from the codecs, because the container is its own failure: an
    /// <c>.mkv</c> holding perfectly ordinary AAC still cannot be opened, so a codec-only answer calls it
    /// playable and the player then refuses. That inversion is a real bug the donor's comment records.
    /// </para>
    /// </summary>
    public IReadOnlySet<string> Containers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Codec names the player can DECODE, keyed by stream kind, as a probe reports them (<c>h264</c>,
    /// <c>aac</c>). A kind that is absent decodes nothing.
    /// <para>
    /// <b>Keyed by <see cref="MediaStreamKind"/> to match <see cref="IMediaCapability"/>, which asks the
    /// DEVICE the same question.</b> It replaced a pair of named properties, one per kind.
    /// The two are compared constantly (the gap between them IS the converter's job, D59), and a comparison
    /// between a keyed lookup and a pair of named properties is written by hand every time. A kind the kit
    /// does not act on today needs no new member here, which is the same reason the capability is keyed.
    /// </para>
    /// <para>
    /// ⚠ AUDIO is the set most likely to be the reason a file fails. Licensed audio — AC-3, E-AC-3, DTS —
    /// is not in Android's mandatory set, so a file whose picture decodes perfectly plays with NO SOUND.
    /// That is why this planner is per-stream: a single verdict for the file would have called that case
    /// unsupported and thrown away a remux that fixes it.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<MediaStreamKind, IReadOnlySet<string>> Codecs { get; init; } =
        new Dictionary<MediaStreamKind, IReadOnlySet<string>>();

    /// <summary>
    /// Kinds the app can RE-ENCODE. A kind that is absent turns an undecodable stream into
    /// <see cref="MediaPlaybackAction.Unsupported"/> instead of a transcode it cannot perform.
    /// <para>
    /// A set rather than the <c>CanEncodeVideo</c>/<c>CanEncodeAudio</c> booleans it replaced, for the reason above: it is
    /// the same question keyed the same way, so growing a kind adds no member.
    /// </para>
    /// </summary>
    public IReadOnlySet<MediaStreamKind> Encodable { get; init; } = new HashSet<MediaStreamKind>();

    /// <summary>Codecs this player decodes for <paramref name="kind"/>; empty when it decodes none.</summary>
    public IReadOnlySet<string> CodecsFor(MediaStreamKind kind) =>
        Codecs.TryGetValue(kind, out var set) ? set : EmptyCodecs;

    /// <summary>Can the app re-encode <paramref name="kind"/>?</summary>
    public bool CanEncode(MediaStreamKind kind) => Encodable.Contains(kind);

    private static readonly IReadOnlySet<string> EmptyCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One stream's verdict, and why.</summary>
/// <param name="Stream">The stream as probed.</param>
/// <param name="DecodesNatively">
/// True when the policy lists this stream's codec. ⚠ Also true when the codec is UNKNOWN and the container
/// is one the policy accepts — see <see cref="MediaPlaybackPlanner.Plan"/> on why an unprobed file is
/// given the benefit of the doubt.
/// </param>
/// <param name="NeedsReEncode">True when this stream is what forces a transcode.</param>
public sealed record MediaStreamPlan(MediaStreamInfo Stream, bool DecodesNatively, bool NeedsReEncode);

/// <summary>The whole verdict for a file.</summary>
/// <param name="Action">What has to happen.</param>
/// <param name="Streams">Per-stream detail. Empty when nothing probed the file.</param>
/// <param name="ContainerOpens">Whether the container itself is playable, independently of the streams.</param>
/// <param name="Reason">
/// A short, non-localised explanation for the host LOG. Not for a user and not for the wire: it names
/// codecs and containers, and this kit's error contract is a code plus parameters, never English prose
/// (`ipc-contracts`).
/// </param>
public sealed record MediaPlaybackPlan(
    MediaPlaybackAction Action,
    IReadOnlyList<MediaStreamPlan> Streams,
    bool ContainerOpens,
    string Reason);

/// <summary>
/// Decides what must happen before a player can open a file: <see cref="MediaPlaybackAction.Direct"/>,
/// <see cref="MediaPlaybackAction.Remux"/>, <see cref="MediaPlaybackAction.Transcode"/> or
/// <see cref="MediaPlaybackAction.Unsupported"/> — <b>per STREAM, not per file</b>.
/// <para>
/// A pure function with no I/O, so it is fully unit-testable — the same profile as
/// <c>ManifestDiff</c> in <c>Shenora</c>, and chosen for the same reason: the decision is where the
/// bugs live, so it should be the part a test can pin exactly.
/// </para>
/// <para>
/// <b>Why per-stream</b> (D42): the frequent real failure is not "this file will not play", it is
/// <i>picture with no sound</i> — a file whose H.264 video decodes perfectly while its AC-3 audio does
/// not, because licensed audio is not in every platform's mandatory set. A single
/// <c>CanPlay(file) -&gt; bool</c> would have been wrong in the most common failure case, and would have
/// discarded the cheap fix: copy the video, re-encode only the sound.
/// </para>
/// <para>
/// Extracted from two independent implementations that agreed on the shape and disagreed on the verdicts,
/// which is exactly why the verdicts live in <see cref="MediaPlaybackPolicy"/> and only the mechanism is
/// here.
/// </para>
/// </summary>
public static class MediaPlaybackPlanner
{
    /// <summary>
    /// Plan playback for a probed file under an app's policy.
    /// <para>
    /// The order is load-bearing, and each step exists because getting it wrong is a real bug someone hit:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>The CONTAINER is decided first and separately.</b> An <c>.mkv</c> carrying ordinary AAC cannot
    /// be opened, so testing codecs first calls the file playable and the player then refuses it. The
    /// donor's own comment records this inversion.
    /// </item>
    /// <item>
    /// <b>An UNPROBED file gets the benefit of the doubt.</b> No streams means no probe ran — a normal
    /// state, since the probe is an external tool the app may not have installed. The verdict then rests
    /// on the container alone, and a file in an accepted container is called
    /// <see cref="MediaPlaybackAction.Direct"/> rather than transcoded. Both donors are explicit that a
    /// missing probe must not cost a needless re-encode; one says "never punish those with a needless
    /// transcode" in as many words.
    /// </item>
    /// <item>
    /// <b>A stream whose codec is unknown is treated as decodable</b>, for the same reason — but only when
    /// the container opens. Guessing "broken" on missing information turns absent tooling into failed
    /// playback.
    /// </item>
    /// <item>
    /// <b>Subtitles and unknown streams never force a transcode.</b> They are droppable: a player that
    /// cannot render a subtitle track still plays the film. Letting them vote would transcode a file for
    /// a stream nobody needs.
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="probe">What a probe found. May be empty; may be sparsely populated.</param>
    /// <param name="policy">What the app's player can open. Its sets are the app's, never the kit's.</param>
    /// <returns>The verdict, with per-stream detail and a log-only reason.</returns>
    public static MediaPlaybackPlan Plan(MediaProbeResult probe, MediaPlaybackPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(policy);

        // Step 1 — the container, on its own. A null container cannot be confirmed playable; it is the
        // "nothing is known at all" case and falls through to Remux below rather than Direct, because
        // repackaging is the safe answer when we cannot even name the wrapper.
        var containerOpens = probe.Container is { Length: > 0 } container
                             && policy.Containers.Contains(container);

        var plans = new List<MediaStreamPlan>(probe.Streams.Count);
        var reEncodeVideo = false;
        var reEncodeAudio = false;
        var blocked = false;

        foreach (var stream in probe.Streams)
        {
            // Step 4 — droppable kinds are recorded but never vote.
            if (stream.Kind is MediaStreamKind.Subtitle or MediaStreamKind.Unknown)
            {
                plans.Add(new MediaStreamPlan(stream, DecodesNatively: true, NeedsReEncode: false));
                continue;
            }

            // Step 3 — an unnamed codec is given the benefit of the doubt. ⚠ ONE lookup for every kind:
            // this used to branch `Kind is Video ? VideoCodecs : AudioCodecs`, which silently treated
            // subtitles (and anything added later) as AUDIO. Keying the policy removed the branch and the
            // bug with it.
            var decodes = stream.Codec is not { Length: > 0 } codec
                          || policy.CodecsFor(stream.Kind).Contains(codec);

            var needsReEncode = !decodes;
            plans.Add(new MediaStreamPlan(stream, decodes, needsReEncode));

            if (!needsReEncode) continue;

            // Can the policy actually perform the re-encode this stream needs? If not, the honest answer
            // is Unsupported — the app can then hand the file to an external player, which is a better
            // outcome than starting a conversion that cannot finish.
            if (!policy.CanEncode(stream.Kind)) blocked = true;
            else if (stream.Kind is MediaStreamKind.Video) reEncodeVideo = true;
            else reEncodeAudio = true;
        }

        if (blocked)
        {
            return new MediaPlaybackPlan(MediaPlaybackAction.Unsupported, plans, containerOpens,
                Describe("unsupported", probe, plans));
        }

        if (reEncodeVideo || reEncodeAudio)
        {
            return new MediaPlaybackPlan(MediaPlaybackAction.Transcode, plans, containerOpens,
                Describe(reEncodeVideo ? "transcode (video)" : "transcode (audio only)", probe, plans));
        }

        // Every stream is copyable. The container alone now decides between serving the bytes and
        // repackaging them — the H.264-in-MKV case, and the whole reason Remux exists as a verdict.
        return containerOpens
            ? new MediaPlaybackPlan(MediaPlaybackAction.Direct, plans, containerOpens,
                Describe("direct", probe, plans))
            : new MediaPlaybackPlan(MediaPlaybackAction.Remux, plans, containerOpens,
                Describe("remux (container)", probe, plans));
    }

    /// <summary>
    /// A one-line summary for the host log. Log-only by contract: it names real codecs and containers, and
    /// nothing user-facing or wire-facing in this kit carries English prose (`ipc-contracts`).
    /// </summary>
    private static string Describe(string verdict, MediaProbeResult probe, List<MediaStreamPlan> plans)
    {
        var container = probe.Container is { Length: > 0 } c ? c : "(unknown container)";
        if (plans.Count == 0) return $"{verdict}: {container}, nothing probed";

        var offenders = plans.Where(p => p.NeedsReEncode)
            .Select(p => $"{p.Stream.Kind}:{p.Stream.Codec ?? "?"}")
            .ToArray();
        return offenders.Length == 0
            ? $"{verdict}: {container}, {plans.Count} stream(s) all decodable"
            : $"{verdict}: {container}, blocked by {string.Join(" + ", offenders)}";
    }
}
