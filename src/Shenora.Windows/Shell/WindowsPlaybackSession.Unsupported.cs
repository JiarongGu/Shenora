using Microsoft.Extensions.Logging;
using Shenora.Modules.Platform;
using Shenora.Core.Shell;

#if !WINDOWS10_0_17763_0_OR_GREATER
using Shenora;

namespace Shenora.Windows;

/// <summary>
/// The plain-<c>net10.0-windows</c> half of <see cref="WindowsPlaybackSession"/>: a NAMED refusal.
/// <b>This type carries the reference statement for every <c>*.Unsupported.cs</c> half in this package.</b>
/// <para>
/// <b>Why they exist.</b> The capabilities they stand in for are WinRT
/// (<c>SystemMediaTransportControls</c>, <c>MediaPlayer</c>, <c>CodecQuery</c>), and the WinRT projections
/// only exist when the target framework names a Windows SDK version — with a bare <c>net10.0-windows</c>,
/// <c>Windows.Media</c> is not a namespace (measured: <c>CS0234</c>). So the package multi-targets rather
/// than forcing a TFM on every consumer for a capability most do not use.
/// </para>
/// <para>
/// <b>A FEATURE refuses at CONSTRUCTION</b> (this type, <see cref="WindowsMediaPlayer"/>), with the fix in
/// the message: the registration in <c>UseWindows</c> is lazy, so the throw lands where the app asked for the
/// capability. A per-method refusal would let it publish into nothing first, which is the silent degradation
/// the kit treats as the worse failure (<see cref="ShellCapability"/>). <b>A QUESTION answers EMPTY</b>
/// (<see cref="WindowsMediaCapability"/>) — the contract already has an answer for "I cannot tell".
/// </para>
/// <para>
/// ⚠ The public shape MUST match the versioned variant exactly — same type name, same package, different
/// TFM — which is why each plain-TFM half has its own entry in <c>MetadataSurfaceTests</c>: the runtime API
/// baseline only ever sees whichever variant the test project references.
/// </para>
/// </summary>
public sealed class WindowsPlaybackSession : IPlaybackSession, IDisposable
{
    /// <param name="log">Accepted and unused here, so the two variants construct identically.</param>
    public WindowsPlaybackSession(ILogger? log = null)
    {
        _ = log;
        throw ShellCapability.NotSupported(
            "The system media transport (Now Playing)", "net10.0-windows",
            "It needs the WinRT projections, which exist only when the target framework names a Windows SDK "
            + "version. Retarget to net10.0-windows10.0.17763.0 or newer — one line, and nothing else "
            + "changes: that floor is Windows 10 1809 and the rest of Shenora.Windows is identical.");
    }

    // Every member below is unreachable — the constructor refuses first.

    /// <inheritdoc />
    public PlaybackCommands Supported { get; set; }

    /// <inheritdoc />
    public TimeSpan SkipInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    public event Action<PlaybackCommandRequest>? CommandReceived;

    /// <inheritdoc />
    public void Publish(PlaybackInfo info) => throw Unreachable();

    /// <inheritdoc />
    public void Report(PlaybackProgress progress) => throw Unreachable();

    /// <inheritdoc />
    public void Clear() => throw Unreachable();

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Raises <see cref="CommandReceived"/> only to keep it from being an event nothing ever
    /// raises (CS0067, an error here).</summary>
    private InvalidOperationException Unreachable()
    {
        CommandReceived?.Invoke(new PlaybackCommandRequest { Command = PlaybackCommand.Stop });
        return new InvalidOperationException(
            "WindowsPlaybackSession cannot be constructed on plain net10.0-windows, so this is unreachable.");
    }
}
#endif
