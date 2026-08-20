using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Core.Shell;

#if !WINDOWS10_0_17763_0_OR_GREATER
using Shenora;

namespace Shenora.Windows;

/// <summary>
/// The plain-<c>net10.0-windows</c> half of <see cref="WindowsMediaPlayer"/>: a NAMED refusal at
/// construction. See <see cref="WindowsPlaybackSession"/> for why these halves exist and how they refuse.
/// <para>
/// It derives from <see cref="MediaPlayerBase"/>, so "same type name, same members, different TFM" is
/// structural here rather than two hand-written shapes kept in step by a test.
/// </para>
/// </summary>
public sealed class WindowsMediaPlayer : MediaPlayerBase
{
    /// <param name="log">Accepted and unused here, so the two variants construct identically.</param>
    public WindowsMediaPlayer(ILogger? log = null)
        : base(log)
        => throw ShellCapability.NotSupported(
            "The host-owned media player", "net10.0-windows",
            "It needs the WinRT projections, which exist only when the target framework names a Windows SDK "
            + "version. Retarget to net10.0-windows10.0.17763.0 or newer — one line, and nothing else "
            + "changes: that floor is Windows 10 1809 and the rest of Shenora.Windows is identical.");

    /// <inheritdoc />
    protected override TimeSpan PositionCore => throw Unreachable();

    /// <inheritdoc />
    protected override TimeSpan? DurationCore => throw Unreachable();

    /// <inheritdoc />
    protected override void OpenCore(MediaSource source, Uri uri) => throw Unreachable();

    /// <inheritdoc />
    protected override void ApplyStartAt(TimeSpan position) => throw Unreachable();

    /// <inheritdoc />
    protected override void PlayCore(double rate) => throw Unreachable();

    /// <inheritdoc />
    protected override void PauseCore() => throw Unreachable();

    /// <inheritdoc />
    protected override Task SeekCore(TimeSpan position) => throw Unreachable();

    /// <inheritdoc />
    protected override void ApplyRateCore(double rate) => throw Unreachable();

    /// <inheritdoc />
    protected override void TeardownCore() => throw Unreachable();

    private static InvalidOperationException Unreachable() => new(
        "WindowsMediaPlayer cannot be constructed on plain net10.0-windows, so this is unreachable.");
}
#endif
