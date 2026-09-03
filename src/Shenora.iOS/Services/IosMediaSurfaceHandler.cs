using AVFoundation;
using CoreAnimation;
using Microsoft.Maui.Handlers;
using Shenora.Mobile;
using UIKit;

namespace Shenora.iOS;

/// <summary>
/// The iOS half of <see cref="MediaSurfaceView"/>: a <c>UIView</c> backed by an <c>AVPlayerLayer</c>, which
/// is handed to the shell's player.
/// <para>
/// ⚠ <b>An <c>AVPlayerLayer</c>, not <c>AVPlayerViewController</c>.</b> The view controller brings Apple's
/// transport UI and its own full-screen behaviour; the page draws every control itself. A layer is the
/// pixels and nothing else.
/// </para>
/// </summary>
public sealed class IosMediaSurfaceHandler : ViewHandler<MediaSurfaceView, IosMediaSurfaceView>
{
    /// <summary>The property mapper. A bare view: it has no properties of its own to map.</summary>
    public static readonly IPropertyMapper<MediaSurfaceView, IosMediaSurfaceHandler> Mapper =
        new PropertyMapper<MediaSurfaceView, IosMediaSurfaceHandler>(ViewMapper);

    /// <summary>Creates the handler MAUI resolves for a <see cref="MediaSurfaceView"/>.</summary>
    public IosMediaSurfaceHandler() : base(Mapper) { }

    /// <inheritdoc />
    protected override IosMediaSurfaceView CreatePlatformView() => new();

    /// <inheritdoc />
    protected override void ConnectHandler(IosMediaSurfaceView platformView)
    {
        base.ConnectHandler(platformView);
        VirtualView?.AttachToPlayer(platformView.PlayerLayer);
    }

    /// <inheritdoc />
    protected override void DisconnectHandler(IosMediaSurfaceView platformView)
    {
        // ⚠ Detach first: the layer goes away with the view, and a player still pointing at it would be
        // rendering into something the view no longer owns.
        VirtualView?.AttachToPlayer(null);
        base.DisconnectHandler(platformView);
    }
}

/// <summary>
/// A view holding an <c>AVPlayerLayer</c> — the handle the player draws into.
/// <para>
/// ⚠ <b>The layer is created EMPTY and handed to the player, not built from one.</b> The shell's player is
/// a DI singleton that outlives every view, so the view cannot own the relationship the other way round.
/// </para>
/// </summary>
public sealed class IosMediaSurfaceView : UIView
{
    private readonly AVPlayerLayer _playerLayer;

    /// <summary>Creates the view and its player layer.</summary>
    public IosMediaSurfaceView()
    {
        _playerLayer = new AVPlayerLayer
        {
            // Letterbox rather than crop: the page sized the hole, and a film whose aspect differs from it
            // must not have its edges cut off to fill it — the `object-fit: contain` a web element gave free.
            VideoGravity = AVLayerVideoGravity.ResizeAspect,
        };
        _playerLayer.Frame = Bounds;
        Layer.AddSublayer(_playerLayer);
    }

    /// <summary>The layer the player is given.</summary>
    internal AVPlayerLayer PlayerLayer => _playerLayer;

    /// <summary>
    /// Keep the layer on the view's own bounds. <b>Two separate traps, and both bite per scroll frame.</b>
    /// </summary>
    /// <remarks>
    /// 🔴 <b>A <c>CALayer</c> does not take part in autolayout</b>, so a layer sized once at construction
    /// keeps that size forever while the page moves this rectangle continuously.
    /// <para>
    /// 🔴 <b>And implicit animations are ON for a layer the view did not create for itself</b>, so a bare
    /// frame assignment ANIMATES over ~0.25 s — the picture then lags visibly behind the hole it is meant
    /// to fill, which reads as stutter in the player rather than as a layout fault.
    /// </para>
    /// </remarks>
    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _playerLayer.Frame = Bounds;
        CATransaction.Commit();
    }
}
