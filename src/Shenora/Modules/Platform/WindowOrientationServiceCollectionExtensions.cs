using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core.Ipc;
using Shenora.Modules.Platform;

namespace Shenora;

/// <summary>
/// Wires <see cref="WindowOrientationModule"/> into DI, so a page can hold the window at an orientation
/// without taking fullscreen first.
/// </summary>
public static class WindowOrientationServiceCollectionExtensions
{
    /// <summary>
    /// Register the orientation route module. <b>OPT-IN</b>: an app that never rotates should not mount
    /// routes it will not call.
    /// <para>
    /// ⚠ <b>Requires an <see cref="Core.Shell.IWindowOrientation"/> in the container</b>, which the SHELL
    /// registers — <c>UseAndroid</c> today. Call it after the shell, and only on a shell that has one:
    /// there is nothing to hold an orientation for on the desktop, so <c>UseWindows</c> registers none and
    /// this cluster has nothing to resolve there.
    /// </para>
    /// <para>
    /// ⚠ <b>Advertise <see cref="Core.Shell.ShellCapability.WindowOrientation"/> alongside it</b>, or the
    /// page cannot tell "this shell will not rotate" from "my call was lost" — the D63 shape.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraWindowOrientation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // TryAddEnumerable: calling twice must be a no-op — two facades claiming one module name is a
        // duplicate the dispatcher rejects.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, WindowOrientationModule>());
        return services;
    }
}
