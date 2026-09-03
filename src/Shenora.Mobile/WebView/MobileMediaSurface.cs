using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Shenora.Modules.Media;

namespace Shenora.Mobile;

/// <summary>
/// The picture, as MAUI sees it — a bare <see cref="View"/> whose platform half is the operating system's
/// own video output. Put it in a layout BENEATH the webview; <see cref="MobileMediaSurface"/> moves it.
/// <para>
/// <b>Deliberately dumb.</b> No bindable properties, no events, no control template: the page already holds
/// every piece of player state, and a second copy here could only disagree with it.
/// </para>
/// <para>
/// ⚠ <b>Nothing is drawn until <see cref="Player"/> is set</b> — the handler hands the platform's picture
/// handle to that player and to nothing else.
/// </para>
/// </summary>
public sealed class MediaSurfaceView : View
{
    /// <summary>
    /// The player that draws here — the shell's own, not the page-backed one.
    /// <para>
    /// ⚠ Assign it BEFORE the view is realized where possible. The handler attaches on whichever comes
    /// second, so a late assignment still works, but only if it happens on the UI thread.
    /// </para>
    /// </summary>
    public MediaPlayerBase? Player { get; set; }

    /// <summary>Give <see cref="Player"/> the platform handle, or take it away with <c>null</c>. Called by
    /// the platform handler; an app never calls this.</summary>
    internal void AttachToPlayer(object? handle) => Player?.AttachSurface(handle);
}

/// <summary>
/// The mobile shell's <see cref="IMediaSurface"/>: it puts <see cref="MediaSurfaceView"/> where the page
/// says the picture goes.
/// <para>
/// 🔴 <b>THE WHOLE DESIGN RESTS ON THE WEBVIEW BEING SEE-THROUGH</b> — call
/// <see cref="MobileMediaSurfaceExtensions.UseShenoraMediaSurface"/> at startup, and give the page's own
/// <c>body</c> a transparent background where the hole belongs. <b>Two layers on Android and THREE on iOS
/// have to agree</b>; miss one and the picture is simply never visible, which looks exactly like a player
/// that never started.
/// </para>
/// <para>
/// ⚠ <b>Compose it as <c>Grid { Children = { surface, webView } }</c></b> — the surface FIRST, so it is
/// behind. On iOS also set the grid's <c>SafeAreaEdges</c> to <c>None</c>: wrapping a bare
/// <c>HybridWebView</c> in a layout makes iOS inset it, which silently changes what
/// <c>env(safe-area-inset-*)</c> reports to the page.
/// </para>
/// </summary>
public sealed class MobileMediaSurface : IMediaSurface
{
    private readonly ILogger? _log;

    private MediaSurfaceView? _surface;
    private VisualElement? _webView;
    private IDispatcher? _dispatcher;
    private bool _warned;

    /// <param name="log">Diagnostics. A visibility CHANGE is logged; a reposition is not.</param>
    public MobileMediaSurface(ILogger? log = null) => _log = log;

    /// <summary>
    /// Give the surface the views it moves. Call it when the page is built, and again on every page the
    /// platform builds — Android recreates the activity, and its page, on a configuration change.
    /// <para>
    /// 🔴 <b>Registration happens before any page exists</b>, which is why this is a separate step: the
    /// service is a DI singleton composed in <c>MauiProgram</c>, and the views it drives are the page's.
    /// </para>
    /// </summary>
    /// <param name="surface">The view the picture is drawn into. Put it in a layout BEFORE the webview.</param>
    /// <param name="webView">
    /// The webview it composites against. Needed only to flip the z-order for
    /// <see cref="MediaSurfaceRegion.OnTop"/> — both elements are set explicitly, because leaving one at
    /// its default makes the order depend on child order instead.
    /// </param>
    /// <param name="dispatcher">The UI dispatcher. Every property this touches is a layout property.</param>
    public void Attach(MediaSurfaceView surface, VisualElement webView, IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(webView);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _surface = surface;
        _webView = webView;
        _dispatcher = dispatcher;
        _warned = false;
    }

