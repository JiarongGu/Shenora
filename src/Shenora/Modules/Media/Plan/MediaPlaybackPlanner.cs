namespace Shenora.Modules.Media;

/// <summary>What has to happen before a player can open a file.</summary>
public enum MediaPlaybackAction
{
    /// <summary>Serve the bytes untouched. The container opens and every stream decodes.</summary>
    Direct,

    /// <summary>Repackage into a container the player can open, COPYING every stream — fast and lossless.
    /// The H.264-in-MKV case, where only the box is wrong.</summary>
    Remux,

    /// <summary>Re-encode at least one stream. Slow and lossy; <see cref="MediaPlaybackPlan.Streams"/>
    /// names the stream that forced it.</summary>
    Transcode,

    /// <summary>A stream needs re-encoding and the policy offers no encoder for its kind. An honest
    /// refusal, so the app can hand the file to an external player instead of failing silently.</summary>
    Unsupported,
}

/// <summary>
/// What the app's player can open, expressed as sets the app owns. ⚠ <b>The kit ships NO default</b>
/// (D42): there is no correct universal list — a browser's set differs from a bundled engine's, Android's
/// differs PER DEVICE because codec support is vendor-declared, and a licensed codec like AC-3 may be
/// present on one handset and absent on the next.
/// </summary>
public sealed record MediaPlaybackPolicy
{
    /// <summary>
    /// Containers the player can OPEN, as lowercase extensions including the dot. Checked separately from
    /// the codecs: an <c>.mkv</c> holding perfectly ordinary AAC still cannot be opened, so a codec-only
    /// answer calls it playable and the player then refuses.
    /// </summary>
    public IReadOnlySet<string> Containers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Codec names the player can DECODE, keyed by stream kind, as a probe reports them (<c>h264</c>,
    /// <c>aac</c>). A kind that is absent decodes nothing. Keyed by <see cref="MediaStreamKind"/> to match
    /// <see cref="IMediaCapability"/>, which asks the DEVICE the same question — the gap between the two
    /// IS the converter's job (D59).
    /// <para>
    /// ⚠ AUDIO is the set most likely to be the reason a file fails. Licensed audio — AC-3, E-AC-3, DTS —
    /// is not in Android's mandatory set, so a file whose picture decodes perfectly plays with NO SOUND.
    /// That is why this planner is per-stream: a single verdict would call that case unsupported and throw
    /// away a remux that fixes it.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<MediaStreamKind, IReadOnlySet<MediaStreamCodec>> Codecs { get; init; } =
        new Dictionary<MediaStreamKind, IReadOnlySet<MediaStreamCodec>>();

    /// <summary>
    /// Kinds the app can RE-ENCODE. A kind that is absent turns an undecodable stream into
    /// <see cref="MediaPlaybackAction.Unsupported"/> instead of a transcode it cannot perform.
    /// </summary>
    public IReadOnlySet<MediaStreamKind> Encodable { get; init; } = new HashSet<MediaStreamKind>();

    /// <summary>Codecs this player decodes for <paramref name="kind"/>; empty when it decodes none.</summary>
    public IReadOnlySet<MediaStreamCodec> CodecsFor(MediaStreamKind kind) =>
        Codecs.TryGetValue(kind, out var set) ? set : EmptyCodecs;

    /// <summary>Can the app re-encode <paramref name="kind"/>?</summary>
    public bool CanEncode(MediaStreamKind kind) => Encodable.Contains(kind);

    private static readonly IReadOnlySet<MediaStreamCodec> EmptyCodecs = new HashSet<MediaStreamCodec>();

    /// <summary>
    /// Does this player decode <paramref name="codec"/> for <paramref name="kind"/>? Applies
    /// <see cref="MediaStreamCodec"/>'s matching rule, so a policy entry with no profile covers any profile.
    /// </summary>
    public bool Decodes(MediaStreamKind kind, MediaStreamCodec codec) => CodecsFor(kind).Covers(codec);
}

/// <summary>
/// What the planner concluded about ONE stream.
/// <para>
/// ⚠ Was two bools, <c>DecodesNatively</c> + <c>NeedsReEncode</c>, which could not tell three different
/// facts apart: a stream that genuinely decodes, one whose codec is UNKNOWN and was given the benefit of
/// the doubt, and a subtitle recorded but never counted. All three read as "decodes natively", so a
/// consumer could not distinguish a certainty from a guess.
/// </para>
/// </summary>
public enum MediaStreamVerdict
{
    /// <summary>The policy lists this codec — it plays.</summary>
    Decodes,

    /// <summary>
    /// The codec is UNNAMED and the container is one the policy accepts, so it is given the benefit of
    /// the doubt. ⚠ A GUESS, not a promise: distinguishable from <see cref="Decodes"/> precisely so a
    /// caller that must not guess can refuse.
    /// </summary>
    Assumed,

    /// <summary>
    /// A subtitle or unknown KIND. Recorded so the stream list is complete, and it never votes on the
    /// file's action.
    /// </summary>
    Droppable,

