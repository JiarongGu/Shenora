#if !WINDOWS10_0_17763_0_OR_GREATER
using Shenora.Core;

namespace Shenora.Windows;

/// <summary>
/// The plain-<c>net10.0-windows</c> half of <see cref="WindowsPlaybackSession"/>: a NAMED refusal.
/// <para>
/// <b>Why this file exists at all.</b> Windows' only system-wide media transport is
/// <c>SystemMediaTransportControls</c>, which is WinRT — and the WinRT projections only exist when the target
/// framework names a Windows SDK version. With a bare <c>net10.0-windows</c>, <c>Windows.Media</c> is not a
/// namespace (measured: <c>CS0234</c>). So rather than force a TFM on every consumer for a capability most do
/// not use, the package multi-targets and this variant refuses with the exact fix in the message.
/// </para>
/// <para>
/// <b>It refuses at CONSTRUCTION, not per call.</b> The registration in <c>UseWinForms</c> is lazy, so the
/// throw lands the first time an app resolves <see cref="IPlaybackSession"/> — at the point it asked for the
/// capability, naming the platform and the one-line remedy. A per-method refusal would let an app publish
/// metadata and report progress into nothing for a while first, which is the silent-degradation this kit
/// treats as the worse failure (<see cref="ShellCapability"/>).
/// </para>
/// <para>
/// ⚠ The public shape here MUST match the versioned variant exactly — it is the same type name in the same
/// package, differing only by TFM, and a consumer that retargets must find the same members. That is why the
/// plain TFM has its own entry in <c>MetadataSurfaceTests</c>: two hand-written shapes need a gate, and the
/// runtime API baseline only ever sees whichever variant the test project itself references.
/// </para>
/// </summary>
public sealed class WindowsPlaybackSession : IPlaybackSession, IDisposable
{
    /// <param name="log">Accepted and unused here, so the two variants construct identically.</param>
    public WindowsPlaybackSession(Action<string>? log = null)
    {
        _ = log;
        throw ShellCapability.NotSupported(
            "The system media transport (Now Playing)", "net10.0-windows",
            "It needs the WinRT projections, which exist only when the target framework names a Windows SDK "
            + "version. Retarget to net10.0-windows10.0.17763.0 or newer — one line, and nothing else "
            + "changes: that floor is Windows 10 1809 and the rest of Shenora.Windows is identical.");
    }

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public PlaybackCommands Supported { get; set; }

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public TimeSpan SkipInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public event Action<PlaybackCommandRequest>? CommandReceived;

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public void Publish(PlaybackInfo info) => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public void Report(PlaybackProgress progress) => throw Unreachable();

    /// <inheritdoc />
    /// <remarks>Unreachable — the constructor refuses first.</remarks>
    public void Clear() => throw Unreachable();

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>
    /// Keeps <see cref="CommandReceived"/> from being an event nothing ever raises (CS0067, an error here)
    /// while stating the truth: on this TFM nothing can raise it.
    /// </summary>
    private InvalidOperationException Unreachable()
    {
        CommandReceived?.Invoke(new PlaybackCommandRequest { Command = PlaybackCommand.Stop });
        return new InvalidOperationException(
            "WindowsPlaybackSession cannot be constructed on plain net10.0-windows, so this is unreachable.");
    }
}
#endif
