using Android.Media;
using Microsoft.Extensions.Logging;
using Shenora;

namespace Shenora.Android;

/// <summary>
/// What both Android converters need to ask the platform, in one place.
///
/// <para>
/// The audio and picture converters each carried a byte-identical private copy of these two until
/// 2026-08-14. Neither is converter-specific: <see cref="HasCodec"/> asks <c>MediaCodecList</c> a
/// question with no reference to a stream kind, and <see cref="Report"/> is the kit's
/// already-owned guarded-log rule (<see cref="AppCallback.Log"/>) spelled out a third time.
/// </para>
/// </summary>
internal static class AndroidMediaCodecs
{
    /// <summary>
    /// Is a codec for this MIME actually instantiable here? <c>RegularCodecs</c>, so a hidden one does not
    /// count — a codec the platform will not hand an ordinary app is not a codec this kit can use.
    /// </summary>
    /// <param name="mime">The Android MIME type, e.g. <c>audio/ac3</c>.</param>
    /// <param name="encoder">True to look for an encoder, false for a decoder.</param>
    internal static bool HasCodec(string mime, bool encoder)
    {
        try
        {
            var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
            foreach (var info in list.GetCodecInfos() ?? [])
            {
                if (info.IsEncoder != encoder) continue;
                foreach (var type in info.GetSupportedTypes() ?? [])
                {
                    if (string.Equals(type, mime, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception)
        {
            // Answering "no" is the safe direction: the caller declines the conversion rather than
            // starting one the device cannot finish.
        }
        return false;
    }

    /// <summary>
    /// A log sink is app-supplied, so a throwing one must not become this converter's failure. Delegates
    /// to <see cref="AppCallback.Log"/> — the ONE owner of that rule — rather than restating it.
    /// </summary>
    internal static void Report(ILogger? log, string message) => AppCallback.Log(log, () => message);
}