    /// <summary>The policy does not list this codec — this stream is what forces a transcode.</summary>
    NeedsReEncode,
}

/// <summary>One stream's verdict, and why.</summary>
/// <param name="Stream">The stream as probed.</param>
/// <param name="Verdict">What the planner concluded. See <see cref="MediaStreamVerdict"/>.</param>
public sealed record MediaStreamPlan(MediaStreamInfo Stream, MediaStreamVerdict Verdict)
{
    /// <summary>
    /// True when this stream does not force a transcode — <see cref="MediaStreamVerdict.Decodes"/>,
    /// <see cref="MediaStreamVerdict.Assumed"/> or <see cref="MediaStreamVerdict.Droppable"/>.
    /// ⚠ Convenience over the verdict, NOT a second source of truth: it cannot tell a certainty from a
    /// guess, which is the distinction the enum exists for.
    /// </summary>
    public bool Plays => Verdict is not MediaStreamVerdict.NeedsReEncode;
}

/// <summary>The whole verdict for a file.</summary>
/// <param name="Action">What has to happen.</param>
/// <param name="Streams">Per-stream detail. Empty when nothing probed the file.</param>
/// <param name="ContainerOpens">Whether the container itself is playable, independently of the streams.</param>
/// <param name="Reason">
/// A short, non-localised explanation for the host LOG — never for a user and never for the wire: it
/// names codecs and containers, and this kit's error contract is a code plus parameters
/// (`ipc-contracts`).
/// </param>
public sealed record MediaPlaybackPlan(
    MediaPlaybackAction Action,
    IReadOnlyList<MediaStreamPlan> Streams,
    bool ContainerOpens,
    string Reason);

/// <summary>
/// Decides what must happen before a player can open a file — <b>per STREAM, not per file</b>. A pure
/// function with no I/O; the verdicts live in <see cref="MediaPlaybackPolicy"/> and only the mechanism is
/// here.
/// <para>
/// <b>Per-stream</b> (D42) because the frequent real failure is not "this file will not play", it is
/// <i>picture with no sound</i>: H.264 that decodes perfectly beside AC-3 that does not. A single
/// <c>CanPlay(file) -&gt; bool</c> is wrong in the most common failure case and discards the cheap fix —
/// copy the video, re-encode only the sound.
/// </para>
/// </summary>
public static class MediaPlaybackPlanner
{
    /// <summary>
    /// Plan playback for a probed file under an app's policy. The order is load-bearing:
    /// <list type="number">
    /// <item><b>The CONTAINER is decided first and separately.</b> An <c>.mkv</c> carrying ordinary AAC
    /// cannot be opened, so testing codecs first calls the file playable and the player then refuses.</item>
    /// <item><b>An UNPROBED file gets the benefit of the doubt</b> — no streams means no probe ran, which
    /// is normal for an external tool the app may not have installed, so the container alone decides and
    /// an accepted one is <see cref="MediaPlaybackAction.Direct"/> rather than a needless transcode.</item>
    /// <item><b>An unknown codec is treated as decodable</b>, but only when the container opens: guessing
    /// "broken" on missing information turns absent tooling into failed playback.</item>
    /// <item><b>Subtitles and unknown streams never force a transcode</b> — they are droppable, and a
    /// player that cannot render a subtitle track still plays the film.</item>
    /// </list>
    /// </summary>
    /// <param name="probe">What a probe found. May be empty; may be sparsely populated.</param>
    /// <param name="policy">What the app's player can open. Its sets are the app's, never the kit's.</param>
    /// <returns>The verdict, with per-stream detail and a log-only reason.</returns>
    public static MediaPlaybackPlan Plan(MediaProbeResult probe, MediaPlaybackPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(policy);

        // Step 1 — the container, on its own. A null container falls through to Remux below rather than
        // Direct: repackaging is the safe answer when the wrapper cannot even be named.
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
                plans.Add(new MediaStreamPlan(stream, MediaStreamVerdict.Droppable));
                continue;
            }

            // Step 3 — an unnamed codec is given the benefit of the doubt. ⚠ ONE keyed lookup for EVERY
            // kind (`CodecsFor`), never a per-kind branch: branching video-vs-audio silently treats
            // subtitles — and any kind added later — as AUDIO.
            var named = stream.Codec is { Length: > 0 } codec;
            var verdict = !named ? MediaStreamVerdict.Assumed
                : policy.Decodes(stream.Kind, new MediaStreamCodec(stream.Codec!, stream.Profile)) ? MediaStreamVerdict.Decodes
                : MediaStreamVerdict.NeedsReEncode;
            plans.Add(new MediaStreamPlan(stream, verdict));

            if (verdict is not MediaStreamVerdict.NeedsReEncode) continue;

            // Can the policy actually perform this re-encode? If not, Unsupported lets the app hand the
            // file to an external player rather than start a conversion that cannot finish.
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

        // Every stream is copyable — the container alone now decides serve versus repackage.
        return containerOpens
            ? new MediaPlaybackPlan(MediaPlaybackAction.Direct, plans, containerOpens,
                Describe("direct", probe, plans))
            : new MediaPlaybackPlan(MediaPlaybackAction.Remux, plans, containerOpens,
                Describe("remux (container)", probe, plans));
    }

    /// <summary>A one-line summary for the host LOG only — it names real codecs and containers, and
    /// nothing user-facing or wire-facing in this kit carries English prose (`ipc-contracts`).</summary>
    private static string Describe(string verdict, MediaProbeResult probe, List<MediaStreamPlan> plans)
    {
        var container = probe.Container is { Length: > 0 } c ? c : "(unknown container)";
        if (plans.Count == 0) return $"{verdict}: {container}, nothing probed";

        var offenders = plans.Where(p => p.Verdict is MediaStreamVerdict.NeedsReEncode)
            .Select(p => $"{p.Stream.Kind}:{p.Stream.Codec ?? "?"}")
            .ToArray();
        return offenders.Length == 0
            ? $"{verdict}: {container}, {plans.Count} stream(s) all decodable"
            : $"{verdict}: {container}, blocked by {string.Join(" + ", offenders)}";
    }
}