    /// <summary>
    /// Forget the page's views, on unload.
    /// <para>
    /// ⚠ <b>Only if they are still THIS page's.</b> On Android the outgoing page unloads AFTER the
    /// incoming one is attached, so an unconditional detach tears down the live page's surface — the same
    /// ordering that governs the IPC bridge's own release.
    /// </para>
    /// </summary>
    /// <param name="surface">The view this page attached.</param>
    public void Detach(MediaSurfaceView surface)
    {
        if (!ReferenceEquals(_surface, surface)) return;
        _surface = null;
        _webView = null;
        _dispatcher = null;
    }

    /// <inheritdoc />
    public void Show(MediaSurfaceRegion region) => OnUi((surface, webView) =>
    {
        // 🔴 ZIndex, never a Children reorder: re-inserting a view disconnects and rebuilds its handler,
        // which for a player means dropping the decoder and the surface in the middle of playback.
        surface.ZIndex = region.OnTop ? 1 : 0;
        webView.ZIndex = region.OnTop ? 0 : 1;

        // ⚠ Margin plus explicit sizes, not Fill — a filled view stretches across the whole cell and every
        // region the page sends draws identically.
        // ⚠ The numbers are CSS pixels used UNCONVERTED; MAUI's density-independent units are the same unit.
        surface.Margin = new Thickness(region.X, region.Y, 0, 0);
        surface.WidthRequest = region.Width;
        surface.HeightRequest = region.Height;

        if (!surface.IsVisible)
        {
            Log(() => $"media surface: showing at {region.X:0},{region.Y:0} "
                + $"{region.Width:0}x{region.Height:0} css px, {(region.OnTop ? "over" : "under")} the page");
        }

        surface.IsVisible = true;
    });

    /// <inheritdoc />
    public void Hide() => OnUi((surface, _) =>
    {
        if (surface.IsVisible) Log(() => "media surface: hidden");
        surface.IsVisible = false;
    });

    /// <summary>
    /// Run <paramref name="work"/> against the attached views, on the UI thread.
    /// <para>
    /// ⚠ <b>Marshalled, not called directly</b>: these arrive on the IPC dispatcher's thread, and setting
    /// a layout property off the UI thread surfaces as an occasional crash rather than an error here.
    /// </para>
    /// <para>
    /// 🔴 <b>Nothing attached is WARNED ABOUT ONCE, not silently ignored.</b> A page positioning a picture
    /// against a surface no page ever attached sees exactly what a broken player looks like; once, rather
    /// than per frame, because the page repositions on every scroll.
    /// </para>
    /// </summary>
    private void OnUi(Action<MediaSurfaceView, VisualElement> work)
    {
        // Read once: Attach can replace all three from the UI thread while this runs.
        var surface = _surface;
        var webView = _webView;
        var dispatcher = _dispatcher;

        if (surface is null || webView is null || dispatcher is null)
        {
            Warn(() => "media surface: no page has attached one, so the picture has nowhere to go "
                + "— call MobileMediaSurface.Attach when the page is built");
            return;
        }

        // 🔴 A SURFACE WITH NO PLAYER IS THE ONE FAILURE THAT LOOKS LIKE SUCCESS. The hole opens, the page
        // draws its controls over it, and nothing ever decodes into it — indistinguishable from a film the
        // platform refused. A shell in this state must not be advertising ShellCapability.MediaSurface.
        if (surface.Player is null)
        {
            Warn(() => "media surface: attached but MediaSurfaceView.Player is null, so nothing will draw "
                + "— set it to the shell's own player, and do not advertise the mediaSurface capability "
                + "until you have");
        }

        dispatcher.Dispatch(() => work(surface, webView));
    }

    private void Log(Func<string> message) => AppCallback.Log(_log, message);

    /// <summary>Say it ONCE. The page repositions on every scroll frame, so a per-call warning is how a
    /// device log becomes unreadable — and it would bury the line that names the fault.</summary>
    private void Warn(Func<string> message)
    {
        if (_warned) return;
        _warned = true;
        Log(message);
    }
}
