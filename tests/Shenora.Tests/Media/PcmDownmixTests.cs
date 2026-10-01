using System.Buffers.Binary;
using Shenora.Android;

namespace Shenora.Tests.Media;

/// <summary>
/// The Android audio conversion's fold to stereo (linked from <c>Shenora.Android</c>). Without it a 5.1 decoder's
/// six channels reached an encoder configured for two, and played three times as long, garbled.
/// </summary>
public class PcmDownmixTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    private static short[] Samples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        return samples;
    }

    [Fact]
    public void Six_channels_become_two_with_one_stereo_frame_per_source_frame()
    {
        // Two 5.1 frames (FL FR FC LFE BL BR): the output is two stereo frames, not six "stereo" ones.
        var stereo = Samples(PcmDownmix.ToStereo(Pcm(1000, 0, 0, 30000, 0, 0, 0, 1000, 0, 30000, 0, 0), channels: 6));

        Assert.Equal(4, stereo.Length);
        var gain = 1f + 2 * 0.70710677f;
        Assert.Equal((short)MathF.Round(1000 / gain), stereo[0]);   // FL to the left only
        Assert.Equal(0, stereo[1]);
        Assert.Equal(0, stereo[2]);                                  // LFE is dropped
        Assert.Equal((short)MathF.Round(1000 / gain), stereo[3]);   // FR to the right only
    }

    [Fact]
    public void Centre_and_surrounds_reach_their_sides_and_a_full_scale_source_does_not_clip()
    {
        var max = short.MaxValue;
        var stereo = Samples(PcmDownmix.ToStereo(Pcm(max, max, max, 0, max, max), channels: 6));
        Assert.Equal(max, stereo[0]);
        Assert.Equal(max, stereo[1]);

        // A surround on one side stays on that side.
        var backLeft = Samples(PcmDownmix.ToStereo(Pcm(0, 0, 0, 0, 1000, 0), channels: 6));
        Assert.True(backLeft[0] > 0);
        Assert.Equal(0, backLeft[1]);
    }

    [Fact]
    public void Quad_has_no_centre_and_stereo_passes_through()
    {
        // 4 channels are FL FR BL BR: channel 2 is a BACK, not a centre, so it must not reach the right side.
        var quad = Samples(PcmDownmix.ToStereo(Pcm(0, 0, 1000, 0), channels: 4));
        Assert.True(quad[0] > 0);
        Assert.Equal(0, quad[1]);

        var two = Pcm(1, 2, 3, 4);
        Assert.Equal(two, PcmDownmix.ToStereo(two, channels: 2));
    }
}
