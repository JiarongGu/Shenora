using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Modules.Media;

// Extensions live with the type they EXTEND — see MediaPlayerExtensions for the rule.
namespace Shenora;

/// <summary>
/// The media tier's SERVICE-COLLECTION half.
///
/// <para>
/// 🔴 <b><c>Add</c> is the container; <c>Use</c> is the pipeline (D73).</b> That split is not invented here
/// — it shipped with D66's <c>AddMessageDispatcher</c> → <c>UseMessageDispatcher</c> rename, whose rule is
/// exactly *"<c>Use</c> means a wider configuration INCLUDING its pipeline; <c>Add</c> is the
/// service-collection level only"*. So containment, the cache location and the diagnostics sink are
/// registered here, and the ROUTES are registered on the interceptor.
/// </para>
/// </summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>
    /// Register the media tier's shared configuration: ONE <see cref="MediaAccessOptions"/> that every
    /// delivery route AND the shell's platform converters read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>ONE <see cref="MediaAccessOptions"/> is the point, not a convenience.</b> Three options types
    /// take one (D71) so containment and the cache location are stated ONCE — three copies is how they
    /// drift, and the drift is a security boundary. Registering it makes the sharing automatic instead of
    /// something each route's construction has to remember.
    /// </para>
    /// <para>
    /// ⚠ <b>It registers no route and starts nothing.</b> A route is pipeline configuration and belongs on
    /// the interceptor; this call is safe in a container an app builds before it has a webview at all.
    /// </para>
    /// <para>
    /// ⚠ <b><c>TryAdd</c>, so an app's own registration WINS</b> — the same escape hatch every other
    /// service here has, and the reason calling this twice is harmless rather than a duplicate.
    /// </para>
    /// <para>
    /// 🔴 <b>Its <see cref="MediaAccessOptions.Log"/> is what finally reaches the PLATFORM CONVERTERS, and
    /// that is why no new type was added for it.</b> A shell registers its converters with no sink
    /// (deliberately), so an app had to write
    /// <c>GetService&lt;IMediaStreamConversion&gt;() as MediaConversionPipeline</c> and re-register to hear a
    /// word — three device round-trips were spent in one session on converters that were mute (D73). A
    /// separate diagnostics type was written first and DELETED: the surface-vocabulary gate rejected the
    /// word, and it was right for a better reason than naming — this object already carries the tier's log,
    /// so a second one would have let the routes and the converters disagree about where lines go.
    /// </para>
    /// </remarks>
    /// <param name="services">The app's container.</param>
    /// <param name="access">
    /// Where media may be read from and how a URL maps to a source. ⚠ <see cref="MediaAccessOptions.AllowedRoots"/>
    /// is required and empty means SERVE NOTHING — a missing configuration fails closed rather than serving
    /// the filesystem.
    /// </param>
    public static IServiceCollection AddShenoraMedia(this IServiceCollection services,
                                                    MediaAccessOptions access)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(access.Resolve);

        services.TryAddSingleton(access);
        return services;
    }
}
