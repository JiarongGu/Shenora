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
    /// Register the clipboard route module. <b>OPT-IN, and deliberately not defaulted on</b>, unlike the
    /// file dialogs a shell now adds for every app (D64).
    /// <para>
    /// 🔴 <b>The reason is what <see cref="ClipboardModule.ReadType"/> grants:</b> reading the user's
    /// clipboard at any moment, with no gesture and no permission prompt. The web withholds that on
    /// purpose — a clipboard routinely holds a password or a bank detail copied from another
    /// application entirely — so handing it to the page has to be something an app SAYS, not something
    /// it receives by composing a shell. Most pages want <c>navigator.clipboard</c> and should never
    /// call this; see <see cref="ClipboardModule"/> for the two capabilities that justify it.
    /// </para>
    /// <para>
    /// ⚠ <b>Requires an <see cref="Core.Shell.IClipboardService"/> in the container</b>, which the SHELL
    /// registers — <c>UseWindows</c> on the desktop, <c>UseAndroid</c>/<c>UseIOS</c> on mobile. Call this
    /// after the shell. Nothing resolves eagerly: the facade is constructed on first dispatch, so a
    /// missing shell registration surfaces as an ordinary resolve failure rather than a startup crash.
    /// </para>
    /// </summary>
    public static IServiceCollection AddShenoraClipboard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // TryAddEnumerable for the same reason the dialogs use it: calling twice must be a no-op rather
        // than registering a second facade, since two facades claiming one module name is a duplicate
        // the dispatcher rejects.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcModule, ClipboardModule>());
        return services;
    }
}
