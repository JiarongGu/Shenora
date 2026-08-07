// CodecQuery is WinRT (Windows 10 1809), so the same multi-target split the playback session uses applies
// here: this file is the versioned half, WindowsMediaCapability.Unsupported.cs refuses by name on plain
// net10.0-windows. Guarding the FILE rather than each body, for the reason recorded there.
#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora.Core;
using Shenora.Media;
// `global::` on every WinRT namespace, and it is not optional: inside `namespace Shenora.Windows` the bare
// identifier `Windows` binds to THIS namespace, so `Windows.Media` resolves to `Shenora.Windows.Windows.Media`
// and fails with a confusing CS0234. Same trap WindowsPlaybackSession documents.
using CodecQuery = global::Windows.Media.Core.CodecQuery;
using CodecKind = global::Windows.Media.Core.CodecKind;
using CodecCategory = global::Windows.Media.Core.CodecCategory;

namespace Shenora.Windows;

/// <summary>
/// The desktop shell's <see cref="IMediaCapability"/> — what THIS Windows machine can decode and encode,
/// asked of the platform through <c>CodecQuery</c> rather than assumed.
/// <para>
/// <b>Why this exists: the planner had no answer on desktop.</b> Both mobile shells registered an
/// <see cref="IMediaCapability"/> and Windows registered none, so <c>MediaPlaybackPlanner</c> could ask the
/// device what it supports on a phone and had to be told on a PC. That asymmetry is the thing D42 exists to
/// prevent — the kit ships the QUESTION, never a hard-coded codec list, and a shell that cannot answer it
/// pushes the guess back onto every app.
/// </para>
/// <para>
/// <b>⚠ It answers about the MACHINE, not about the webview.</b> WebView2 accepts a narrower set than
/// Media Foundation decodes — a machine with an HEVC extension installed decodes HEVC while the element
/// still refuses it. That is exactly the delta D59 names as the converter's job, and it is why the two
/// inputs to the planner are separate: this is the DEVICE half, <c>MediaPlaybackPolicy</c> is the webview
/// half, and the gap between them is what gets converted.
/// </para>
/// <para>
/// Results are cached. The codec set cannot change while the process runs — an installed extension needs a
/// restart to register — and each query walks the platform's MFT list, which is not free.
/// </para>
/// </summary>
public sealed class WindowsMediaCapability : IMediaCapability
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly Dictionary<(MediaStreamKind Kind, bool Encode), IReadOnlySet<string>> _cache = [];

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape a capability query.</param>
    public WindowsMediaCapability(Action<string>? log = null) => _log = log;

    /// <inheritdoc />
    public IReadOnlySet<string> Decodable(MediaStreamKind kind) => Query(kind, encode: false);

    /// <inheritdoc />
    public IReadOnlySet<string> Encodable(MediaStreamKind kind) => Query(kind, encode: true);

    private IReadOnlySet<string> Query(MediaStreamKind kind, bool encode)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((kind, encode), out var cached)) return cached;
        }

        var found = AppCallback.RunOrDefault(
            () => Enumerate(kind, encode),
            // ⚠ EMPTY on failure, never a guess. "I know of none" is the honest answer and the safe
            // direction for a planner reading it — it converts rather than assuming playback will work.
            (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ex => Log(() => $"[Shenora.Windows] codec query failed ({ex.GetType().Name}: {ex.Message})."));

        lock (_gate) _cache[(kind, encode)] = found;
        return found;
    }

    private IReadOnlySet<string> Enumerate(MediaStreamKind kind, bool encode)
    {
        var codecKind = kind switch
        {
            MediaStreamKind.Video => CodecKind.Video,
            MediaStreamKind.Audio => CodecKind.Audio,
            // Subtitles and anything a later version adds: the platform has no codec concept for them, and
            // an empty set says "I know of none" rather than inventing an answer.
            _ => (CodecKind?)null,
        };
        if (codecKind is not { } resolved) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var category = encode ? CodecCategory.Encoder : CodecCategory.Decoder;
        // GetAwaiter().GetResult() rather than .Wait(): this is called from a planner that is synchronous by
        // contract, the query is a local MFT enumeration with no UI thread involved, and unwrapping the
        // AggregateException a Wait() would produce buys nothing here.
        // An INSTANCE method, not static — checked against the projection after assuming otherwise, the same
        // way MPNowPlayingInfoCenter.playbackState turned out not to exist on the iOS side.
        var codecs = new CodecQuery().FindAllAsync(resolved, category, string.Empty)
            .AsTask().GetAwaiter().GetResult();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 🔴 The UNRECOGNISED ones are logged too, and that is not tidiness. An unknown subtype is dropped,
        // which reads downstream as "the device does not support it" — indistinguishable from the machine
        // genuinely lacking the codec (D63's failure mode, in a translation table). Without this line a
        // missing row here and an absent decoder produce identical evidence, so a device measurement can
        // never be attributed. This one was written after `MFVideoFormat_HEVC` turned out to be missing
        // from the table while `H265` was present.
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var codec in codecs)
        {
            foreach (var subtype in codec.Subtypes)
            {
                if (Normalise(subtype) is { } name) names.Add(name);
                else unknown.Add(subtype);
            }
        }
        Log(() => $"[Shenora.Windows] {(encode ? "encodable" : "decodable")} {kind}: {string.Join(", ", names)}");
        if (unknown.Count > 0)
        {
            Log(() => $"[Shenora.Windows] {kind} subtypes this table does not name (reported as UNSUPPORTED): "
                + string.Join(", ", unknown));
        }
        return names;
    }

    /// <summary>
    /// Map a Media Foundation subtype GUID onto the kit's codec vocabulary — the same short names the
    /// Matroska probe and <see cref="MediaPlaybackPolicy"/> use.
    /// <para>
    /// ⚠ <b>A translation table is unavoidable and it is deliberately SMALL.</b> Every platform names codecs
    /// differently (`MediaCodec` says <c>audio/mp4a-latm</c>, Matroska says <c>A_AAC</c>, Media Foundation
    /// says a GUID), so something must translate — and the kit already decided the vocabulary. What this
    /// must not become is a codec DATABASE: an unknown subtype is dropped, which reads as "the device does
    /// not support it", and the planner then converts. Being conservative here costs a needless conversion;
    /// being generous would claim support the element does not have.
    /// </para>
    /// </summary>
    private static string? Normalise(string subtype) => subtype.Trim('{', '}').ToLowerInvariant() switch
    {
        // The MF audio subtype GUIDs, whose first four bytes are the wave format tag.
        "00001610-0000-0010-8000-00aa00389b71" => "aac",
        "0000706d-0000-0010-8000-00aa00389b71" => "mp3",
        "00000055-0000-0010-8000-00aa00389b71" => "mp3",
        "00002000-0000-0010-8000-00aa00389b71" => "ac3",
        "00000001-0000-0010-8000-00aa00389b71" => "pcm",
        "0000f1ac-0000-0010-8000-00aa00389b71" => "flac",
        "6c61616c-0000-0010-8000-00aa00389b71" => "alac",
        "704f7075-0000-0010-8000-00aa00389b71" => "opus",
        "8d2fd10b-5841-4a6b-8905-588fec1aded9" => "vorbis",
        // Video subtypes are FOURCCs in the same GUID shape — Data1 is the FOURCC read little-endian, so
        // 'H264' (48 32 36 34) prints as 34363248.
        "34363248-0000-0010-8000-00aa00389b71" => "h264",
        // ⚠ HEVC has THREE registered subtypes and listing only one is how a machine that decodes it gets
        // reported as not doing so. MFVideoFormat_HEVC is FOURCC 'HEVC' and is what the HEVC Video
        // Extension advertises; 'H265' and 'HEVS' (elementary stream) are the other two. Only 'H265' was
        // here, which made "no HEVC on this box" unattributable — see the unknown-subtype log above.
        "43564548-0000-0010-8000-00aa00389b71" => "hevc",   // 'HEVC'
        "35363248-0000-0010-8000-00aa00389b71" => "hevc",   // 'H265'
        "53564548-0000-0010-8000-00aa00389b71" => "hevc",   // 'HEVS' — elementary stream
        "31435657-0000-0010-8000-00aa00389b71" => "vc1",
        "30385056-0000-0010-8000-00aa00389b71" => "vp8",
        "30395056-0000-0010-8000-00aa00389b71" => "vp9",
        "31305641-0000-0010-8000-00aa00389b71" => "av1",
        _ => null,
    };

    private void Log(Func<string> message) => AppCallback.Log(_log, message);
}
#endif
