using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>The <c>cef_app_t</c> the shell hands CEF, and the browser-process handler behind it.</summary>
internal sealed unsafe class ChromiumApp : CefObject<_cef_app_t>
{
    /// <summary>
    /// Chromium's local-network checks, off for every page (D83). The pages are the app's own, and the checks refused
    /// what the WebView2 shell allows, with no prompt the permission handler could answer (measured, CEF 154):
    /// <list type="bullet">
    /// <item>a page's fetch to a loopback server of the app's own failed, where WebView2 answered it;</item>
    /// <item>the dev server's hot-reload socket, from the dev server's document the shell proxies to mark it.</item>
    /// </list>
    /// </summary>
    internal static readonly string[] DisabledFeatures = ["LocalNetworkAccessChecks", "LocalNetworkAccessChecksWebSockets"];

    private readonly ProcessHandler _process;

    /// <param name="contextInitialized">Runs on CEF's UI thread once CEF's context exists.</param>
    public ChromiumApp(Action contextInitialized)
    {
        _process = new ProcessHandler(contextInitialized);
        Struct->get_browser_process_handler = &GetProcessHandler;
        Struct->on_before_command_line_processing = &BeforeCommandLine;
    }

    /// <summary><c>--disable-features</c> with <paramref name="features"/> added to whatever the app already disables.</summary>
    internal static string WithDisabledFeatures(string? existing, IEnumerable<string> features)
    {
        var all = (existing ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var feature in features)
            if (!all.Contains(feature, StringComparer.Ordinal)) all.Add(feature);
        return string.Join(',', all);
    }

    [UnmanagedCallersOnly]
    private static _cef_browser_process_handler_t* GetProcessHandler(_cef_app_t* self) => From<ChromiumApp>(self)._process.ForCef();

    // The browser process only (an empty process type), where the switch reaches every child it starts.
    [UnmanagedCallersOnly]
    private static void BeforeCommandLine(_cef_app_t* self, _cef_string_utf16_t* processType, _cef_command_line_t* commandLine)
    {
        using var line = new CefRef<_cef_command_line_t>(commandLine);
        if (!string.IsNullOrEmpty(CefStrings.Read(processType))) return;
        AppCallback.Run(() =>
        {
            const string name = "disable-features";
            fixed (char* n = name)
            {
                var switchName = CefStrings.View(n, name.Length);
                var existing = commandLine->has_switch(commandLine, &switchName) == 1
                    ? CefStrings.TakeUserFree(commandLine->get_switch_value(commandLine, &switchName))
                    : null;
                var value = WithDisabledFeatures(existing, DisabledFeatures);
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
