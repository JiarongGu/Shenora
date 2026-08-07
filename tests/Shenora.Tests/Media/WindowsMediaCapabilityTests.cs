using Shenora.Windows;
using Shenora.Modules.Media;

namespace Shenora.Tests.Media;

/// <summary>
/// <see cref="WindowsMediaCapability"/> — the desktop half of the question D42 says the kit must ASK
/// rather than answer. Windows was the one shell that registered no capability at all.
/// <para>
/// ⚠ <b>These deliberately assert SHAPE, not contents.</b> The answer is per-machine — this box reports
/// <c>av1, h264, vc1, vp8, vp9</c> for video and no HEVC (no extension installed) — so a test asserting a
/// particular codec would be asserting the CI runner's Windows install, and would fail for the wrong
/// reason on someone else's. The measurement belongs in a commit message; the invariants belong here.
/// </para>
/// </summary>
public class WindowsMediaCapabilityTests
{
    /// <summary>
    /// A kind the platform has no codec concept for answers EMPTY rather than throwing — "I know of none"
    /// is honest, and it is the safe direction for a planner (it converts rather than assuming).
    /// </summary>
    [Fact]
    public void An_unknown_kind_is_answered_with_an_empty_set()
    {
        var capability = new WindowsMediaCapability();

        Assert.Empty(capability.Decodable(MediaStreamKind.Subtitle));
        Assert.Empty(capability.Encodable(MediaStreamKind.Subtitle));
    }

    /// <summary>
    /// Cached: the codec set cannot change while the process runs (an installed extension needs a restart)
    /// and each query walks the platform's MFT list, which is not free.
    /// </summary>
    [Fact]
    public void The_answer_is_cached_rather_than_re_queried()
    {
        var capability = new WindowsMediaCapability();

        Assert.Same(capability.Decodable(MediaStreamKind.Audio), capability.Decodable(MediaStreamKind.Audio));
    }

    /// <summary>
    /// Decode and encode are separate questions and must not share a cache entry — a machine that decodes
    /// AC-3 does not necessarily encode it, and conflating them would claim an encoder that is not there.
    /// </summary>
    [Fact]
    public void Decode_and_encode_are_answered_separately()
    {
        var capability = new WindowsMediaCapability();

        var decode = capability.Decodable(MediaStreamKind.Audio);
        var encode = capability.Encodable(MediaStreamKind.Audio);

        Assert.NotSame(decode, encode);
    }
}
