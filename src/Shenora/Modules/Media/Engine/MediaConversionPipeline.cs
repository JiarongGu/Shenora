namespace Shenora.Modules.Media;

/// <summary>
/// One frame crossing the conversion seam — <b>the same record for a soundtrack and for a picture.</b>
///
/// <para>
/// 🔴 <b>Why one type and not two.</b> This kit segments and streams both kinds through the same tables:
/// every frame has a position in the file, a time it is shown, and a flag saying whether it can be decoded
/// on its own. Audio simply answers "yes" to the last one for every frame, which is a VALUE, not a
/// different shape. Splitting the record would have split the remuxer, the segment engine and every test
/// behind it — and this repo already knows where that ends: the planner's own note records that branching
/// <c>Kind is Video ? … : …</c> silently treated SUBTITLES as audio, and that keying by kind removed the
/// branch and the bug together.
/// </para>
/// </summary>
/// <param name="Data">
/// The compressed frame. On the way IN, as the container stored it; on the way OUT, in the form the target
/// container carries (length-prefixed, for both AAC in MP4 and H.264 in MP4).
/// </param>
/// <param name="PresentationTimeUs">
/// When this frame is SHOWN or HEARD, in microseconds from the start of the stream. ⚠ Microseconds because
/// both platforms speak them (Android's <c>presentationTimeUs</c>, iOS's <c>CMTime</c> converted once); the
/// muxer scales into the track timescale, so a conversion never has to know what that timescale is.
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
///
/// <para>
/// The same shape as <c>WebViewResourceMiddleware</c>, deliberately — this kit already has a middleware
/// idiom and a second, different one for the same kind of job would be a thing to learn twice.
/// </para>
/// <para>
/// <b>Declining is how KIND is handled.</b> A converter that only does soundtracks looks at
/// <see cref="MediaStreamInfo.Kind"/> and returns null for a picture; there is no separate registry, no
/// per-kind interface, and nothing that can be registered into the wrong one.
/// </para>
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
/// The conversion pipeline: a chain of converters, asked in registration order, for EVERY stream kind.
///
/// <para>
/// <b>Tier 2 of D52's engine tiers, and the narrowest thing that closes the gap tier 1 leaves.</b>
/// <see cref="Mp4Remuxer"/> repairs the CONTAINER for nothing; what it cannot repair is a stream the
/// container will not hold. AC-3, E-AC-3 and DTS are routine inside an <c>.mkv</c> and play in no browser;
/// MPEG-4 Part 2 is decoded by the device and refused by its own webview. Repairing either means decoding
/// one stream and encoding another, which is two platform calls: zero bytes shipped, zero licence weight,
/// no codec written (D51/D52).
/// </para>
/// <para>
/// 🔴 <b>Why this is per-FRAME and not "convert this file".</b> A two-hour soundtrack is gigabytes as PCM
/// and a two-hour picture far more, so a whole-stream call cannot run on a phone at all. It is also the
/// shape every platform already has — <c>MediaCodec</c>, <c>AudioConverter</c> and <c>VideoToolbox</c> are
/// all fed buffers and drained — so a file-shaped seam would force every implementation to build a queue
/// behind it.
/// </para>
/// <para>
/// 🔴 <b>Middleware, not replacement</b> — an app supplying its own converter ADDS it to the chain, and the
/// kit's platform converters stay behind it answering everything the app's does not. Wanting a better DTS
/// decoder does not mean re-providing AC-3, AAC and the picture as well (owner, 2026-08-07: *"so the
/// consumer can also reuse our built in convertor"*). Later registrations are asked FIRST: adding a
/// converter means to override.
/// </para>
/// <para>
/// ⚠ <b>What the kit still does not do:</b> pick codecs, choose bitrates, or ship an encoder. It asks the
/// platform for the conversion the web needs and refuses what it cannot do — a device that cannot decode
/// AC-3 answers false from <see cref="IMediaStreamConversion.CanConvert"/> and the planner says
/// <see cref="MediaPlaybackAction.Unsupported"/> rather than starting work that cannot finish.
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
    /// 🔴 <b>THIS SEPARATES TWO QUESTIONS THAT WERE FUSED, and the fusion cost a day (2026-08-13).</b>
    /// "Does the kit CLAIM this codec" is a declaration; "can this DEVICE do it" is a measurement. Answering
    /// both by constructing the converter's decoder and encoder on every ask produced an over-claim (a
    /// promise made from the encoder alone, so the muxer failed after accepting a track) and an under-claim
    /// (a refusal for a codec that merely could not open a session without its file's ESDS) in one evening.
    /// </para>
    /// </param>
    public MediaConversionPipeline(IMediaCapability? device) => _device = device;

    /// <summary>
    /// What the registered converters CLAIM, without building a single codec — the declaration half of
    /// <see cref="CanConvert"/>.
    /// <para>
    /// ⚠ Inspectable on purpose. "Which pictures does this shell claim?" used to cost two hardware codec
    /// instances per codec asked, so nothing asked it and a whole platform went years without a video
    /// converter unnoticed.
    /// </para>
    /// </summary>
    public IReadOnlyList<MediaStreamClaim> Claims
    {
        get { lock (_gate) return [.. _entries.SelectMany(e => e.Claims)]; }
    }

    /// <summary>How the DECLARATION half answers — three states, because two were not enough.</summary>
    private enum Claimed
    {
        /// <summary>Nothing offers it. Refuse, free.</summary>
        No,

        /// <summary>A converter DECLARED it. The device decides.</summary>
        Yes,

        /// <summary>
        /// A claim-less converter is registered, so this MIGHT be handled and only asking can tell.
        /// </summary>
        Ask,
    }

    /// <summary>
    /// Is this stream something a registered converter offers to attempt?
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>THREE STATES, AND THE THIRD WAS FOUND ON A DEVICE (2026-08-13).</b> A two-state version
    /// answered "claimed" for anything as soon as ONE claim-less converter was registered — and the kit
    /// always has one, because the audio converters predate claims. The device then became the only gate, so
    /// <c>h264</c> reported <c>accepted=True</c>: every device decodes it, and this kit deliberately refuses
    /// to CONVERT it because MP4 carries it and the remuxer copies it losslessly. A wildcard must therefore
    /// fall back to ASKING the chain — the pre-claims behaviour — never to the device alone.
    /// </para>
    /// <para>
    /// ⚠ <b>PER CONVERTER, not one flat list.</b> With a flat list, a converter registered WITHOUT claims
    /// beside one that declared some would have its codecs refused by <c>CanConvert</c> while <c>Begin</c>
    /// converted them happily. Asked per entry, a claim-less registration is a wildcard for itself only.
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

    /// <summary>
    /// Add a converter. Dispose the return value to remove it.
    /// <para>
    /// ⚠ Removable for the same reason a route is: a converter that outlives the feature it served would
    /// answer for the next one, which is the bug class the interceptor's <c>Use</c> already documents.
    /// </para>
    /// </summary>
    public IDisposable Use(MediaConversionMiddleware converter) => Use(converter, []);

    /// <summary>
    /// Add a converter and DECLARE what it claims. Dispose the return value to remove both.
    /// </summary>
    /// <param name="converter">The converter, unchanged — declining is still how it opts out per stream.</param>
    /// <param name="claims">
    /// The (kind, codec) pairs this converter is willing to attempt. ⚠ A claim is not a promise: the DEVICE
    /// still decides, which is the whole point of keeping the two apart. An EMPTY list means "ask me about
    /// anything" — the pre-declaration behaviour, so an existing converter keeps working untouched.
    /// <para>
    /// ⚠ <b>This is the "support what we support, ignore the rest" seam, and it is deliberately NOT a second
    /// mechanism.</b> Declining in the converter still works and still wins per stream; a claim only lets
    /// the pipeline answer a NO without building codecs, and lets an app SEE what a shell offers.
    /// </para>
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
    /// <para>
    /// 🔴 <b>TWO QUESTIONS, IN ORDER: does the kit CLAIM it, and can the DEVICE do it.</b> A claim is
    /// checked first because it is free and because a NO there is final — the kit will not attempt what it
    /// does not offer, whatever the hardware can do. Only then is the device consulted.
    /// </para>
    /// <para>
    /// ⚠ <b>Both fallbacks are the OLD behaviour, so nothing that worked stops working:</b> a converter
    /// registered without claims is asked about anything, and with no <see cref="IMediaCapability"/> the
    /// device half is answered the way it always was — by building a run and seeing whether it starts.
    /// </para>
    /// </remarks>
    public bool CanConvert(MediaStreamKind kind, string codec)
    {
        // ⚠ The probe carries the DIMENSIONS a video stream would have, because a platform video encoder
        // refuses to configure at 0x0 — so a probe without them would answer "cannot convert" for a codec
        // the device handles perfectly. That is precisely the failure the owner named: taking what IS
        // supported as unsupported. 640x360 is arbitrary, valid, and never encodes anything.
        if (string.IsNullOrWhiteSpace(codec)) return false;

        // THE DECLARATION. Free, and a no here is final.
        var claimed = IsClaimed(kind, codec);
        if (claimed is Claimed.No) return false;

        // THE DEVICE — but ONLY for something explicitly declared. A wildcard falls through to the chain,
        // because "some converter might handle it" is not the same as "this codec is offered", and the
        // device's yes would otherwise promise a conversion nobody performs (h264 on every phone).
        // ⚠ `Contains` uses the SET's comparer, so an app-supplied IMediaCapability built on a plain
        // HashSet would answer case-sensitively while the claim half above is explicitly
        // case-INsensitive — the two halves of one question disagreeing, which reads as "this device
        // cannot decode ac3" for a container that spelled it `AC3`. The contract says
        // OrdinalIgnoreCase (see IMediaCapability.Decodable) and every shipped implementation obeys it;
        // this does not re-derive the set, it just refuses to depend on a stranger's comparer.
        if (claimed is Claimed.Yes && _device is not null)
        {
            return _device.Decodable(kind).Contains(codec, StringComparer.OrdinalIgnoreCase);
        }
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

        // Last registered, first asked — an app that adds one means to override the default.
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
        /// ⚠ Removes the ENTRY, so a converter's claims leave with it. Leaving them behind would let
        /// <see cref="CanConvert"/> keep saying yes to a codec whose converter is gone — the same
        /// "outlives the feature it served" bug the removability exists to prevent.
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
/// <para>
/// 🔴 <b>A claim is not a promise, and keeping those apart is the whole point.</b> "The kit offers to try
/// mpeg4" and "this device can decode mpeg4" are different facts, and fusing them cost a day on 2026-08-13:
/// answering both at once by constructing the converter's codecs produced a promise made from an ENCODER
/// alone, and separately a refusal of a codec that merely lacked its file's ESDS.
/// </para>
/// <para>
/// ⚠ <b>It is also the cheap, inspectable answer to "what does this shell support?"</b> — which nothing asked
/// for pictures, because asking cost two hardware codec instances per codec. That is how a shell with no
/// video converter at all stayed unnoticed.
/// </para>
/// </remarks>
/// <param name="Kind">Which kind of stream.</param>
/// <param name="Codec">
/// The codec name as a probe reports it (<c>ac3</c>, <c>mpeg4</c>) — lowercase by convention and compared
/// case-insensitively, because a container's spelling is not something a caller should have to match.
/// </param>
public readonly record struct MediaStreamClaim(MediaStreamKind Kind, string Codec);

/// <summary>
/// What a CONSUMER of conversion sees: ask whether a stream can be handled, and begin one.
///
/// <para>
/// Deliberately smaller than <see cref="MediaConversionPipeline"/>, which additionally lets converters be
/// ADDED. The remuxer and the planner only ever ask, so they take this; an app composing the chain takes
/// the pipeline. Splitting them means a caller cannot accidentally mutate a pipeline it was only meant to
/// consult.
/// </para>
/// </summary>
public interface IMediaStreamConversion
{
    /// <summary>
    /// Can this device turn a <paramref name="kind"/> stream in <paramref name="codec"/> into something the
    /// container carries and the webview plays?
    /// <para>
    /// Asked BEFORE any work starts, because the honest answer to "no" is
    /// <see cref="MediaPlaybackAction.Unsupported"/> — a refusal the app can act on — rather than a
    /// conversion that fails halfway and leaves a partial file.
    /// </para>
    /// <para>
    /// ⚠ <b>The kind is a parameter here and a property on <see cref="MediaStreamInfo"/> in
    /// <see cref="Begin"/></b>, because this question arrives with nothing but a codec NAME — and the same
    /// name can mean different things per kind, so answering it without the kind is guessing.
    /// </para>
    /// </summary>
    bool CanConvert(MediaStreamKind kind, string codec);

    /// <summary>
    /// Begin converting one stream, or null when this device cannot.
    /// <para>
    /// ⚠ Null rather than an exception: "this device lacks that codec" is an ordinary answer here, and it is
    /// the SAME answer <see cref="IMediaStreamConversion.CanConvert"/> gives — a caller that checked first should not have to catch
    /// as well.
    /// </para>
    /// </summary>
    /// <param name="source">
    /// What the stream IS, including the values a platform codec is CONFIGURED with before it is fed
    /// anything: rate and channels for audio, width and height for video.
    /// </param>
    /// <param name="codecPrivate">
    /// The codec's initialisation data as the container stored it — Matroska's <c>CodecPrivate</c>, which is
    /// an <c>avcC</c>, an AudioSpecificConfig, or a Vorbis header set depending on the codec.
    /// <para>
    /// ⚠ <b>Empty is legal and common</b> (AC-3 needs none), but for the codecs that DO need it a decoder
    /// configured without it produces silence, or a green picture, rather than an error.
    /// </para>
    /// </param>
    IMediaStreamConversionRun? Begin(MediaStreamInfo source, ReadOnlyMemory<byte> codecPrivate);
}

/// <summary>
/// One stream's conversion in progress. Dispose to release the platform codecs.
///
/// <para>
/// ⚠ <b>Disposing MATTERS here in a way it does not for a managed object.</b> Every platform hands out a
/// hardware or system codec instance and a device has only a handful — a video run holds TWO, a decoder and
/// an encoder. Leaking one does not leak memory, it makes the NEXT conversion in the app fail with a
/// resource error that names nothing.
/// </para>
/// </summary>
public interface IMediaStreamConversionRun : IDisposable
{
    /// <summary>
    /// What the output IS — the same record the input arrived as, so the muxer builds its sample entry from
    /// the run rather than from assumptions: codec, and then rate and channels or width and height.
    /// <para>
    /// ⚠ A decoder may resample or downmix (5.1 AC-3 to stereo AAC) and an encoder may align dimensions up
    /// to a macroblock boundary, so this is not the input echoed back.
    /// </para>
    /// </summary>
    MediaStreamInfo OutputFormat { get; }

    /// <summary>
    /// The decoder configuration the OUTPUT needs — an AudioSpecificConfig for AAC, an <c>avcC</c> for
    /// H.264 — which the container must carry in its sample entry before the first frame.
    /// <para>
    /// ⚠ It is only knowable after the encoder has been fed, so it is a property read at the END rather than
    /// a constructor argument. Reading it early is legal and returns empty; writing THAT into a file produces
    /// one that opens and plays nothing, which is why <see cref="Mp4Remuxer"/> reads it after
    /// <see cref="Drain"/>.
    /// </para>
    /// </summary>
    ReadOnlyMemory<byte> OutputConfig { get; }

    /// <summary>
    /// How many samples one output frame represents (1024 for AAC), for the timing table.
    /// <para>
    /// ⚠ <b>AUDIO ONLY.</b> A video run answers 0 and its timing comes from each frame's presentation time
    /// instead — the one genuinely kind-specific number in this contract, and it is a property rather than a
    /// separate interface because a number that does not apply is cheaper than a type that must be
    /// downcast.
    /// </para>
    /// </summary>
    int OutputFramesPerPacket { get; }

    /// <summary>
    /// Feed one compressed input frame; returns whatever came out, which is often nothing.
    /// <para>
    /// ⚠ <b>Zero outputs is NORMAL and not an error.</b> Codecs buffer: a decoder needs several frames before
    /// it emits, and an encoder holds a window — for video a GOP-sized one, so the first outputs can be
    /// dozens of frames behind. A caller that treats an empty return as failure abandons a perfectly good
    /// conversion in its opening second.
    /// </para>
    /// <para>
    /// 🔴 <b>Outputs are in DECODE order — the order to write them — and each carries its own presentation
    /// time.</b> The two differ exactly when B-frames are used, which is what the composition table exists
    /// for; a caller that assumes emission order is presentation order writes a file that plays out of order.
    /// </para>
    /// </summary>
    IReadOnlyList<MediaFrame> Push(MediaFrame frame);

    /// <summary>
    /// End of input: return everything still buffered.
    /// <para>
    /// 🔴 <b>Skipping this truncates the stream and nothing reports it.</b> The tail sits inside the codec,
    /// the file is well-formed, and playback simply stops early — the same "exit 0 is not evidence" shape
    /// <see cref="ISegmentEngine.HasRenderedPicture"/> exists for. It costs more for a picture than a
    /// soundtrack, because the encoder's window is a GOP rather than a few packets.
    /// </para>
    /// </summary>
    IReadOnlyList<MediaFrame> Drain();
}
