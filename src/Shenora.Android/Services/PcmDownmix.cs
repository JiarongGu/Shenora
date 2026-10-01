using System.Buffers.Binary;

namespace Shenora.Android;

/// <summary>
/// Interleaved 16-bit PCM folded to stereo, for a decoder that hands back more channels than the AAC encoder was
/// configured with. Fed as they were, six channels read as stereo: three times as long, and garbled.
/// <para>
/// Android's channel order (WAVE order): 3 = FL FR FC · 4 = FL FR BL BR · 5 = FL FR FC BL BR · 6 = FL FR FC LFE BL BR
/// · 8 = FL FR FC LFE BL BR SL SR. Centre and surrounds at −3 dB, LFE dropped (ITU-R BS.775), each side normalised so
/// a full-scale source does not clip. Any other count keeps its first two channels. Portable on purpose: the tests
/// link this file.
/// </para>
/// </summary>
internal static class PcmDownmix
{
    private const float Minus3dB = 0.70710677f;

    public static byte[] ToStereo(ReadOnlySpan<byte> pcm, int channels)
    {
        if (channels <= 2) return pcm.ToArray();
        var (centre, back, side) = channels switch
        {
            3 => (2, -1, -1),
            4 => (-1, 2, -1),
            5 => (2, 3, -1),
            6 => (2, 4, -1),
            8 => (2, 4, 6),
            _ => (-1, -1, -1),
        };
        var gain = 1f + (centre >= 0 ? Minus3dB : 0f) + (back >= 0 ? Minus3dB : 0f) + (side >= 0 ? Minus3dB : 0f);
        // The right-hand member of each pair; -1 stays "none" rather than becoming channel 0.
        var backRight = back < 0 ? -1 : back + 1;
        var sideRight = side < 0 ? -1 : side + 1;

        var frames = pcm.Length / (2 * channels);
        var output = new byte[frames * 4];
        for (var frame = 0; frame < frames; frame++)
        {
            var at = frame * channels;
            var middle = Sample(pcm, at, centre) * Minus3dB;
            var left = Sample(pcm, at, 0) + middle + Sample(pcm, at, back) * Minus3dB + Sample(pcm, at, side) * Minus3dB;
            var right = Sample(pcm, at, 1) + middle + Sample(pcm, at, backRight) * Minus3dB + Sample(pcm, at, sideRight) * Minus3dB;
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4), Clamp(left / gain));
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(frame * 4 + 2), Clamp(right / gain));
        }
        return output;
    }

    /// <summary>Channel <paramref name="channel"/> of the frame starting at sample <paramref name="at"/>; 0 for none.</summary>
    private static float Sample(ReadOnlySpan<byte> pcm, int at, int channel) =>
        channel < 0 ? 0f : BinaryPrimitives.ReadInt16LittleEndian(pcm[((at + channel) * 2)..]);

    private static short Clamp(float value) => (short)Math.Clamp(MathF.Round(value), short.MinValue, short.MaxValue);
}
