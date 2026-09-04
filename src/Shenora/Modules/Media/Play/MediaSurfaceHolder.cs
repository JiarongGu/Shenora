namespace Shenora.Modules.Media;

/// <summary>
/// Pairs the platform's picture handle with the player that draws on it, <b>whichever arrives second</b>.
///
/// <para>
/// 🔴 <b>ORDER DOES NOT MATTER, and that is the entire reason this type exists.</b> The two halves race:
/// the platform realizes its surface when the layout does, and an app sets the player when its page loads.
/// Neither can wait for the other, so both are remembered here and the pair is completed by whichever is
/// last. <b>Dropping a handle that arrived first is a black rectangle and nothing else</b> — the player
/// opens the file, reports a moving clock and decodes into nowhere, so every symptom points at the player.
/// </para>
///
/// <para>
/// ⚠ <b>The handle is the PLATFORM's own, typed as <see cref="object"/></b> because this package is
/// <c>net10.0</c> and may not name a platform type (D19/D20) — <c>Android.Views.ISurfaceHolder</c> on
/// Android, <c>AVFoundation.AVPlayerLayer</c> on iOS. This type never dereferences it; it only remembers
/// it and hands it to <see cref="MediaPlayerBase.AttachSurface"/>.
/// </para>
///
/// <para>
/// ⚠ <b>Not thread-safe, deliberately.</b> Both halves arrive on the UI thread on both shells, and a lock
/// here would only hide a caller that is already on the wrong one.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is public, and it is not for testability.</b> A shell view lives in a platform-only assembly,
/// so a helper two packages need has to be public — the rule <c>Shenora.csproj</c> states for
/// <c>WinFormsUiDispatcher</c>, because a <c>ProjectReference</c> does not grant internal access. It is
/// also the piece an app writing its OWN surface view needs, over the same
/// <see cref="MediaPlayerBase.AttachSurfaceCore"/> seam the kit ships no engine behind (D51/D42).
/// </para>
/// </remarks>
public sealed class MediaSurfaceHolder
{
    private object? _handle;
    private MediaPlayerBase? _player;

    /// <summary>
    /// The player that draws here — a shell's own, never the page-backed one (D58), which has no picture
    /// to give.
    /// <para>
    /// Setting it detaches the outgoing player first: two players holding one surface is a torn picture at
    /// best. Assigning the SAME player again does nothing at all, so a caller that re-runs its wiring on
    /// every layout pass does not tear down a live picture.
    /// </para>
    /// </summary>
    public MediaPlayerBase? Player
    {
        get => _player;
        set
        {
            if (ReferenceEquals(_player, value)) return;
            _player?.AttachSurface(null);
            _player = value;
            // The surface may already exist — see the remarks on the type. Without this the handle is
            // dropped for good.
            if (_handle is not null) value?.AttachSurface(_handle);
        }
    }

    /// <summary>The handle the platform last supplied, or <c>null</c> when it has none. Exposed for a
    /// shell that must answer "is there a surface yet?" without provoking one.</summary>
    public object? Handle => _handle;

    /// <summary>
    /// The platform's surface appeared, or went away with <c>null</c>. Called by a platform view's handler;
    /// an app never calls this.
    /// <para>
    /// 🔴 <b>Pass <c>null</c> BEFORE the surface is destroyed.</b> A player holding a dead surface draws
    /// into a released buffer, which on some Android devices is a native crash rather than a blank view.
    /// </para>
    /// </summary>
    /// <param name="handle">The platform handle, or <c>null</c> to detach.</param>
    public void SetHandle(object? handle)
    {
        _handle = handle;
        _player?.AttachSurface(handle);
    }
}
