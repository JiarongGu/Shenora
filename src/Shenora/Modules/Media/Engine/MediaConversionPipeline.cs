namespace Shenora.Modules.Media;

/// <summary>
/// One frame crossing the conversion seam — <b>the same record for a soundtrack and for a picture.</b>
/// ⚠ A <c>Kind is Video ? … : …</c> branch silently treats SUBTITLES as audio; key by kind instead.
/// </summary>
/// <param name="Data">
/// The compressed frame. On the way IN, as the container stored it; on the way OUT, in the form the target
/// container carries (length-prefixed, for both AAC in MP4 and H.264 in MP4).
/// </param>
/// <param name="PresentationTimeUs">
/// When this frame is SHOWN or HEARD, in microseconds from the start of the stream. The muxer scales into
/// the track timescale, so a conversion never has to know what that timescale is.
/// </param>
/// <param name="IsKeyframe">
/// True when this frame decodes without reference to any other — <b>always true for audio</b>, and the
/// sync-sample table for video. ⚠ Wrong in the safe-looking direction is still wrong: claim every video
/// frame and a seek lands on a green smear; claim none and the file cannot seek at all.
/// </param>
public readonly record struct MediaFrame(
    ReadOnlyMemory<byte> Data,
    long PresentationTimeUs,
    bool IsKeyframe = true);

/// <summary>
/// One converter in the chain: answer with a run, or return null to DECLINE and let the next try.
/// <b>Declining is how KIND is handled</b> — a converter that only does soundtracks looks at
/// <see cref="MediaStreamInfo.Kind"/> and returns null for a picture.
/// </summary>
/// <param name="source">
/// What the stream is. <see cref="MediaStreamInfo.SampleRate"/> and <see cref="MediaStreamInfo.Channels"/>
/// configure an audio codec; <see cref="MediaStreamInfo.Width"/> and <see cref="MediaStreamInfo.Height"/>
/// configure a video one. A codec told the wrong values does not fail — it produces audio at the wrong
/// speed, or a picture that is stretched or green.
/// </param>
/// <param name="codecPrivate">The container's initialisation data. Empty is legal and common.</param>
public delegate IMediaStreamConversionRun? MediaConversionMiddleware(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate);

/// <summary>
/// The conversion pipeline: a chain of converters asked frame by frame, for EVERY stream kind.
/// <para>
/// <b>Middleware, not replacement.</b> An app's own converter is ADDED to the chain and the kit's platform
/// converters stay behind it answering what the app's declines. <b>Later registrations are asked FIRST</b>,
/// so adding a converter means overriding.
/// </para>
/// </summary>
public sealed class MediaConversionPipeline : IMediaStreamConversion
{
    private readonly List<(MediaConversionMiddleware Converter, IReadOnlyList<MediaStreamClaim> Claims)> _entries = [];
    private readonly Lock _gate = new();
    private readonly IMediaCapability? _device;

    /// <summary>A pipeline that answers <see cref="CanConvert"/> by asking its converters.</summary>
    public MediaConversionPipeline() : this(null) { }

    /// <summary>
    /// A pipeline that can answer <see cref="CanConvert"/> from the DEVICE's own answer instead of by
    /// building codecs.
    /// </summary>
    /// <param name="device">
    /// What this device can decode, or null to fall back to constructing a run per question.
    /// <para>
    /// ⚠ Constructing a run to answer produces an over-claim (a promise made from the ENCODER alone, so the
    /// muxer fails after accepting a track) and an under-claim (a refusal for a codec that merely could not
    /// open a session without its file's ESDS).
    /// </para>
    /// </param>
    public MediaConversionPipeline(IMediaCapability? device) => _device = device;

    /// <summary>
    /// What the registered converters CLAIM, without building a single codec — the declaration half of
    /// <see cref="CanConvert"/>.
    /// </summary>
    public IReadOnlyList<MediaStreamClaim> Claims
    {
        get { lock (_gate) return [.. _entries.SelectMany(e => e.Claims)]; }
    }

    /// <summary>How the DECLARATION half answers.</summary>
    private enum Claimed
    {
        /// <summary>Nothing offers it. Refuse, free.</summary>
        No,

        /// <summary>A converter DECLARED it. The device decides.</summary>
        Yes,

        /// <summary>A claim-less converter is registered, so only asking the chain can tell.</summary>
        Ask,
    }

