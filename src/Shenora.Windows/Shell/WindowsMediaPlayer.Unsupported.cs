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
/// ⚠ The public shape here MUST match the versioned variant exactly — same type name, same package,
/// differing only by TFM — so a consumer that retargets finds the same members. That is what
/// <c>MetadataSurfaceTests</c>' plain-TFM entry is for: two hand-written shapes need a gate.
/// </para>
/// </summary>
public sealed class WindowsMediaPlayer : IMediaPlayer, IDisposable
{
    /// <param name="log">Accepted and unused here, so the two variants construct identically.</param>
    public WindowsMediaPlayer(Action<string>? log = null)
    {
        _ = log;
        throw ShellCapability.NotSupported(
            "The host-owned media player", "net10.0-windows",
            "It needs the WinRT projections, which exist only when the target framework names a Windows SDK "
            + "version. Retarget to net10.0-windows10.0.17763.0 or newer — one line, and nothing else "
            + "changes: that floor is Windows 10 1809 and the rest of Shenora.Windows is identical.");
    }

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public MediaPlayerStatus Status => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public double Rate { get; set; } = 1.0;

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public event Action<MediaPlayerStatus>? StateChanged;

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public Task OpenAsync(MediaSource source, CancellationToken cancellationToken = default) => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public Task PlayAsync(CancellationToken cancellationToken = default) => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public Task PauseAsync(CancellationToken cancellationToken = default) => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public Task CloseAsync(CancellationToken cancellationToken = default) => throw Unreachable();

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>
    /// Keeps <see cref="StateChanged"/> from being an event nothing ever raises (CS0067, an error here)
    /// while stating the truth: on this TFM nothing can raise it.
    /// </summary>
    private InvalidOperationException Unreachable()
    {
        StateChanged?.Invoke(new MediaPlayerStatus { State = MediaPlayerState.Empty });
        return new InvalidOperationException(
            "WindowsMediaPlayer cannot be constructed on plain net10.0-windows, so this is unreachable.");
    }
}
#endif
