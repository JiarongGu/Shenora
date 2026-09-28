using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Core.Events;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Modules.FileDialog;

namespace Shenora.Chromium;

/// <summary>Composes the Chromium shell (D82) into an app.</summary>
public static class ChromiumHostExtensions
{
    /// <summary>
    /// Run the app in the Chromium shell: CEF's own windows, the page served from the app's origin, and IPC over
    /// a transport with no code in the renderer (D83). The main window opens once CEF has started, and the app
    /// ends when the last window closes. Unless the app registered its own first, it registers the shell's
    /// <see cref="IUrlLauncher"/> (the user's browser, http/https only; a page's popups go there) and
    /// <see cref="IFileDialogs"/> (CEF's native dialogs over the main window), with the page's route to the dialogs,
    /// and on Windows <see cref="IClipboardService"/>.
    /// <para>
    /// ⚠ On Windows, Chromium's sandbox exists only when the app starts through CEF's <c>bootstrap.exe</c> and
    /// the kit's shim (D82). Started any other way it runs, and logs a warning that the sandbox is off.
    /// </para>
    /// </summary>
    /// <param name="builder">The app being composed.</param>
    /// <param name="options">The main window, where the page comes from, and development settings.</param>
    public static ShenoraApplicationBuilder UseChromium(this ShenoraApplicationBuilder builder, ChromiumHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new CefUiDispatcher(
            work => CefTask.Post(cef_thread_id_t.TID_UI, work),
            () => Cef.cef_currently_on(cef_thread_id_t.TID_UI) == 1,
            sp.GetService<ILogger<CefUiDispatcher>>()));
        builder.Services.TryAddSingleton<IUiDispatcher>(sp => sp.GetRequiredService<CefUiDispatcher>());
        // The native services, TryAdd so an app's own registration wins.
        builder.Services.TryAddSingleton<IUrlLauncher, ChromiumUrlLauncher>();
#if CEF_WINDOWS
        // The page's route to it stays the app's opt-in (AddShenoraClipboard), as with the WebView2 shell.
        builder.Services.TryAddSingleton<IClipboardService, Win32Clipboard>();
#endif
        builder.Services.AddSingleton(sp => new ChromiumWindows(options, sp.GetRequiredService<CefUiDispatcher>(),
            sp.GetRequiredService<IMessageDispatcher>(), sp.GetService<IEventBus>(), sp.GetService<ILogger<ChromiumWindows>>(),
            sp.GetRequiredService<IUrlLauncher>()));
        builder.Services.AddSingleton<IShenoraRunner, ChromiumRunner>();
        builder.Services.TryAddSingleton<IFileDialogs>(sp => new ChromiumFileDialogs(sp.GetRequiredService<ChromiumWindows>(),
            sp.GetRequiredService<CefUiDispatcher>(), sp.GetService<IFileDialogPathStore>(), sp.GetService<ILogger<ChromiumFileDialogs>>()));
        // The page's route to them, registered where the implementation exists (D64), as UseWindows does.
        builder.Services.AddShenoraFileDialogs();
        return builder;
    }
}
