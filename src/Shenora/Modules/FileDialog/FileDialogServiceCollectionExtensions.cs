using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora.Modules.FileDialog;
using Shenora.Core.Ipc;

namespace Shenora;

/// <summary>
/// Wires <see cref="FileDialogModule"/> into DI, so a page can drive the shell's native dialogs.
/// </summary>
public static class FileDialogServiceCollectionExtensions
{
    /// <summary>
    /// Register the file-dialog route module. OPT-IN, because the routes need an
    /// <see cref="IFileDialogs"/> only a shell can supply — which is also why it stays PUBLIC: two shell
    /// packages call it, and a <c>ProjectReference</c> grants no <c>internal</c> access.
    /// <para>
    /// ⚠ <b>Requires an <see cref="IFileDialogs"/> in the container</b>, which the SHELL registers —
    /// <c>UseWindows</c> on the desktop, <c>UseAndroid</c>/<c>UseIOS</c> on Android/iOS. Call this after
    /// the shell; a missing registration surfaces as an ordinary resolve failure on first dispatch.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraFileDialogs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // TryAddEnumerable, not AddIpcModule: the SHELL calls this too (D64), and an app that also calls
        // it must be a no-op — two facades claiming one module name is a duplicate the dispatcher rejects.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, FileDialogModule>());
        return services;
    }
}
