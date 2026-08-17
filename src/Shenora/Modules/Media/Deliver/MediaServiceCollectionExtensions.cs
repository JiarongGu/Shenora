using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Modules.Media;

namespace Shenora;

/// <summary>
/// The media tier's SERVICE-COLLECTION half. 🔴 <b><c>Add</c> is the container; <c>Use</c> is the pipeline
/// (D73)</b> — so containment, the cache location and the diagnostics sink are registered here, and the
/// ROUTES are registered on the interceptor.
/// </summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>
    /// Register the media tier's shared configuration: ONE <see cref="MediaAccessOptions"/> that every
    /// delivery route AND the shell's platform converters read.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Three options types must share ONE <see cref="MediaAccessOptions"/></b> (D71), so containment
    /// and the cache location are stated ONCE — three copies drift, and the drift is a security boundary.
    /// <c>TryAdd</c>, so an app's own registration WINS and calling this twice is harmless; it registers
    /// no route and starts nothing, so it is safe in a container an app builds before it has a webview.
    /// <para>
    /// ⚠ <see cref="MediaAccessOptions.Log"/> is the sink the PLATFORM CONVERTERS reach. A shell registers
    /// its converters without one, so they stay MUTE until an app casts the pipeline
    /// (<c>GetService&lt;IMediaStreamConversion&gt;() as MediaConversionPipeline</c>) and re-registers
    /// them (D73).
    /// </para>
    /// </remarks>
    /// <param name="services">The app's container.</param>
    /// <param name="access">
    /// Where media may be read from and how a URL maps to a source. ⚠
    /// <see cref="MediaAccessOptions.AllowedRoots"/> is required and empty means SERVE NOTHING — a missing
    /// configuration fails closed rather than serving the filesystem.
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
