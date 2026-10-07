using System.Collections.Concurrent;
using System.Drawing;
using Microsoft.Extensions.Logging;
using Shenora.Core.Events;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// One splash, from the runner's start to its lift: the component's setup, its boot work, the frames its surfaces draw,
/// and the rule that lifts it — every <see cref="SplashContext.OnShown"/> finished AND the page's condition (the
/// handshake, or <c>closeSplash()</c> when held, or the timeout from the main window opening); or a
/// <see cref="Close"/>; or an <see cref="Abort"/>. Two surfaces: the optional card from <see cref="Start"/> until the
/// main window is on screen, and the splash over the main window's render area from <see cref="WindowOpened"/> until
/// the lift. Nothing here throws to its caller: a failure costs the splash, never the app.
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
    private readonly bool? _dark;   // the app's colour scheme, else the system's
    private readonly CancellationTokenSource _appStopping = new();
    private readonly ConcurrentDictionary<SplashImage, Size?> _imageSizes = new(SplashImageSource.Comparer);
    private readonly Lock _gate = new();
    private readonly Lock _renderGate = new();   // the card's thread and the window's may both draw for a moment
    private readonly ThreadLocal<SplashSurface?> _renderingFor = new();
    private SplashContext? _context;
    private Func<SplashElement>? _render;
    private SplashElement? _lastGood;
    private ISplashSurface? _card, _overlay;
    private volatile SplashSurface _drawing = SplashSurface.Card;
    private ITimer? _timeout;
    private long _startedAt;
    private bool _started, _bootDone, _windowOpened, _pageReady, _released, _timedOut, _lifted, _renderFailureLogged;

    public SplashSession(ChromiumSplashOptions options, string? title, Color? windowBackground, IServiceProvider services, IEventBus? bus,
        Func<ISplashSurface?> surfaces, TimeProvider time, ILogger? log, bool? systemDark, ColorScheme scheme = ColorScheme.System)
    {
        _options = options;
        _title = title;
        _dark = ChromiumColorSchemes.Dark(scheme, systemDark);
        _background = options.Background ?? windowBackground ?? (_dark == false ? Light : Dark);
        _services = services;
        _bus = bus;
        _surfaces = surfaces;
        _time = time;
        _log = log;
        _systemDark = systemDark;
    }

    /// <summary>Raised once, after the lift (on the thread that lifted it): the main window's kit strip ends with it.</summary>
    public event Action? Lifted;

    /// <summary>A window is up and has not lifted.</summary>
    public bool IsShowing
    {
        get
        {
            lock (_gate) return (_card ?? _overlay) is not null && !_lifted;
        }
    }

    /// <summary>It has lifted, or never could show (its setup failed).</summary>
    public bool HasLifted
    {
        get
        {
            lock (_gate) return _lifted;
        }
    }

    // Inside a render, the surface that render is for; anywhere else, the phase the splash is in.
    SplashSurface ISplashSessionSink.Surface => _renderingFor.Value ?? _drawing;

    /// <summary>Set up the component, show the card when there is one, and start the boot work. Once; the runner's
    /// thread. <paramref name="workAreas"/>, the displays' work areas in DIP with the primary first, place the card.</summary>
    public void Start(ChromiumWindowGeometry.Plan placement, IReadOnlyList<Rectangle> workAreas)
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }
        _startedAt = _time.GetTimestamp();
        if (_options.Card is null) _drawing = SplashSurface.Window;
        var context = new SplashContext(_services, _systemDark, _dark, _bus, this, _log);
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
        else if (_options.Card is { } card) ShowCard(SplashGeometry.CardRect(placement, workAreas, card.Width, card.Height));
        StartWork(work);
    }

    private void ShowCard(Rectangle dipRect)
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
            surface.ShowCard(dipRect, RenderFor(SplashSurface.Card));
            lock (_gate)
            {
                if (!_lifted && !_windowOpened)
                {
                    _card = surface;
                    surface = null;
                }
            }
            AppCallback.Log(_log, () => "[Shenora.Chromium] Splash card shown");
        }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] The splash card could not be shown; the splash waits for the window", LogLevel.Warning, ex);
        }
        finally
        {
            if (surface is not null) AppCallback.Run(surface.Dispose);
        }
    }

    private SplashRender RenderFor(SplashSurface surface) => (windowPx, scale, measurer) => Render(windowPx, scale, measurer, surface);

    // The surface's thread. One render at a time, each reading its own surface from the context; the component's own
    // failure keeps the last tree it drew.
    private SplashFrame Render(Size windowPx, float scale, ISplashTextMeasurer measurer, SplashSurface surface)
    {
        SplashElement tree;
        lock (_renderGate)
        {
            _renderingFor.Value = surface;
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
            finally
            {
                _renderingFor.Value = null;
            }
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

    /// <summary>The main window exists: the splash opens over its render area. CEF's UI thread, before the window
    /// shows: an overlay owned later would let CEF's show raise the main window over it.</summary>
    public void WindowOpened(nint mainWindow, SplashOverlayLayout layout)
    {
        bool show;
        lock (_gate)
        {
            if (_windowOpened) return;
            _windowOpened = true;
            show = !_lifted && _render is not null;
        }
        _drawing = SplashSurface.Window;
        if (show) ShowOver(mainWindow, layout);
        StartTimeout();
        _context?.RaiseWindowOpened();
        Evaluate();
    }

    private void ShowOver(nint mainWindow, SplashOverlayLayout layout)
    {
        ISplashSurface? surface = null;
        try
        {
            surface = _surfaces();
            if (surface is null)
            {
                AppCallback.Log(_log, () => "[Shenora.Chromium] No splash: this platform has no display to draw one on", LogLevel.Warning);
                return;
            }
            surface.ShowOver(mainWindow, layout, RenderFor(SplashSurface.Window));
            lock (_gate)
            {
                if (!_lifted)
                {
                    _overlay = surface;
                    surface = null;
                }
            }
            AppCallback.Log(_log, () => "[Shenora.Chromium] Splash shown over the main window");
        }
        catch (Exception ex)
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] The splash could not show over the main window; the app starts without it", LogLevel.Warning, ex);
        }
        finally
        {
            if (surface is not null) AppCallback.Run(surface.Dispose);
        }
    }

    /// <summary>The main window is on screen: the splash shows over it, and once it does, the card, if any, goes. CEF's
    /// UI thread; returns at once.</summary>
    public void WindowShown()
    {
        ISplashSurface? overlay;
        lock (_gate) overlay = _lifted ? null : _overlay;
        if (overlay is null)
        {
            CardGone();
            return;
        }
        var revealed = AppCallback.RunOrDefault(() => { overlay.Reveal(CardGone); return true; }, false,
            ex => AppCallback.Log(_log, () => "[Shenora.Chromium] The splash could not show over the main window", LogLevel.Warning, ex));
        if (!revealed) CardGone();
    }

    // The card is no longer needed. Any thread; once.
    private void CardGone()
    {
        ISplashSurface? card;
        lock (_gate)
        {
            card = _card;
            _card = null;
        }
        if (card is not null)
            AppCallback.Run(card.Dispose, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] Closing the splash card failed", LogLevel.Warning, ex));
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

    /// <summary>The main window drew its first frame: the splash leaves it the part of the frame it covered until then.
    /// Any thread.</summary>
    public void WindowPainted()
    {
        ISplashSurface? surface;
        lock (_gate) surface = _lifted ? null : _overlay;
        if (surface is not null)
            AppCallback.Run(surface.Uncover, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] The splash could not uncover the window", LogLevel.Warning, ex));
    }

    /// <summary>The main window moved, resized, maximized or changed DPI.</summary>
    public void OwnerMoved()
    {
        ISplashSurface? surface;
        lock (_gate) surface = _lifted ? null : _overlay;
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
        ISplashSurface? card, overlay;
        lock (_gate)
        {
            if (_lifted) return;
            card = _card;
            overlay = _overlay;
        }
        if (card is not null) AppCallback.Run(card.Invalidate);
        if (overlay is not null) AppCallback.Run(overlay.Invalidate);
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
        ISplashSurface? card, overlay;
        lock (_gate)
        {
            if (_lifted) return;
            _lifted = true;
            card = _card;
            overlay = _overlay;
            _card = null;
            _overlay = null;
        }
        _timeout?.Dispose();
        _context?.Lifted();
        // Raised once, and let go of: the handlers hold the main windows that subscribed.
        var lifted = Interlocked.Exchange(ref Lifted, null);
        if (lifted is not null) AppCallback.Run(lifted, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] A splash-lifted hook failed", LogLevel.Warning, ex));
        // The card goes at once: only the window's splash fades, into the page.
        if (card is not null)
            AppCallback.Run(card.Dispose, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] Closing the splash card failed", LogLevel.Warning, ex));
        if (overlay is null) return;
        AppCallback.Log(_log, () => $"[Shenora.Chromium] Splash lifted: {why}");
        if (fade && _options.FadeOut > TimeSpan.Zero)
            AppCallback.Run(() => overlay.FadeOut(_options.FadeOut, overlay.Dispose), ex =>
            {
                AppCallback.Log(_log, () => "[Shenora.Chromium] The splash's fade failed", LogLevel.Warning, ex);
                AppCallback.Run(overlay.Dispose);
            });
        else
            AppCallback.Run(overlay.Dispose, ex => AppCallback.Log(_log, () => "[Shenora.Chromium] Closing the splash failed", LogLevel.Warning, ex));
    }
}
