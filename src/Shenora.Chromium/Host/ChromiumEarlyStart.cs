using System.Runtime.ExceptionServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// CEF started while the app is still being composed (D87). The first frame waits on Chromium's GPU process, which
/// starts only once CEF does and takes hundreds of milliseconds to set up, while the window, the page and the app's
/// own start finish inside that wait: so the time the app spends being built before CEF starts is time the first frame
/// comes later. <see cref="ChromiumHostExtensions.UseChromium"/> starts CEF here when the app runs from its layout, and
/// <see cref="ChromiumRunner"/> takes it over; run from anywhere else, a test host included, the runner starts CEF.
/// </summary>
internal sealed class ChromiumEarlyStart
{
    /// <summary>The process's one: CEF starts once per process.</summary>
    public static readonly ChromiumEarlyStart Process = new();

    /// <summary>
    /// This process is the app run from its layout, the only case CEF starts early in. On Windows, started through
    /// CEF's launcher and the kit's shim, which leave the sandbox; on macOS, from its bundle, the only place the shell
    /// runs; on Linux, with the helper the build laid out beside it. Not a test host, and not the app's dll run by
    /// <c>dotnet</c>, as an IDE may.
    /// </summary>
    public static bool LaunchedFromLayout =>
#if CEF_WINDOWS
        CefStartup.Sandbox != 0;
#elif CEF_MACOS
        Directory.Exists(MacPlatform.Framework);
#elif CEF_LINUX
        File.Exists(LinuxPlatform.Helper) && File.Exists(LinuxPlatform.Library);
#else
        false;
#endif

    private readonly Lock _gate = new();
    private ChromiumHostOptions? _options;
    private int _thread;
    private object? _app;   // the ChromiumApp, which CEF holds for the life of the process
    private ExceptionDispatchInfo? _failure;
    private bool _contextReady;
    private Action? _started;

    /// <summary>True when CEF started in this process as an app was composed.</summary>
    public bool HasStarted
    {
        get { lock (_gate) return _options is not null; }
    }

    /// <summary>Start CEF for <paramref name="options"/> on this thread with <paramref name="settings"/>.</summary>
    public void Start(ChromiumHostOptions options, CefStartup.Settings settings) =>
        Start(options, contextInitialized =>
        {
            var app = new ChromiumApp(contextInitialized);
            CefStartup.Initialize(app, settings);
            return app;
        });

    /// <summary>
    /// Start on this thread, once: <paramref name="initialize"/> starts CEF with the callback its context runs, and
    /// returns what CEF must keep alive. Never throws: a CEF that will not start is reported by
    /// <see cref="ShenoraApplication.Run"/>, as it was when the runner started it.
    /// </summary>
    internal void Start(ChromiumHostOptions options, Func<Action, object> initialize)
    {
        lock (_gate)
        {
            if (_options is not null) return;
            _options = options;
            _thread = Environment.CurrentManagedThreadId;
        }
        try
        {
            _app = initialize(ContextInitialized);
        }
        catch (Exception ex)
        {
            _failure = ExceptionDispatchInfo.Capture(ex);
        }
    }

    /// <summary>
    /// The runner's side. False when CEF did not start early, and the runner starts it. True when it did: CEF runs, and
    /// <paramref name="started"/> runs once its context exists, which is at once unless CEF has yet to say so.
    /// </summary>
    /// <exception cref="InvalidOperationException">CEF started early with other options, or on another thread,
    /// or would not start (the exception the start threw).</exception>
    public bool TakeOver(ChromiumHostOptions options, Action started)
    {
        lock (_gate)
            if (_options is null) return false;
        if (!ReferenceEquals(_options, options))
            throw new InvalidOperationException(
                "Chromium already started in this process with other options: the first UseChromium starts it, once per process.");
        if (_thread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException(
                "The Chromium shell runs on the thread that composed it: UseChromium started Chromium on that thread, and CEF's loop must run there.");
        _failure?.Throw();
        if (_contextReady) started();
        else _started = started;
        return true;
    }

    // CEF's UI thread, which is the composing thread.
    private void ContextInitialized()
    {
        _contextReady = true;
        var started = _started;
        _started = null;
        started?.Invoke();
    }
}
