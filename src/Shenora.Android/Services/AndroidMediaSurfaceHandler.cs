using Android.Views;
using Microsoft.Maui.Handlers;
using Shenora.Mobile;

namespace Shenora.Android;

/// <summary>
/// The Android half of <see cref="MediaSurfaceView"/>: a bare <c>SurfaceView</c> whose buffer is handed to
/// the shell's player.
/// <para>
/// ⚠ <b>A <c>SurfaceView</c>, not a player control.</b> The platform's own player views bring a whole
/// transport UI — buttons, a scrubber, a hide timer — and the page draws every one of those itself. What a
/// player needs from this class is somewhere to put pixels, and nothing else.
/// </para>
/// <para>
/// 🔴 <b>The buffer is attached when the platform says it EXISTS and detached before it is destroyed.</b>
/// A player holding a released surface draws into a dead buffer, which on some devices is a native crash
/// rather than a blank view — which is why this listens to the holder instead of attaching at
/// <see cref="ConnectHandler"/>.
/// </para>
/// </summary>
public sealed class AndroidMediaSurfaceHandler : ViewHandler<MediaSurfaceView, SurfaceView>
{
    /// <summary>The property mapper. A bare view: it has no properties of its own to map.</summary>
    public static readonly IPropertyMapper<MediaSurfaceView, AndroidMediaSurfaceHandler> Mapper =
        new PropertyMapper<MediaSurfaceView, AndroidMediaSurfaceHandler>(ViewMapper);

    /// <summary>Creates the handler MAUI resolves for a <see cref="MediaSurfaceView"/>.</summary>
    public AndroidMediaSurfaceHandler() : base(Mapper) { }

    private HolderCallback? _callback;

    /// <inheritdoc />
    protected override SurfaceView CreatePlatformView() => new(Context);

    /// <inheritdoc />
    protected override void ConnectHandler(SurfaceView platformView)
    {
        base.ConnectHandler(platformView);

        _callback = new HolderCallback(this);
        platformView.Holder?.AddCallback(_callback);

        // A surface that already exists raises nothing, so ask once rather than waiting for a callback
        // that has been and gone.
        if (platformView.Holder is { Surface.IsValid: true } holder) VirtualView?.AttachToPlayer(holder);
    }

    /// <inheritdoc />
    protected override void DisconnectHandler(SurfaceView platformView)
    {
        // ⚠ Detach the PLAYER first: everything below releases the buffer it is drawing into.
        VirtualView?.AttachToPlayer(null);

        if (_callback is not null)
        {
            platformView.Holder?.RemoveCallback(_callback);
            _callback = null;
        }

        base.DisconnectHandler(platformView);
    }

    /// <summary>Bridges the holder's lifecycle to the player. Separate class because
    /// <c>ISurfaceHolderCallback</c> would otherwise put three platform methods on the handler's own
    /// surface.</summary>
    private sealed class HolderCallback(AndroidMediaSurfaceHandler owner) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceCreated(ISurfaceHolder holder) => owner.VirtualView?.AttachToPlayer(holder);

        /// <summary>A resize does not change WHICH buffer the player draws into, so nothing is re-attached
        /// — the platform scales the existing one.</summary>
        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) { }

        public void SurfaceDestroyed(ISurfaceHolder holder) => owner.VirtualView?.AttachToPlayer(null);
    }
}
