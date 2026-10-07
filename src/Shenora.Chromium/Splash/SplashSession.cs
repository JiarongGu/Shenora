using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Shenora.Core.Events;

namespace Shenora.Chromium.Host;

/// <summary>
/// One splash, from the runner's start to its lift: the component's setup, its boot work, the frames its surface draws,
/// and the rule that lifts it — every <see cref="SplashContext.OnShown"/> finished AND the page's condition (the
/// handshake, or <c>closeSplash()</c> when held, or the timeout from the main window opening); or a
/// <see cref="Close"/>; or an <see cref="Abort"/>. Nothing here throws to its caller: a failure costs the splash, never
/// the app.
/// </summary>
internal sealed class SplashSession : ISplashSessionSink, IDisposable
{
    private static readonly Color Dark = Color.FromArgb(0x1F, 0x1F, 0x1F), Light = Color.FromArgb(0xF3, 0xF3, 0xF3);
    private static readonly TimeSpan AnimationPeriod = TimeSpan.FromMilliseconds(1600);

    private readonly ChromiumSplashOptions _options;
    private readonly string? _title;
    private readonly Color _background;
    private readonly IServiceProvider _services;
    private readonly IEventBus? _bus;
    private readonly Func<ISplashSurface?> _surfaces;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly bool? _systemDark;
    private readonly CancellationTokenSource _appStopping = new();
    private readonly ConcurrentDictionary<SplashImage, Size?> _imageSizes = new(SplashImageSource.Comparer);
    private readonly Lock _gate = new();
    private SplashContext? _context;
    private Func<SplashElement>? _render;
    private SplashElement? _lastGood;
    private ISplashSurface? _surface;
    private ITimer? _timeout;
    private long _startedAt;
    private bool _started, _bootDone, _windowOpened, _pageReady, _released, _timedOut, _lifted, _renderFailureLogged;

    public SplashSession(ChromiumSplashOptions options, string? title, Color? windowBackground, IServiceProvider services, IEventBus? bus,
        Func<ISplashSurface?> surfaces, TimeProvider time, ILogger? log, bool? systemDark)
    {
        _options = options;
        _title = title;
        _background = options.Background ?? windowBackground ?? (systemDark == false ? Light : Dark);
        _services = services;
        _bus = bus;
        _surfaces = surfaces;
        _time = time;
        _log = log;
        _systemDark = systemDark;
    }

    /// <summary>A window is up and has not lifted.</summary>
    public bool IsShowing
    {
        get
        {
            lock (_gate) return _surface is not null && !_lifted;
        }
    }

