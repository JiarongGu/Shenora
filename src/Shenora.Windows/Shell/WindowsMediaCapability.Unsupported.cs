using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Modules.Media;

#if !WINDOWS10_0_17763_0_OR_GREATER

namespace Shenora.Windows;

/// <summary>
/// The plain-<c>net10.0-windows</c> half of <see cref="WindowsMediaCapability"/>.
/// <para>
/// <b>⚠ It answers EMPTY rather than throwing, and that is the opposite of what
/// <see cref="WindowsPlaybackSession"/> does on this TFM — deliberately.</b> The two contracts fail
/// differently because they mean different things:
/// </para>
/// <list type="bullet">
///   <item><b>A transport surface is a FEATURE.</b> An app asking for Now Playing on a TFM that cannot
///   provide it has made a mistake, and a named refusal at construction is the kindest answer.</item>
///   <item><b>A capability query is a QUESTION</b>, and the contract already has an answer for "I cannot
///   tell": the empty set. Every caller handles it, because a device that genuinely decodes nothing is
///   legitimate. Throwing here would make an app branch on the TFM to ask a question that is safe to ask
///   anywhere.</item>
/// </list>
/// <para>
/// The consequence is honest and small: on plain <c>net10.0-windows</c> the planner is told the machine
/// decodes nothing it knows about, so it converts where it might not have needed to. Slower, never wrong.
/// Retarget to <c>net10.0-windows10.0.17763.0</c> and it asks the platform instead.
/// </para>
/// <para>
/// ⚠ The public shape MUST match the versioned variant exactly — same type, same package, different TFM —
/// which is why the plain TFM has its own entry in <c>MetadataSurfaceTests</c>.
/// </para>
/// </summary>
public sealed class WindowsMediaCapability : IMediaCapability
{
    private static readonly HashSet<MediaStreamCodec> None = new();

    /// <param name="log">Accepted and unused here, so the two variants construct identically.</param>
    public WindowsMediaCapability(ILogger? log = null) => _ = log;

    /// <inheritdoc />
    /// <remarks>Always empty on this TFM — <c>CodecQuery</c> is WinRT. See the type remarks.</remarks>
    public IReadOnlySet<MediaStreamCodec> Decodable(MediaStreamKind kind) => None;

    /// <inheritdoc />
    /// <remarks>Always empty on this TFM — <c>CodecQuery</c> is WinRT. See the type remarks.</remarks>
    public IReadOnlySet<MediaStreamCodec> Encodable(MediaStreamKind kind) => None;
}
#endif
