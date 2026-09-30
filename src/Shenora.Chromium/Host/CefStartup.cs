using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// Starting CEF, which both of its hosts do the same way (D83): the Views shell on the main thread's loop, and a host
/// that owns its UI thread (<see cref="ChromiumEngine"/>) on a loop of CEF's own.
/// <list type="number">
/// <item>The sandbox: from the shim when CEF's bootstrap started the app (D82), else none. Then the subprocesses run
/// through the launcher the build laid out beside the app, when there is one (<see cref="LauncherBesideApp"/>), and
/// otherwise THIS exe is every CEF subprocess too, which is what <see cref="ExecuteIfSubprocess"/> answers first.</item>
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
    internal sealed record Settings(string Cache, bool IsDevelopment, int DevToolsPort, uint? BackgroundColor, bool MultiThreadedLoop)
    {
        /// <summary>A debug port open in production too. Only for a process that holds no app page (D86): the port
        /// reaches every page in its process, the bridge's included.</summary>
        public int PagelessDebugPort { get; init; }

        /// <summary>Keep cookies that have no expiry across a restart.</summary>
        public bool PersistSessionCookies { get; init; }

        /// <summary>The locale; null is the OS's UI language, as WebView2 follows it (CEF's own default is en-US on
        /// Windows and macOS whatever the OS speaks).</summary>
        public string? Locale { get; init; }

        /// <summary>The profile's folder under <see cref="Cache"/>. Chrome's own windows always use <c>Default</c>
        /// (measured by an adopter, CEF 152), so a process whose windows are Chrome's names it so.</summary>
        public string Profile { get; init; } = "default";
    }

    /// <summary>The sandbox the shim created, or 0 when the app was not started through CEF's launcher.</summary>
    public static nint Sandbox => RuntimePointer("Shenora.Chromium.SandboxInfo");

    /// <summary>
    /// CEF's first call, and a subprocess's whole life: when this process is one of CEF's, it runs as that and returns
    /// its exit code, which the caller exits with. Otherwise -1. A process is one only when the app runs an exe of its
    /// own with no launcher beside it: through the shim, or through the build's layout, CEF's subprocesses never run
    /// .NET.
    /// </summary>
    public static int ExecuteIfSubprocess(ChromiumApp app, ILogger? log)
    {
        SelectApiVersion();
#if CEF_MACOS
        // Every subprocess runs the bundle's native helper, never .NET, and sandboxes itself there.
        _ = app;
        _ = log;
        return -1;
#else
        if (Sandbox != 0) return -1;   // the shim ran every subprocess itself
        var args = MainArgs(RuntimePointer("Shenora.Chromium.Instance"));
        var code = Cef.cef_execute_process(&args, app.ForCef(), null);
        if (code < 0) AppCallback.Log(log, () => "[Shenora.Chromium] Running without CEF's bootstrap launcher: Chromium's sandbox is OFF", LogLevel.Warning);
        return code;
#endif
    }

    /// <summary>Initialize CEF. Throws when it will not start, naming its log.</summary>
    public static void Initialize(ChromiumApp app, Settings settings)
    {
        // A host started through the shim may skip ExecuteIfSubprocess, and without this the app crashed at start
        // (0x80000003, measured).
        SelectApiVersion();
        Directory.CreateDirectory(settings.Cache);
        var profile = Path.Combine(settings.Cache, settings.Profile);
        var logFile = Path.Combine(settings.Cache, "cef.log");
        var sandbox = Sandbox;
        var args = MainArgs(RuntimePointer("Shenora.Chromium.Instance"));
        // Started without the launcher (`dotnet <App>.App.dll`, as an IDE may), CEF would start each subprocess as
        // `dotnet.exe --type=…` with no app to run, and every one exits at once (measured: the GPU process and the
        // network service died on every restart until the app crashed). The launcher the build laid out beside the
        // app runs them natively instead. Unsandboxed, as the app itself is on this path.
        var subprocess = sandbox == 0
            ? LauncherBesideApp(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, AppContext.BaseDirectory, Environment.ProcessPath, File.Exists)
            : null;
#if CEF_MACOS
        // The bundle's helper, which sandboxes each subprocess itself: the browser process is never sandboxed there.
        subprocess = MacPlatform.Helper;
        var noSandbox = 0;
        var framework = MacPlatform.Framework;
        var bundle = Path.GetDirectoryName(MacPlatform.Contents) ?? MacPlatform.Contents;
#elif CEF_LINUX
        // The helper beside the app runs every subprocess natively, and Chromium sandboxes them itself (the zygote's
        // namespaces, or chrome-sandbox). Without it (`dotnet <App>.App.dll`) this exe is every subprocess, unsandboxed.
        if (File.Exists(LinuxPlatform.Helper)) subprocess = LinuxPlatform.Helper;
        var noSandbox = subprocess is null ? 1 : 0;
        var framework = "";
        var bundle = "";
#else
        var noSandbox = sandbox == 0 ? 1 : 0;
        var framework = "";
        var bundle = "";
#endif
        subprocess ??= "";
        var cef = new _cef_settings_t
        {
            size = (nuint)sizeof(_cef_settings_t),
            no_sandbox = noSandbox,
            multi_threaded_message_loop = settings.MultiThreadedLoop ? 1 : 0,
            command_line_args_disabled = settings.IsDevelopment ? 0 : 1,
            remote_debugging_port = settings.PagelessDebugPort > 0 ? settings.PagelessDebugPort
                : settings.IsDevelopment ? settings.DevToolsPort : 0,
            persist_session_cookies = settings.PersistSessionCookies ? 1 : 0,
            log_severity = cef_log_severity_t.LOGSEVERITY_WARNING,
        };
        if (settings.BackgroundColor is { } color) cef.background_color = color;
        // A .NET culture name as it stands: CEF resolves it to the nearest locale it carries (zh-Hans-CN to zh-CN, en-AU
        // to en-GB, es-MX to es-419, measured), and to en-US when that one's locale files were not laid out.
        var locale = settings.Locale ?? System.Globalization.CultureInfo.CurrentUICulture.Name;

        int initialized;
        fixed (char* c = settings.Cache)
        fixed (char* p = profile)
        fixed (char* l = logFile)
        fixed (char* s = subprocess)
        fixed (char* f = framework)
        fixed (char* b = bundle)
        fixed (char* lc = locale)
        {
            cef.root_cache_path = CefStrings.View(c, settings.Cache.Length);
            cef.cache_path = CefStrings.View(p, profile.Length);
            cef.log_file = CefStrings.View(l, logFile.Length);
            if (locale.Length > 0) cef.locale = CefStrings.View(lc, locale.Length);
            if (subprocess.Length > 0) cef.browser_subprocess_path = CefStrings.View(s, subprocess.Length);
            if (framework.Length > 0) cef.framework_dir_path = CefStrings.View(f, framework.Length);
            if (bundle.Length > 0) cef.main_bundle_path = CefStrings.View(b, bundle.Length);
            initialized = Cef.cef_initialize(&args, &cef, app.ForCef(), (void*)sandbox);
        }
        if (initialized == 0)
            throw new InvalidOperationException($"Chromium did not start (CEF exit code {Cef.cef_get_exit_code()}). Its log is {logFile}.");
    }

    /// <summary>
    /// The app's launcher, laid out by the build as <c>&lt;App&gt;.exe</c> beside the app's own <c>&lt;App&gt;.App</c>
    /// assembly: CEF's bootstrap, which runs a subprocess natively through the kit's shim. Only when the running exe is
    /// NOT in the app's folder, which is <c>dotnet.exe</c> running the app's dll: an app running an exe of its own there
    /// is one CEF can start for each subprocess, and an unrelated <c>&lt;App&gt;.exe</c> beside it must not be. Null
    /// otherwise, when the app is not laid out that way, or not on Windows.
    /// </summary>
    internal static string? LauncherBesideApp(string? entryAssembly, string baseDirectory, string? processPath, Func<string, bool> exists)
    {
        if (!OperatingSystem.IsWindows() || entryAssembly is not { Length: > 4 } name || !name.EndsWith(".App", StringComparison.OrdinalIgnoreCase))
            return null;
        if (processPath is not null && SameFolder(Path.GetDirectoryName(processPath), baseDirectory)) return null;
        var launcher = Path.Combine(baseDirectory, name[..^4] + ".exe");
        return exists(launcher) ? launcher : null;
    }

    private static bool SameFolder(string? a, string b) =>
        a is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

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
#if CEF_MACOS
            MacPlatform.Prepare();
#elif CEF_LINUX
            LinuxPlatform.Prepare();
#endif
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
#if CEF_LINUX
        if (argv.Length > 0) argv[0] = LinuxPlatform.Executable;
#endif
        var pointers = (sbyte**)NativeMemory.Alloc((nuint)(argv.Length + 1), (nuint)sizeof(sbyte*));
        for (var i = 0; i < argv.Length; i++) pointers[i] = (sbyte*)Marshal.StringToCoTaskMemUTF8(argv[i]);
        pointers[argv.Length] = null;
        return new _cef_main_args_t { argc = argv.Length, argv = pointers };
    }
#endif
}
