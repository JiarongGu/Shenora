using Android.Media;
using Microsoft.Extensions.Logging;
using Shenora;

namespace Shenora.Android;

/// <summary>What both Android converters need to ask the platform, in one place.</summary>
internal static class AndroidMediaCodecs
{
    /// <summary>
    /// Is a codec for this MIME actually instantiable here? <c>RegularCodecs</c>, so a codec the platform
    /// will not hand an ordinary app does not count.
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
            // "No" is the safe direction: the caller declines rather than starting what cannot finish.
        }
        return false;
    }

    /// <summary>
    /// Guarded log — an app-supplied sink that throws must not become this converter's failure. The rule
    /// itself lives in <see cref="AppCallback.Log"/>.
    /// </summary>
    internal static void Report(ILogger? log, string message) => AppCallback.Log(log, () => message);
}
