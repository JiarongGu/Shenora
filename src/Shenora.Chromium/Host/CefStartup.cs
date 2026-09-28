using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// Starting CEF, which both of its hosts do the same way (D83): the Views shell on the main thread's loop, and a host
/// that owns its UI thread (<see cref="ChromiumEngine"/>) on a loop of CEF's own.
/// <list type="number">
/// <item>The sandbox: from the shim when CEF's bootstrap started the app (D82), else none, and then THIS exe is every
/// CEF subprocess too, which is what <see cref="ExecuteIfSubprocess"/> answers first.</item>
/// <item><c>cef_api_hash</c> is the FIRST CEF call, selecting the Stable API version the binding was built for.</item>
/// <item>🔴 Outside development, command-line switches are disabled, so only the shell can open a debug port: a port on
/// the app's own command line otherwise reaches the page holding the bridge (measured, CEF 152).</item>
/// </list>
/// </summary>
internal static unsafe class CefStartup
{
    /// <summary>What CEF is started with.</summary>
    /// <param name="Cache">The root of CEF's cache, its profile and its log.</param>
    /// <param name="IsDevelopment">Development: switches on, the debug port.</param>
    /// <param name="DevToolsPort">The debug port in development; 0 for none.</param>
    /// <param name="BackgroundColor">The browsers' background before a page paints, as ARGB.</param>
    /// <param name="MultiThreadedLoop">CEF runs its message loop on a thread of its own, for a host that owns its UI
    /// thread; false runs it on the caller's (the Views shell's main thread).</param>
    internal sealed record Settings(string Cache, bool IsDevelopment, int DevToolsPort, uint? BackgroundColor, bool MultiThreadedLoop);

    /// <summary>The sandbox the shim created, or 0 when the app was not started through CEF's launcher.</summary>
    public static nint Sandbox => RuntimePointer("Shenora.Chromium.SandboxInfo");

    /// <summary>
    /// CEF's first call, and a subprocess's whole life: when this process is one of CEF's (no shim, so this exe is
    /// every subprocess too), it runs as that and returns its exit code, which the caller exits with. Otherwise -1.
    /// </summary>
    public static int ExecuteIfSubprocess(ChromiumApp app, ILogger? log)
    {
        SelectApiVersion();
        if (Sandbox != 0) return -1;   // the shim ran every subprocess itself
        var args = MainArgs(RuntimePointer("Shenora.Chromium.Instance"));
        var code = Cef.cef_execute_process(&args, app.ForCef(), null);
        if (code < 0) AppCallback.Log(log, () => "[Shenora.Chromium] Running without CEF's bootstrap launcher: Chromium's sandbox is OFF", LogLevel.Warning);
        return code;
    }

    /// <summary>Initialize CEF. Throws when it will not start, naming its log.</summary>
    public static void Initialize(ChromiumApp app, Settings settings)
    {
        // A host started through the shim may skip ExecuteIfSubprocess, and without this the app crashed at start
        // (0x80000003, measured).
        SelectApiVersion();
        Directory.CreateDirectory(settings.Cache);
        var profile = Path.Combine(settings.Cache, "default");
        var logFile = Path.Combine(settings.Cache, "cef.log");
        var sandbox = Sandbox;
        var args = MainArgs(RuntimePointer("Shenora.Chromium.Instance"));
        var cef = new _cef_settings_t
        {
            size = (nuint)sizeof(_cef_settings_t),
            no_sandbox = sandbox == 0 ? 1 : 0,
            multi_threaded_message_loop = settings.MultiThreadedLoop ? 1 : 0,
            command_line_args_disabled = settings.IsDevelopment ? 0 : 1,
            remote_debugging_port = settings.IsDevelopment ? settings.DevToolsPort : 0,
            log_severity = cef_log_severity_t.LOGSEVERITY_WARNING,
        };
        if (settings.BackgroundColor is { } color) cef.background_color = color;

        int initialized;
        fixed (char* c = settings.Cache)
        fixed (char* p = profile)
        fixed (char* l = logFile)
        {
            cef.root_cache_path = CefStrings.View(c, settings.Cache.Length);
            cef.cache_path = CefStrings.View(p, profile.Length);
            cef.log_file = CefStrings.View(l, logFile.Length);
            initialized = Cef.cef_initialize(&args, &cef, app.ForCef(), (void*)sandbox);
        }
        if (initialized == 0)
            throw new InvalidOperationException($"Chromium did not start (CEF exit code {Cef.cef_get_exit_code()}). Its log is {logFile}.");
    }

    private static int _apiVersionSelected;

    // Once per process: it must precede every other CEF call, so it is also where a missing runtime shows first.
    private static void SelectApiVersion()
    {
        if (Volatile.Read(ref _apiVersionSelected) == 1) return;
        // The binding's structs are ONE OS's (CefOs), so a build run on another reads CEF's memory wrongly.
#if CEF_WINDOWS
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This build of Shenora.Chromium is Windows'.");
#elif CEF_MACOS
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This build of Shenora.Chromium is macOS'.");
#elif CEF_LINUX
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This build of Shenora.Chromium is Linux'.");
#endif
        try
        {
            Cef.cef_api_hash(CefApi.Version, 0);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "CEF's runtime is not beside the app. Reference the Shenora.Chromium package from the app's own project: "
                + "its build fetches the pinned CEF build and lays the app out beside CEF's launcher. A reference that "
                + "only reaches it through another package (Shenora.Windows) does not run that build (D83).", ex);
        }
        Volatile.Write(ref _apiVersionSelected, 1);
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
    private static _cef_main_args_t? _mainArgs;

    /// <summary>argv as UTF-8, allocated once and kept for the process: CEF may read it at any time.</summary>
    private static _cef_main_args_t MainArgs(nint instance) => _mainArgs ??= AllocateMainArgs();

    private static _cef_main_args_t AllocateMainArgs()
    {
        var argv = Environment.GetCommandLineArgs();
        var pointers = (sbyte**)NativeMemory.Alloc((nuint)(argv.Length + 1), (nuint)sizeof(sbyte*));
        for (var i = 0; i < argv.Length; i++) pointers[i] = (sbyte*)Marshal.StringToCoTaskMemUTF8(argv[i]);
        pointers[argv.Length] = null;
        return new _cef_main_args_t { argc = argv.Length, argv = pointers };
    }
#endif
}
