using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Modules.Media;

namespace Shenora;

/// <summary>
/// The media tier's SERVICE-COLLECTION half: <c>Add</c> is the container, <c>Use</c> is the pipeline (D73),
/// so containment, the cache location and the diagnostics sink are registered here and the ROUTES on the
/// interceptor.
/// </summary>
public static class MediaServiceCollectionExtensions
{
    /// <summary>
    /// Register the media tier's shared configuration: ONE <see cref="MediaAccessOptions"/> that every
    /// delivery route AND the shell's platform converters read (D71). <c>TryAdd</c>, so an app's own
    /// registration WINS and calling this twice is harmless; it registers no route and starts nothing.
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="MediaAccessOptions.Log"/> is the sink the PLATFORM CONVERTERS reach. A shell registers
    /// its converters without one, so they stay MUTE until an app casts the pipeline
    /// (<c>GetService&lt;IMediaStreamConversion&gt;() as MediaConversionPipeline</c>) and re-registers them
    /// (D73).
    /// </remarks>
    /// <param name="services">The app's container.</param>
    /// <param name="access">
    /// Where media may be read from and how a URL maps to a source. ⚠
    /// <see cref="MediaAccessOptions.AllowedRoots"/> is required and empty means SERVE NOTHING.
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