    /// <summary>Is this stream something a registered converter offers to attempt?</summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>A wildcard falls back to ASKING the chain, never to the device alone.</b> The device alone
    /// answers yes to <c>h264</c>, which every device decodes and this kit refuses to CONVERT because MP4
    /// carries it and the remuxer copies it losslessly.
    /// </para>
    /// <para>
    /// ⚠ <b>Asked PER CONVERTER, not against one flat list</b>, so a claim-less registration is a wildcard
    /// for itself only. Flattened, a converter registered WITHOUT claims beside one that declared some would
    /// have its codecs refused by <c>CanConvert</c> while <c>Begin</c> converted them happily.
    /// </para>
    /// </remarks>
    private Claimed IsClaimed(MediaStreamKind kind, string codec)
    {
        var wildcard = false;

        lock (_gate)
        {
            foreach (var (_, claims) in _entries)
            {
                if (claims.Count == 0)
                {
                    // Keep looking: an EXPLICIT claim elsewhere is a stronger answer than "might handle it".
                    wildcard = true;
                    continue;
                }

                foreach (var claim in claims)
                {
                    if (claim.Kind == kind && string.Equals(claim.Codec, codec, StringComparison.OrdinalIgnoreCase))
                    {
                        return Claimed.Yes;
                    }
                }
            }
        }

        return wildcard ? Claimed.Ask : Claimed.No;
    }

    /// <summary>Add a converter. Dispose the return value to remove it.</summary>
    public IDisposable Use(MediaConversionMiddleware converter) => Use(converter, []);

    /// <summary>Add a converter and DECLARE what it claims. Dispose the return value to remove both.</summary>
    /// <param name="converter">The converter, unchanged — declining is still how it opts out per stream.</param>
    /// <param name="claims">
    /// The (kind, codec) pairs this converter is willing to attempt. ⚠ A claim is not a promise: the DEVICE
    /// still decides. An EMPTY list means "ask me about anything".
    /// </param>
    public IDisposable Use(MediaConversionMiddleware converter, IReadOnlyList<MediaStreamClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(claims);

        lock (_gate)
        {
            _entries.Add((converter, claims));
        }

        return new Registration(this, converter, claims);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 🔴 <b>TWO QUESTIONS, IN ORDER: does the kit CLAIM it, and can the DEVICE do it.</b> A claim is
    /// checked first and a NO there is final — the kit will not attempt what it does not offer, whatever
    /// the hardware can do.
    /// </remarks>
    public bool CanConvert(MediaStreamKind kind, string codec)
    {
        if (string.IsNullOrWhiteSpace(codec)) return false;

        // THE DECLARATION. Free, and a no here is final.
        var claimed = IsClaimed(kind, codec);
        if (claimed is Claimed.No) return false;

        // THE DEVICE — but ONLY for something explicitly declared; a wildcard falls through to the chain.
        // ⚠ `Contains` uses the SET's comparer, so an app-supplied IMediaCapability built on a plain
        // HashSet would answer case-sensitively while the claim half above is case-INsensitive — reading
        // as "this device cannot decode ac3" for a container that spelled it `AC3`. The contract says
        // OrdinalIgnoreCase (see IMediaCapability.Decodable).
        if (claimed is Claimed.Yes && _device is not null)
        {
            return _device.Decodable(kind).Covers(codec);
        }
        // ⚠ The probe carries the DIMENSIONS a video stream would have: a platform video encoder refuses to
        // configure at 0x0, so a probe without them answers "cannot convert" for a codec the device handles
        // perfectly. 640x360 is arbitrary, valid, and never encodes anything.
        var probe = kind is MediaStreamKind.Video
            ? new MediaStreamInfo(kind, codec, Width: 640, Height: 360)
            : new MediaStreamInfo(kind, codec);
        using var run = Begin(probe, ReadOnlyMemory<byte>.Empty);
        return run is not null;
    }

    /// <inheritdoc />
    public IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate)
    {
        ArgumentNullException.ThrowIfNull(source);

        MediaConversionMiddleware[] snapshot;
        lock (_gate) snapshot = [.. _entries.Select(e => e.Converter)];

        // Last registered, first asked.
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            var run = snapshot[i](source, codecPrivate);
            if (run is not null) return run;
        }
        return null;
    }

    private sealed class Registration(MediaConversionPipeline owner, MediaConversionMiddleware converter,
                                      IReadOnlyList<MediaStreamClaim> claims) : IDisposable
    {
        /// <summary>
        /// ⚠ Removes the ENTRY, so a converter's claims leave with it — otherwise
        /// <see cref="CanConvert"/> keeps saying yes to a codec whose converter is gone.
        /// </summary>
        public void Dispose()
        {
            lock (owner._gate)
            {
                var index = owner._entries.FindIndex(e => e.Converter == converter && e.Claims == claims);
                if (index >= 0) owner._entries.RemoveAt(index);
            }
        }
    }
}

/// <summary>
/// One stream kind + codec a converter offers to attempt — the DECLARATION half of
/// <see cref="IMediaStreamConversion.CanConvert"/>.
/// </summary>
/// <remarks>
/// ⚠ A claim is not a promise: "the kit offers to try mpeg4" and "this device can decode mpeg4" are
/// different facts, and the DEVICE half still decides.
/// </remarks>
/// <param name="Kind">Which kind of stream.</param>
/// <param name="Codec">
/// The codec name as a probe reports it (<c>ac3</c>, <c>mpeg4</c>) — lowercase by convention and compared
/// case-insensitively, because a container's spelling is not something a caller should have to match.
/// </param>
public readonly record struct MediaStreamClaim(MediaStreamKind Kind, string Codec);

