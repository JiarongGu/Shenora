using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core.Ipc;
using Shenora.Modules.Platform;

namespace Shenora;

/// <summary>Wires <see cref="ColorSchemeModule"/> into DI, so a page can read and change the app's colour scheme.</summary>
public static class ColorSchemeServiceCollectionExtensions
{
    /// <summary>
    /// Register the colour scheme route module. <b>OPT-IN</b>: only an app whose page offers the choice needs the
    /// route; the setting itself, and its effect on the page, need nothing.
    /// <para>
    /// ⚠ <b>Requires an <see cref="Core.Shell.IColorScheme"/> in the container</b>, which the desktop shells register
    /// (<c>UseChromium</c>, <c>UseWindows</c>). Advertise <see cref="Core.Shell.ShellCapability.ColorScheme"/>
    /// alongside it, so the page can tell a shell without the setting from a lost call.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraColorScheme(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, ColorSchemeModule>());
        return services;
    }
}
