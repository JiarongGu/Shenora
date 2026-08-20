using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;

// CodecQuery is WinRT (Windows 10 1809): this is the versioned half of the multi-target split, and
// WindowsMediaCapability.Unsupported.cs is the plain one. See WindowsPlaybackSession.cs for the split, and
// for why every WinRT namespace below needs `global::`.
#if WINDOWS10_0_17763_0_OR_GREATER
using Shenora;
using CodecQuery = global::Windows.Media.Core.CodecQuery;
using CodecKind = global::Windows.Media.Core.CodecKind;
using CodecCategory = global::Windows.Media.Core.CodecCategory;

namespace Shenora.Windows;

/// <summary>
/// The desktop shell's <see cref="IMediaCapability"/> — what THIS Windows machine can decode and encode,
/// asked of the platform through <c>CodecQuery</c> rather than assumed (D42). Results are cached; the codec
/// set cannot change while the process runs.
/// <para>
/// ⚠ <b>It answers about the MACHINE, not about the webview.</b> WebView2 accepts a narrower set than Media
/// Foundation decodes — a machine with the HEVC extension installed decodes HEVC while the element still
/// refuses it. That gap is what the planner converts: this is the DEVICE half,
/// <c>MediaPlaybackPolicy</c> is the webview half (D59).
/// </para>
/// </summary>
public sealed class WindowsMediaCapability : IMediaCapability
{
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private readonly Dictionary<(MediaStreamKind Kind, bool Encode), IReadOnlySet<MediaStreamCodec>> _cache = [];

    /// <param name="log">Diagnostics. Guarded — a throwing sink must not escape a capability query.</param>
    public WindowsMediaCapability(ILogger? log = null) => _log = log;

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => Query(kind, encode: false);

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => Query(kind, encode: true);

    private IReadOnlySet<MediaStreamCodec> Query(MediaStreamKind kind, bool encode)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue((kind, encode), out var cached)) return cached;
        }

        var found = AppCallback.RunOrDefault(
            () => Enumerate(kind, encode),
            // ⚠ EMPTY on failure, never a guess. "I know of none" is the honest answer and the safe
            // direction for a planner reading it — it converts rather than assuming playback will work.
            (IReadOnlySet<MediaStreamCodec>)new HashSet<MediaStreamCodec>(),
            ex => Log(() => "[Shenora.Windows] codec query failed.", ex));

        lock (_gate) _cache[(kind, encode)] = found;
        return found;
    }

    private IReadOnlySet<MediaStreamCodec> Enumerate(MediaStreamKind kind, bool encode)
    {
        var codecKind = kind switch
        {
            MediaStreamKind.Video => CodecKind.Video,
            MediaStreamKind.Audio => CodecKind.Audio,
            // Subtitles and anything a later version adds: the platform has no codec concept for them.
            _ => (CodecKind?)null,
        };
        if (codecKind is not { } resolved) return new HashSet<MediaStreamCodec>();

        var category = encode ? CodecCategory.Encoder : CodecCategory.Decoder;
        // Blocking is safe: the planner is synchronous by contract and this is a local MFT enumeration with
        // no UI thread involved.
        var codecs = new CodecQuery().FindAllAsync(resolved, category, string.Empty)
            .AsTask().GetAwaiter().GetResult();

        var names = new HashSet<MediaStreamCodec>();
        // 🔴 The UNRECOGNISED ones are logged too. An unknown subtype is dropped, which reads downstream as
        // "the device does not support it" — indistinguishable from the machine genuinely lacking the codec,
        // so without this line a missing row here and an absent decoder produce identical evidence.
        var unknown = new HashSet<MediaStreamCodec>();
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
    /// Matroska probe and <see cref="MediaPlaybackPolicy"/> use. ⚠ Deliberately small, never a codec
    /// DATABASE: an unknown subtype is dropped and the planner converts, so being conservative costs a
    /// needless conversion while being generous would claim support the element does not have.
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
        // ⚠ HEVC has THREE registered subtypes, and listing only one is how a machine that decodes it gets
        // reported as not doing so. 'HEVC' (MFVideoFormat_HEVC) is what the HEVC Video Extension advertises.
        "43564548-0000-0010-8000-00aa00389b71" => "hevc",   // 'HEVC'
        "35363248-0000-0010-8000-00aa00389b71" => "hevc",   // 'H265'
        "53564548-0000-0010-8000-00aa00389b71" => "hevc",   // 'HEVS' — elementary stream
        "31435657-0000-0010-8000-00aa00389b71" => "vc1",
        "30385056-0000-0010-8000-00aa00389b71" => "vp8",
        "30395056-0000-0010-8000-00aa00389b71" => "vp9",
        "31305641-0000-0010-8000-00aa00389b71" => "av1",
        _ => null,
    };

    private void Log(Func<string> message, Exception? failure = null) => AppCallback.Log(_log, message, exception: failure);
}
#endif
