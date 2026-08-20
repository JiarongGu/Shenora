using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Core.Ipc;
using Shenora.Modules.Clipboard;

namespace Shenora;

/// <summary>
/// Wires <see cref="ClipboardModule"/> into DI, so a page can reach the parts of the native clipboard
/// the web platform withholds.
/// </summary>
public static class ClipboardServiceCollectionExtensions
{
    /// <summary>
    /// Register the clipboard route module. <b>OPT-IN, not defaulted on</b> unlike the file dialogs a
    /// shell adds for every app (D64) — mounting it hands the page
    /// <see cref="ClipboardModule.ReadType"/>, so read <see cref="ClipboardModule"/> before calling this.
    /// <para>
    /// ⚠ <b>Requires an <see cref="Core.Shell.IClipboardService"/> in the container</b>, which the SHELL
    /// registers — <c>UseWindows</c> on the desktop, <c>UseAndroid</c>/<c>UseIOS</c> on mobile. Call this
    /// after the shell.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraClipboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // TryAddEnumerable: calling twice must be a no-op — two facades claiming one module name is a
        // duplicate the dispatcher rejects.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, ClipboardModule>());
        return services;
    }
}