/// <summary>
/// What a CONSUMER of conversion sees: ask whether a stream can be handled, and begin one. Smaller than
/// <see cref="MediaConversionPipeline"/>, which additionally lets converters be ADDED.
/// </summary>
public interface IMediaStreamConversion
{
    /// <summary>
    /// Can this device turn a <paramref name="kind"/> stream in <paramref name="codec"/> into something the
    /// container carries and the webview plays?
    /// <para>
    /// Asked BEFORE any work starts: the answer to "no" is <see cref="MediaPlaybackAction.Unsupported"/>
    /// rather than a conversion that fails halfway and leaves a partial file.
    /// </para>
    /// </summary>
    bool CanConvert(MediaStreamKind kind, string codec);

    /// <summary>
    /// Begin converting one stream, or null when this device cannot. ⚠ Null rather than an exception:
    /// "this device lacks that codec" is an ordinary answer here.
    /// </summary>
    /// <param name="source">
    /// What the stream IS, including the values a platform codec is CONFIGURED with before it is fed
    /// anything: rate and channels for audio, width and height for video.
    /// </param>
    /// <param name="codecPrivate">
    /// The codec's initialisation data as the container stored it — Matroska's <c>CodecPrivate</c>, which is
    /// an <c>avcC</c>, an AudioSpecificConfig, or a Vorbis header set depending on the codec.
    /// ⚠ <b>Empty is legal and common</b> (AC-3 needs none), but for the codecs that DO need it a decoder
    /// configured without it produces silence, or a green picture, rather than an error.
    /// </param>
    IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate);
}

/// <summary>
/// One stream's conversion in progress. Dispose to release the platform codecs.
/// <para>
/// ⚠ <b>Disposing MATTERS here in a way it does not for a managed object.</b> Every platform hands out a
/// hardware or system codec instance and a device has only a handful — a video run holds TWO. Leaking one
/// does not leak memory, it makes the NEXT conversion in the app fail with a resource error that names
/// nothing.
/// </para>
/// </summary>
public interface IMediaStreamConversionRun : IDisposable
{
    /// <summary>
    /// What the output IS: codec, and then rate and channels or width and height. ⚠ A decoder may resample
    /// or downmix (5.1 AC-3 to stereo AAC) and an encoder may align dimensions up to a macroblock boundary,
    /// so this is <b>not</b> the input echoed back — the muxer must build its sample entry from here.
    /// </summary>
    MediaStreamInfo OutputFormat { get; }

    /// <summary>
    /// The decoder configuration the OUTPUT needs — an AudioSpecificConfig for AAC, an <c>avcC</c> for
    /// H.264 — which the container must carry in its sample entry before the first frame.
    /// <para>
    /// ⚠ Only knowable after the encoder has been fed. Reading it early is legal and returns empty; writing
    /// THAT into a file produces one that opens and plays nothing, so <see cref="Mp4Remuxer"/> reads it
    /// after <see cref="Drain"/>.
    /// </para>
    /// </summary>
    ReadOnlyMemory<byte> OutputConfig { get; }

    /// <summary>
    /// How many samples one output frame represents (1024 for AAC), for the timing table.
    /// ⚠ <b>AUDIO ONLY.</b> A video run answers 0 and its timing comes from each frame's presentation time.
    /// </summary>
    int OutputFramesPerPacket { get; }

    /// <summary>
    /// Feed one compressed input frame; returns whatever came out, which is often nothing.
    /// <para>
    /// ⚠ <b>Zero outputs is NORMAL and not an error.</b> Codecs buffer — for video a GOP-sized window, so
    /// the first outputs can be dozens of frames behind. A caller that treats an empty return as failure
    /// abandons a perfectly good conversion in its opening second.
    /// </para>
    /// <para>
    /// 🔴 <b>Outputs are in DECODE order — the order to write them — and each carries its own presentation
    /// time.</b> The two differ when B-frames are used; a caller that assumes emission order is presentation
    /// order writes a file that plays out of order.
    /// </para>
    /// </summary>
    IReadOnlyList<MediaFrame> Push(MediaFrame frame);

    /// <summary>
    /// End of input: return everything still buffered.
    /// <para>
    /// 🔴 <b>Skipping this truncates the stream and nothing reports it.</b> The tail sits inside the codec,
    /// the file is well-formed, and playback simply stops early — by more for a picture than a soundtrack,
    /// because the encoder's window is a GOP rather than a few packets.
    /// </para>
    /// </summary>
    IReadOnlyList<MediaFrame> Drain();
}
