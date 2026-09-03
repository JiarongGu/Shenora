using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui.Hosting;
using Shenora.Modules.Media;

#if ANDROID
using PlatformMediaSurfaceHandler = Shenora.Android.AndroidMediaSurfaceHandler;
#elif IOS || MACCATALYST
using PlatformMediaSurfaceHandler = Shenora.iOS.IosMediaSurfaceHandler;
#endif

namespace Shenora.Mobile;

/// <summary>Puts the shell's picture surface on a <see cref="MauiAppBuilder"/>.</summary>
public static class MobileMediaSurfaceExtensions
{
    /// <summary>
    /// Register <see cref="MediaSurfaceView"/>'s platform handler and make the webview see-through, so the
    /// shell can draw a picture under a hole the page leaves.
    /// <para>
    /// <b>Three more things are yours</b>, and each one missing gives the same symptom — no picture:
    /// </para>
    /// <list type="number">
    ///   <item>Register the service with <see cref="AddShenoraMediaSurface"/> on the Shenora builder.</item>
    ///   <item>Put a <see cref="MediaSurfaceView"/> in a layout BEFORE the webview, set its
    ///   <see cref="MediaSurfaceView.Player"/>, and <see cref="MobileMediaSurface.Attach"/> the pair when
    ///   the page is built.</item>
    ///   <item>Make the page's own background transparent where the picture belongs — this call cannot
    ///   reach the document, and an opaque <c>body</c> hides everything underneath it.</item>
    /// </list>
    /// <para>
    /// ⚠ <b>Opt-in.</b> An app that plays no video should not call it: the transparency mapping applies to
    /// every webview the app realizes, and a page that stops painting its own background would then show
    /// whatever is behind it.
    /// </para>
    /// </summary>
    /// <param name="builder">The MAUI application builder.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
#if ANDROID || IOS || MACCATALYST
    public static MauiAppBuilder UseShenoraMediaSurface(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<MediaSurfaceView, PlatformMediaSurfaceHandler>());

        MobileWebViewTransparency.Enable();
        return builder;
    }

    /// <summary>
    /// Register the shell's picture surface, so the media module's <c>SURFACE_SHOW</c>/<c>SURFACE_HIDE</c>
    /// routes have somewhere to land.
    /// <para>
    /// <b>Opt-in</b>, like every other kit cluster: an app that plays no video registers nothing, and its
    /// shell then reports the capability absent instead of accepting positions it will never draw.
    /// </para>
    /// <para>
    /// ⚠ <b>It registers a surface with no VIEWS yet</b> — the page attaches those later
    /// (<see cref="MobileMediaSurface.Attach"/>), because DI is composed before any page exists.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddShenoraMediaSurface(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // ONE object under two registrations: the page resolves the concrete type to Attach, the media
        // module resolves the contract. Two singletons would give the module a surface no page ever
        // attached, which is the one failure this feature cannot afford to make silent.
        services.TryAddSingleton<MobileMediaSurface>();
        services.TryAddSingleton<IMediaSurface>(sp => sp.GetRequiredService<MobileMediaSurface>());
        return services;
    }
#else
#error Shenora.Mobile: this platform has no media surface handler. Add one and an alias above (D65).
#endif
}
