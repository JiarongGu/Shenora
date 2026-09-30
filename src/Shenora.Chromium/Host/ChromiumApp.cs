using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;
using Shenora.Core.Shell;

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

    /// <summary>
    /// macOS: Chromium's mock keychain for its cookie-encryption key (D85). The real one keeps it in
    /// a login-keychain item every CEF app shares ("Chromium Safe Storage"), so the first app to run owns it and any
    /// other waits on a login-password prompt before its page loads (measured: the same app stalled without the switch
    /// and ran with it). The cost is that cookies at rest are encrypted with a fixed key.
    /// </summary>
    internal const string MockKeychain = "use-mock-keychain";

    /// <summary>
    /// A process that is only Chromium's browser (D86): no first-run page, and no offer to become the system's default
    /// browser, which is the user's browser to make and not an app's.
    /// </summary>
    internal static readonly string[] BrowserSwitches = ["no-first-run", "no-default-browser-check"];

    private readonly ProcessHandler _process;
    private readonly bool _appPages;

    /// <param name="contextInitialized">Runs on CEF's UI thread once CEF's context exists.</param>
    /// <param name="browser">This process holds no app page, only Chromium's own windows onto the web (D86): the
    /// local-network checks stay on, and <paramref name="browser"/> is the client of every window Chrome's UI opens.
    /// Null for the app's own pages.</param>
    /// <param name="relaunched">A later launch CEF handed to this process, on CEF's UI thread: its folder, and no
    /// arguments (see <c>Relaunched</c>). A process with the app's pages answers every one, this or nothing; a browser
    /// process (<paramref name="browser"/>) leaves it to Chrome, which opens a window.</param>
    public ChromiumApp(Action contextInitialized, CefObject<_cef_client_t>? browser = null, Action<SingleInstanceLaunch>? relaunched = null)
    {
        _process = new ProcessHandler(contextInitialized, browser, browser is null ? relaunched ?? (_ => { }) : null);
        _appPages = browser is null;
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
        var appPages = From<ChromiumApp>(self)._appPages;
        AppCallback.Run(() =>
        {
            if (appPages)
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
            }
            else
            {
                foreach (var browserSwitch in BrowserSwitches) AppendSwitch(commandLine, browserSwitch);
            }
#if CEF_MACOS
            AppendSwitch(commandLine, MockKeychain);
#endif
        });
    }

    private static void AppendSwitch(_cef_command_line_t* commandLine, string name)
    {
        fixed (char* n = name)
        {
            var switchName = CefStrings.View(n, name.Length);
            commandLine->append_switch(commandLine, &switchName);
        }
    }


    private sealed class ProcessHandler : CefObject<_cef_browser_process_handler_t>
    {
        private readonly Action _contextInitialized;
        private readonly CefObject<_cef_client_t>? _defaultClient;
        private readonly Action<SingleInstanceLaunch>? _relaunched;

        public ProcessHandler(Action contextInitialized, CefObject<_cef_client_t>? defaultClient, Action<SingleInstanceLaunch>? relaunched)
        {
            _contextInitialized = contextInitialized;
            _defaultClient = defaultClient;
            _relaunched = relaunched;
            Struct->on_context_initialized = &ContextInitialized;
            // Chrome's UI makes a window's browsers itself (a CDP target, a new tab, a window.open), and without a
            // client CEF knows nothing of them: no callback runs, and shutdown waits for them to be closed by hand.
            if (defaultClient is not null) Struct->get_default_client = &DefaultClient;
            if (relaunched is not null) Struct->on_already_running_app_relaunch = &Relaunched;
        }

        [UnmanagedCallersOnly]
        private static void ContextInitialized(_cef_browser_process_handler_t* self) => AppCallback.Run(From<ProcessHandler>(self)._contextInitialized);

        // CEF lets one process own a data folder (root_cache_path) and forwards a later launch's command line to it.
        // Left unanswered it opens a Chrome-style window HERE, on the app's own profile and beside the page that holds
        // the bridge (measured on Windows: "New tab - Chromium", with the app's pages among its tiles).
        // ⚠ The launch arrives with NO arguments. That command line is Chromium's, not the app's: outside development
        // the shell has CEF ignore the app's command line, and what came was only CEF's own switches (its log file,
        // its disabled features), with none of "open me.txt --flag" the launch was given (measured on Windows).
        [UnmanagedCallersOnly]
        private static int Relaunched(_cef_browser_process_handler_t* self, _cef_command_line_t* commandLine, _cef_string_utf16_t* currentDirectory)
        {
            using var line = new CefRef<_cef_command_line_t>(commandLine);
            var relaunched = From<ProcessHandler>(self)._relaunched!;
            var folder = CefStrings.Read(currentDirectory);
            AppCallback.Run(() => relaunched(new SingleInstanceLaunch([], folder)));
            return 1;
        }

        [UnmanagedCallersOnly]
        private static _cef_client_t* DefaultClient(_cef_browser_process_handler_t* self) =>
            From<ProcessHandler>(self)._defaultClient is { } client ? client.ForCef() : null;
    }
}
