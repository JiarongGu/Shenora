using Microsoft.Extensions.Logging;
using Shenora.Modules.Media;
using Shenora.Core.Shell;

#if !WINDOWS10_0_17763_0_OR_GREATER
using Shenora;

namespace Shenora.Windows;

/// <summary>
/// The plain-<c>net10.0-windows</c> half of <see cref="WindowsMediaPlayer"/>: a NAMED refusal, exactly as
/// <see cref="WindowsPlaybackSession"/> does it and for the same reason — <c>Windows.Media.Playback</c> is
/// WinRT, and the WinRT projections only exist when the target framework names a Windows SDK version.
/// <para>
/// <b>It refuses at CONSTRUCTION, not per call.</b> The registration in <c>UseWindows</c> is lazy, so the
/// throw lands the first time an app resolves the player — at the point it asked for the capability, naming
/// the platform and the one-line remedy. A per-method refusal would let an app open a source and call Play
/// into nothing first, which is the silent degradation this kit treats as the worse failure.
/// </para>
/// <para>
/// 🔴 <b>It derives from <see cref="MediaPlayerBase"/> so the two TFM variants cannot DRIFT.</b>
/// <see cref="WindowsPlaybackSession"/>'s pair are two hand-written shapes kept in step by a test, because
/// that contract has no base class; here the public surface is inherited on both sides, so "same type name,
/// same members, different TFM" is structural rather than a promise. The overrides below are all
/// <c>protected</c> — they are not surface, and nothing can reach them anyway.
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

    /// <summary>States the truth: on this TFM the constructor refuses, so nothing here can run.</summary>
    private static InvalidOperationException Unreachable() => new(
        "WindowsMediaPlayer cannot be constructed on plain net10.0-windows, so this is unreachable.");
}
#endif
