using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shenora;
using Shenora.Modules.Requests;
using Shenora.Core.Ipc;

namespace Shenora.Modules.FileDialog;

/// <summary>
/// Wires <see cref="FileDialogModule"/> into DI, so a page can drive the shell's native dialogs.
/// </summary>
public static class FileDialogServiceCollectionExtensions
{
    /// <summary>
    /// Register the file-dialog route module. OPT-IN, like <see cref="IpcRequestServiceCollectionExtensions.AddShenoraRequests"/>:
    /// an app whose page never picks a file should not carry the routes, and D21 says the kit ships the
    /// primitive rather than the product.
    /// <para>
    /// ⚠ <b>Requires an <see cref="IFileDialogs"/> in the container</b>, which the SHELL registers —
    /// <c>UseWindows</c> on the desktop, <c>UseAndroid</c>/<c>UseIOS</c> on Android/iOS. Call this after the shell, and
    /// nothing here resolves it eagerly: the facade is constructed on first dispatch through the same lazy
    /// path every other DI-registered module uses, so a missing shell registration surfaces as an ordinary
    /// resolve failure rather than a startup crash inside a singleton factory.
    /// </para>
    /// <para>
    /// It takes no options. The facade's module name is a constant
    /// (<see cref="FileDialogModule.Module"/>) because this facade publishes no events, so there is nothing
    /// for a configurable name to keep in step with — the reason <c>OperationsModule</c> needs one.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraFileDialogs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // ⚠ TryAddEnumerable, not AddIpcModule: the SHELL calls this now (D64), and an app that also
        // calls it — every app written before that did — would otherwise register a SECOND
        // FileDialogModule, and two facades claiming one module name is a duplicate the dispatcher
        // rejects. So the old explicit call stays valid and becomes a harmless no-op, which is what makes
        // this a default rather than a migration.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, FileDialogModule>());
        return services;
    }
}
