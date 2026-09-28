using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// Runs the app on CEF's message loop, on the main thread (D83's Views model).
/// <list type="number">
/// <item>The sandbox: from the shim when CEF's bootstrap started the app (D82), else none, and then THIS exe
/// is every CEF subprocess too, which is what <c>cef_execute_process</c> answers first.</item>
/// <item><c>cef_api_hash</c> is the FIRST CEF call, selecting the Stable API version the binding was built for.</item>
/// <item>🔴 Outside development, command-line switches are disabled, so only the shell can open a debug port:
/// a port on the app's own command line otherwise reaches the page holding the bridge (measured, CEF 152).</item>
/// <item>The app's hooks start once CEF's context exists, and stop after the loop, before CEF shuts down.</item>
/// </list>
/// </summary>
internal sealed unsafe class ChromiumRunner(ChromiumHostOptions options, CefUiDispatcher ui, ChromiumWindows windows, ILogger<ChromiumRunner>? log = null)
    : IShenoraRunner
{
    public void Run(ShenoraApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var sandbox = RuntimePointer("Shenora.Chromium.SandboxInfo");
        Cef.cef_api_hash(CefApi.Version, 0);
        var args = MainArgs(RuntimePointer("Shenora.Chromium.Instance"));
        var isDevelopment = options.IsDevelopment ?? app.Environment.IsDevelopment;
        var cefApp = new ChromiumApp(() => Started(app, isDevelopment), devServer: isDevelopment && options.DevUrl is not null);

        if (sandbox == 0)
        {
            var code = Cef.cef_execute_process(&args, cefApp.ForCef(), null);
            if (code >= 0) { Environment.Exit(code); return; }
            AppCallback.Log(log, () => "[Shenora.Chromium] Running without CEF's bootstrap launcher: Chromium's sandbox is OFF", LogLevel.Warning);
        }

        var cache = options.CachePath ?? app.Paths.DataArea("chromium");
        Directory.CreateDirectory(cache);
        var profile = Path.Combine(cache, "default");
        var logFile = Path.Combine(cache, "cef.log");
        var settings = new _cef_settings_t
        {
            size = (nuint)sizeof(_cef_settings_t),
            no_sandbox = sandbox == 0 ? 1 : 0,
            command_line_args_disabled = isDevelopment ? 0 : 1,
            remote_debugging_port = isDevelopment ? options.DevToolsPort : 0,
            log_severity = cef_log_severity_t.LOGSEVERITY_WARNING,
        };
        if (options.Window.BackgroundColor is { } color) settings.background_color = (uint)color.ToArgb();

        int initialized;
        fixed (char* c = cache)
        fixed (char* p = profile)
        fixed (char* l = logFile)
        {
            settings.root_cache_path = CefStrings.View(c, cache.Length);
            settings.cache_path = CefStrings.View(p, profile.Length);
            settings.log_file = CefStrings.View(l, logFile.Length);
            initialized = Cef.cef_initialize(&args, &settings, cefApp.ForCef(), (void*)sandbox);
        }
        if (initialized == 0)
            throw new InvalidOperationException($"Chromium did not start (CEF exit code {Cef.cef_get_exit_code()}). Its log is {logFile}.");

        SynchronizationContext.SetSynchronizationContext(new CefUiContext(ui, log));
        try
        {
            Cef.cef_run_message_loop();
        }
        finally
        {
            ui.MarkGone();
            AppCallback.Run(app.Stop, ex => AppCallback.Log(log, () => "[Shenora.Chromium] Stopping the app failed", LogLevel.Error, ex));
            Cef.cef_shutdown();
        }
    }

    /// <summary>CEF's context exists: the UI thread is usable, the app starts, the main window opens.</summary>
    private void Started(ShenoraApplication app, bool isDevelopment)
    {
        ui.MarkReady();
        try
        {
            app.Start();
            windows.Initialize(app, isDevelopment);
            windows.Open(ChromiumWindows.MainWindowName, options.Window);
        }
        catch (Exception ex)
        {
            AppCallback.Log(log, () => "[Shenora.Chromium] The app could not start; quitting", LogLevel.Critical, ex);
            Cef.cef_quit_message_loop();
        }
    }

    private static nint RuntimePointer(string name) =>
        AppContext.GetData(name) is string hex && long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? (nint)value
            : 0;

#if CEF_WINDOWS
    private static _cef_main_args_t MainArgs(nint instance) =>
        new() { instance = instance != 0 ? instance : GetModuleHandleW(null) };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);
#else
    /// <summary>argv as UTF-8, allocated once and kept for the process: CEF may read it at any time.</summary>
    private static _cef_main_args_t MainArgs(nint instance)
    {
        var argv = Environment.GetCommandLineArgs();
        var pointers = (sbyte**)NativeMemory.Alloc((nuint)(argv.Length + 1), (nuint)sizeof(sbyte*));
        for (var i = 0; i < argv.Length; i++) pointers[i] = (sbyte*)Marshal.StringToCoTaskMemUTF8(argv[i]);
        pointers[argv.Length] = null;
        return new _cef_main_args_t { argc = argv.Length, argv = pointers };
    }
#endif
}