    /// <summary>Set up the component, show its first frame, and start its boot work. Once; the runner's thread.</summary>
    public void Start(ChromiumWindowGeometry.Plan placement)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _startedAt = _time.GetTimestamp();
        var context = new SplashContext(_services, _systemDark, _bus, this, _log);
        _context = context;
        try
        {
            _render = (_options.Component ?? Splash.Preset(title: _title))(context);
        }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] The splash's setup threw; the app starts without a splash", LogLevel.Error, ex);
        }
        var work = context.TakeShownWork();
        if (_render is null) Lift("its setup failed", fade: false);
        else Show(placement);
        StartWork(work);
    }

    private void Show(ChromiumWindowGeometry.Plan placement)
    {
        ISplashSurface? surface = null;
        try
        {
            // Closed during its own setup: no window at all, rather than one that flashes up and takes the foreground.
            lock (_gate)
                if (_lifted) return;
            surface = _surfaces();
            if (surface is null)
            {
                AppCallback.Log(_log, () => "[Shenora.Chromium] No splash: this platform has no display to draw one on", LogLevel.Warning);
                return;
            }
            surface.Show(placement, Render);
            lock (_gate)
            {
                if (!_lifted)
                {
                    _surface = surface;
                    surface = null;
                }
            }
            AppCallback.Log(_log, () => "[Shenora.Chromium] Splash shown");
        }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] The splash window could not be shown; the app starts without it", LogLevel.Warning, ex);
        }
        finally
        {
            if (surface is not null) AppCallback.Run(surface.Dispose);
        }
    }

    // The surface's thread. The component's own failure keeps the last tree it drew.
    private SplashFrame Render(Size windowPx, float scale, ISplashTextMeasurer measurer)
    {
        SplashElement tree;
        try
        {
            tree = _render!();
            _lastGood = tree;
        }
        catch (Exception ex)
        {
            if (!_renderFailureLogged)
            {
                _renderFailureLogged = true;
                AppCallback.Log(_log, () => "[Shenora.Chromium] The splash's render threw; it keeps its last frame", LogLevel.Warning, ex);
            }
            tree = _lastGood ?? new SplashStack();
        }
        var elapsed = _time.GetElapsedTime(_startedAt).TotalMilliseconds / AnimationPeriod.TotalMilliseconds;
        return SplashLayout.Build(tree, _background, windowPx, scale, measurer, elapsed - Math.Floor(elapsed), ImageSize);
    }

    private Size? ImageSize(SplashImage image)
    {
        if (_imageSizes.Count >= SplashImageSource.Capacity) _imageSizes.Clear();
        return _imageSizes.GetOrAdd(image, SplashPng.ReadSize);
    }

    private void StartWork(IReadOnlyList<Func<CancellationToken, Task>> work)
    {
        if (work.Count == 0)
        {
            BootDone();
            return;
        }
        var token = _appStopping.Token;
        var running = work.Select(w => Task.Run(async () =>
        {
            try
            {
                await w(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                AppCallback.Log(_log, () => "[Shenora.Chromium] The splash's boot work threw; the splash no longer waits on it", LogLevel.Error, ex);
            }
        }, CancellationToken.None)).ToArray();
        _ = Task.WhenAll(running).ContinueWith(_ => BootDone(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void BootDone()
    {
        lock (_gate) _bootDone = true;
        Evaluate();
    }

    /// <summary>The main window exists. CEF's UI thread, before the window shows.</summary>
    public void WindowOpened(nint mainWindow)
    {
        ISplashSurface? surface;
        lock (_gate)
        {
            if (_windowOpened) return;
            _windowOpened = true;
            surface = _lifted ? null : _surface;
        }
        // Attached FIRST: CEF shows the window as this returns, and an unowned splash would end up under it.
        if (surface is not null)
            AppCallback.Run(() => surface.Attach(mainWindow),
                ex => AppCallback.Log(_log, () => "[Shenora.Chromium] The splash could not attach to the main window", LogLevel.Warning, ex));
        StartTimeout();
        _context?.RaiseWindowOpened();
        Evaluate();
    }

    // A timeout past what a timer can hold (about 49 days) is as good as none.
    private static readonly TimeSpan LongestTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private void StartTimeout()
    {
        var due = _options.Timeout > LongestTimer ? Timeout.InfiniteTimeSpan : _options.Timeout;
        var timer = AppCallback.RunOrDefault(() => _time.CreateTimer(_ => TimedOut(), null, due, Timeout.InfiniteTimeSpan), null,
            ex => AppCallback.Log(_log, () => "[Shenora.Chromium] The splash's timeout could not be set", LogLevel.Warning, ex));
        lock (_gate)
        {
            if (!_lifted)
            {
                _timeout = timer;
                return;
            }
        }
        timer?.Dispose();
    }

    /// <summary>The main window moved or resized.</summary>
    public void OwnerMoved()
    {
        ISplashSurface? surface;
        lock (_gate) surface = _lifted ? null : _surface;
        if (surface is not null) AppCallback.Run(surface.FollowOwner);
    }

    /// <summary>The main window's page completed its ready handshake.</summary>
    public void PageReady()
    {
        lock (_gate) _pageReady = true;
        _context?.RaisePageReady();
        Evaluate();
    }

    /// <summary>The page asked to lift it (<c>closeSplash()</c>).</summary>
    public void Release()
    {
        lock (_gate) _released = true;
        Evaluate();
    }

    /// <summary>Lift it now, fading.</summary>
    public void Close() => Lift("it was closed", fade: true);

    void ISplashSessionSink.Invalidate()
    {
        ISplashSurface? surface;
        lock (_gate) surface = _lifted ? null : _surface;
        if (surface is not null) AppCallback.Run(surface.Invalidate);
    }

    /// <summary>Destroy it now, with no fade: the loop ended, the app failed to start, or the main window went.</summary>
    public void Abort() => Lift("the app stopped", fade: false);

    /// <summary>The app is stopping: the boot work's token is cancelled.</summary>
    public void AppStopping() => AppCallback.Run(_appStopping.Cancel,
        ex => AppCallback.Log(_log, () => "[Shenora.Chromium] A splash boot work's cancellation callback threw", LogLevel.Warning, ex));

    public void Dispose()
    {
        AppStopping();
        Abort();
    }

    private void TimedOut()
    {
        bool waitingOnBoot;
        lock (_gate)
        {
            _timedOut = true;
            waitingOnBoot = !_bootDone && !_lifted;
        }
        // The timeout stands in for the page only, so a splash still up now is waiting on the app's own boot work.
        if (waitingOnBoot)
            AppCallback.Log(_log, () => $"[Shenora.Chromium] The splash's timeout ({_options.Timeout}) has passed and it still waits on its OnShown work",
                LogLevel.Warning);
        Evaluate();
    }

    private void Evaluate()
    {
        string? why = null;
        lock (_gate)
        {
            if (_lifted || !_started || !_bootDone) return;
            if (_released) why = "the page closed it";
            else if (_pageReady && !_options.HoldUntilClosed) why = "the page is ready";
            else if (_timedOut) why = "the page did not say it was ready in time";
        }
        if (why is not null) Lift(why, fade: true);
    }

    private void Lift(string why, bool fade)
    {
        ISplashSurface? surface;
        lock (_gate)
        {
            if (_lifted) return;
            _lifted = true;
            surface = _surface;
            _surface = null;
        }
        _timeout?.Dispose();
        _context?.Lifted();
        if (surface is null) return;
        AppCallback.Log(_log, () => $"[Shenora.Chromium] Splash lifted: {why}");
        if (fade && _options.FadeOut > TimeSpan.Zero)
            AppCallback.Run(() => surface.FadeOut(_options.FadeOut, surface.Dispose), ex =>
            {
                AppCallback.Log(_log, () => "[Shenora.Chromium] The splash's fade failed", LogLevel.Warning, ex);
                AppCallback.Run(surface.Dispose);
            });
        else
            AppCallback.Run(surface.Dispose, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] Closing the splash failed", LogLevel.Warning, ex));
    }
}
