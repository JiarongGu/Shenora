using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;

#if !WINDOWS10_0_17763_0_OR_GREATER

namespace Shenora.Windows;

/// <summary>
/// The plain-<c>net10.0-windows</c> half of <see cref="WindowsMediaCapability"/>. See
/// <see cref="WindowsPlaybackSession"/> for why these halves exist.
/// <para>
/// ⚠ <b>It answers EMPTY rather than throwing</b> — a capability query is a QUESTION, and the contract
/// already means "I cannot tell" by the empty set. So the planner is told this machine decodes nothing it
/// knows about and converts where it might not have needed to: slower, never wrong. Retarget to
/// <c>net10.0-windows10.0.17763.0</c> and it asks the platform instead.
/// </para>
/// </summary>
public sealed class WindowsMediaCapability : IMediaCapability
{
    private static readonly HashSet<MediaStreamCodec> None = new();

    /// <param name="log">Accepted and unused here, so the two variants construct identically.</param>
    public WindowsMediaCapability(ILogger? log = null) => _ = log;

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => None;

    /// <inheritdoc />
    public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => None;
}
#endif
