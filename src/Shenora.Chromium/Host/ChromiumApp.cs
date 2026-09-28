using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>The <c>cef_app_t</c> the shell hands CEF, and the browser-process handler behind it.</summary>
internal sealed unsafe class ChromiumApp : CefObject<_cef_app_t>
{
    /// <summary>
    /// Chromium's local-network check on WebSockets, off while the page comes from a dev server. The shell proxies
    /// the dev server's document to mark it (D83), so Chromium does not see that page as local, and refuses its
    /// WebSocket back to the dev server (<c>ERR_BLOCKED_BY_LOCAL_NETWORK_ACCESS_CHECKS</c>, with no prompt): the
    /// dev server's hot-reload socket (measured). Only in development, and only the WebSocket check.
    /// </summary>
    internal const string DevServerDisabledFeature = "LocalNetworkAccessChecksWebSockets";

    private readonly ProcessHandler _process;
    private readonly bool _devServer;

    /// <param name="contextInitialized">Runs on CEF's UI thread once CEF's context exists.</param>
    /// <param name="devServer">The page comes from a dev server (development with a dev URL).</param>
    public ChromiumApp(Action contextInitialized, bool devServer = false)
    {
        _process = new ProcessHandler(contextInitialized);
        _devServer = devServer;
        Struct->get_browser_process_handler = &GetProcessHandler;
        Struct->on_before_command_line_processing = &BeforeCommandLine;
    }

    /// <summary><c>--disable-features</c> with <paramref name="feature"/> added to whatever the app already disables.</summary>
    internal static string WithDisabledFeature(string? existing, string feature)
    {
        var features = (existing ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!features.Contains(feature, StringComparer.Ordinal)) features.Add(feature);
        return string.Join(',', features);
    }

    [UnmanagedCallersOnly]
    private static _cef_browser_process_handler_t* GetProcessHandler(_cef_app_t* self) => From<ChromiumApp>(self)._process.ForCef();

    // The browser process only (an empty process type), where the switch reaches every child it starts.
    [UnmanagedCallersOnly]
    private static void BeforeCommandLine(_cef_app_t* self, _cef_string_utf16_t* processType, _cef_command_line_t* commandLine)
    {
        using var line = new CefRef<_cef_command_line_t>(commandLine);
        if (!From<ChromiumApp>(self)._devServer || !string.IsNullOrEmpty(CefStrings.Read(processType))) return;
        AppCallback.Run(() =>
        {
            const string name = "disable-features";
            fixed (char* n = name)
            {
                var switchName = CefStrings.View(n, name.Length);
                var existing = commandLine->has_switch(commandLine, &switchName) == 1
                    ? CefStrings.TakeUserFree(commandLine->get_switch_value(commandLine, &switchName))
                    : null;
                var value = WithDisabledFeature(existing, DevServerDisabledFeature);
                commandLine->remove_switch(commandLine, &switchName);
                fixed (char* v = value)
                {
                    var switchValue = CefStrings.View(v, value.Length);
                    commandLine->append_switch_with_value(commandLine, &switchName, &switchValue);
                }
            }
        });
    }

    private sealed class ProcessHandler : CefObject<_cef_browser_process_handler_t>
    {
        private readonly Action _contextInitialized;

        public ProcessHandler(Action contextInitialized)
        {
            _contextInitialized = contextInitialized;
            Struct->on_context_initialized = &ContextInitialized;
        }

        [UnmanagedCallersOnly]
        private static void ContextInitialized(_cef_browser_process_handler_t* self) => AppCallback.Run(From<ProcessHandler>(self)._contextInitialized);
    }
}
