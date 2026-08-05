using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;

namespace Shenora.Ipc;

/// <summary>
/// Wires <see cref="FileDialogFacade"/> into DI, so a page can drive the shell's native dialogs.
/// </summary>
public static class FileDialogServiceCollectionExtensions
{
    /// <summary>
    /// Register the file-dialog route module. OPT-IN, like <see cref="OperationServiceCollectionExtensions.AddShenoraOperations"/>:
    /// an app whose page never picks a file should not carry the routes, and D21 says the kit ships the
    /// primitive rather than the product.
    /// <para>
    /// ⚠ <b>Requires an <see cref="IFileDialogs"/> in the container</b>, which the SHELL registers —
    /// <c>UseWinForms</c> on the desktop, <c>UseMobile</c> on Android/iOS. Call this after the shell, and
    /// nothing here resolves it eagerly: the facade is constructed on first dispatch through the same lazy
    /// path every other DI-registered module uses, so a missing shell registration surfaces as an ordinary
    /// resolve failure rather than a startup crash inside a singleton factory.
    /// </para>
    /// <para>
    /// It takes no options. The facade's module name is a constant
    /// (<see cref="FileDialogFacade.Module"/>) because this facade publishes no events, so there is nothing
    /// for a configurable name to keep in step with — the reason <c>OperationsFacade</c> needs one.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraFileDialogs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModuleFacade<FileDialogFacade>();
        return services;
    }
}
