#if CEF_LINUX
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using static Shenora.Chromium.Host.LinuxSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// Xlib as the splash uses it: threads initialised before its first call (two threads open connections), and an error
/// handler for its own connections. Xlib's default handler EXITS the process, and the splash names a window it does not
/// own (the main one), which may be gone by the time a request about it reaches the server.
/// </summary>
internal static unsafe class SplashX11
{
    private static readonly ConcurrentDictionary<nint, byte> Ours = new();
    private static nint _previous;
    private static int _initialised;

    public static void Init()
    {
        if (Interlocked.Exchange(ref _initialised, 1) == 1) return;
        XInitThreads();
        _previous = XSetErrorHandler(&OnError);
    }

    /// <summary>A connection of the splash's own, whose errors the splash answers; null when there is no display.</summary>
    public static nint Open()
    {
        Init();
        var display = XOpenDisplay(0);
        if (display != 0) Ours[display] = 0;
        return display;
    }

    public static void Close(nint display)
    {
        if (display == 0) return;
        XCloseDisplay(display);
        Ours.TryRemove(display, out _);
    }

    // The splash's own requests fail only when a window went away under them: nothing to do. Anyone else's go on to the
    // handler that was there before (Xlib's own, or a toolkit's).
    [UnmanagedCallersOnly]
    private static int OnError(nint display, nint error)
    {
        if (Ours.ContainsKey(display)) return 0;
        return _previous != 0 ? ((delegate* unmanaged<nint, nint, int>)_previous)(display, error) : 0;
    }
}

/// <summary>
/// The X11 desktop as the splash needs it before CEF can describe it: the first desktop's work area (<c>_NET_WORKAREA</c>,
/// else the whole screen) in pixels, and the scale Chromium itself takes from <c>Xft.dpi</c>.
/// </summary>
internal static unsafe class LinuxScreens
{
    /// <summary>Null when there is no X display to reach (no <c>DISPLAY</c>, or it refuses).</summary>
    public static (Rectangle Work, float Scale)? Read()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        var display = SplashX11.Open();
        if (display == 0) return null;
        try { return Read(display); }
        finally { SplashX11.Close(display); }
    }

    public static (Rectangle Work, float Scale) Read(nint display)
    {
        var screen = XDefaultScreen(display);
        var root = XRootWindow(display, screen);
        var work = new Rectangle(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen));
        if (XGetWindowProperty(display, root, XInternAtom(display, "_NET_WORKAREA", 0), 0, 4, 0, 6 /* CARDINAL */,
                out _, out var format, out var items, out _, out var data) == 0 && data != 0)
        {
            if (format == 32 && items >= 4)
            {
                var v = (long*)data;   // format 32 arrives as C longs
                work = new Rectangle((int)v[0], (int)v[1], (int)v[2], (int)v[3]);
            }
            XFree(data);
        }
        return (work, Scale(display));
    }

    private static float Scale(nint display)
    {
        var resources = Marshal.PtrToStringUTF8(XResourceManagerString(display));
        foreach (var line in (resources ?? "").Split('\n'))
            if (line.StartsWith("Xft.dpi:", StringComparison.Ordinal)
                && float.TryParse(line["Xft.dpi:".Length..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) && dpi > 0)
                return Math.Max(1, dpi / 96f);
        return 1;
    }

    public static IReadOnlyList<Rectangle> WorkAreasDip()
    {
        if (Read() is not var (work, scale)) return [];
        return [new Rectangle((int)(work.X / scale), (int)(work.Y / scale), (int)(work.Width / scale), (int)(work.Height / scale))];
    }

    public static Rectangle ToPixels(ChromiumWindowGeometry.Plan plan, Rectangle work, float scale)
    {
        if (plan.Maximized) return work;
        int width = (int)Math.Round(plan.Width * scale), height = (int)Math.Round(plan.Height * scale);
        return plan is { X: { } x, Y: { } y }
            ? new Rectangle((int)Math.Round(x * scale), (int)Math.Round(y * scale), width, height)
            : new Rectangle(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height);
    }
}

/// <summary>
/// The Linux splash: an X11 window on a connection and thread of its own, so it draws and animates while CEF's thread is
/// busy. A managed window typed <c>_NET_WM_WINDOW_TYPE_SPLASH</c>, undecorated and never given the keyboard: either the
/// card, before the main window exists, or the splash over the main window's client window (the manager's frame is
/// outside it), transient for it from its creation and mapped once it is shown, so the window manager keeps it above it;
/// not override-redirect, which would float it over every other app for as long as a held splash lasts. It takes the
/// clicks over it. Frames go up with <c>XPutImage</c>, only where they changed; it fades through <c>_NET_WM_WINDOW_OPACITY</c>
/// where a compositor runs, and closes at once where none does.
/// </summary>
internal sealed unsafe class LinuxSplashSurface(ILogger? log) : ISplashSurface
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Lock _gate = new();   // the window and the wake pipe, against a caller on another thread
    private Thread? _thread;
    private Exception? _failure;
    private ChromiumWindowGeometry.Plan _plan;
    private nuint _overOwner;   // the main window the splash opens over; zero for the card
    private SplashOverlayLayout _layout;
    // A frameless window's strip, which this window covers and draws: its buttons' model, their state and places, the
    // press on its drag area, its height here, and whether the window is maximized (the restore glyph, no resize band).
    private CaptionButtons? _buttons;
    private CaptionButtonState _stripState = new(null, null);
    private CaptionButtonRect[] _stripButtons = [];
    private readonly StripGesture _gesture = new();
    private CaptionButtonPalette? _palette;
    private int _stripTop;
    private bool _ownerMaximized, _stripDirty;
    private nuint _netWmState;
    private SplashRender? _render;
    private LinuxSplashPainter? _painter;
    private nint _display, _visual, _gc, _image;
    private byte* _imageData;
    private Size _imageSize, _size;
    private int _depth;
    private nuint _window, _owner, _deleteWindow;
    private float _scale = 1;
    private int _wakeRead = -1, _wakeWrite = -1;
    private bool _animating, _fading, _closing;
    private long _fadeStart;
    private TimeSpan _fade;
    private Action? _fadeDone;
    private int _renderQueued;
    private int _disposed;

    public void ShowCard(Rectangle dipRect, SplashRender render)
    {
        _plan = new ChromiumWindowGeometry.Plan(dipRect.Width, dipRect.Height, dipRect.X, dipRect.Y, false);
        Start(render);
    }

    // CEF's UI thread, before CEF maps the main window. Synchronous: the transient-for hint is up as this returns, and a
    // manager stacks a transient above its owner from then on.
    public void ShowOver(nint mainWindow, SplashOverlayLayout layout, SplashRender render)
    {
        if (mainWindow == 0) throw new ArgumentException("The main window has no X window.", nameof(mainWindow));
        _overOwner = (nuint)mainWindow;
        _layout = layout;
        if (layout.Frameless)
        {
            // Fed and answered on the splash's thread: a state to draw, a click for the window.
            _buttons = new CaptionButtons(state =>
            {
                _stripState = state;
                _stripDirty = true;
                Present(whole: false);
            }, kind => layout.CaptionClicked?.Invoke(kind));
        }
        Start(render);
    }

    private void Start(SplashRender render)
    {
        _render = render;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shenora splash" };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The splash window did not open within 5 seconds.");
        if (_failure is { } failure) throw new InvalidOperationException("The splash window could not be opened.", failure);
    }

    private void Run()
    {
        try
        {
            Open();
        }
        catch (Exception ex)
        {
            _failure = ex;
            Close();
            _ready.Set();
            return;
        }
        _ready.Set();
        Loop();
        Close();
    }

    private void Open()
    {
        var fds = stackalloc int[2];
        // Close-on-exec, or the app's own child processes would inherit it; non-blocking, or a full pipe would stall a
        // caller.
        if (pipe2(fds, OCloexec | ONonblock) != 0) throw new InvalidOperationException("The splash's wake pipe could not be made.");
        _wakeRead = fds[0];
        _wakeWrite = fds[1];
        _display = SplashX11.Open();
        if (_display == 0) throw new InvalidOperationException("The X display could not be opened.");
        var screen = XDefaultScreen(_display);
        _visual = XDefaultVisual(_display, screen);
        _depth = XDefaultDepth(_display, screen);
        _gc = XDefaultGC(_display, screen);
        // The painter's bytes are 0xAARRGGBB words: what a 24-bit TrueColor visual takes and nothing else.
        var masks = (nuint*)((byte*)_visual + VisualRedMaskOffset);
        if (_depth != 24 || masks[0] != 0xFF0000 || masks[1] != 0xFF00 || masks[2] != 0xFF)
            throw new InvalidOperationException($"The X display's default visual is not 24-bit RGB (depth {_depth}); the splash draws in that only.");
        var (work, scale) = LinuxScreens.Read(_display);
        _scale = scale;
        var bounds = _overOwner != 0 && OwnerBounds(_overOwner) is { } over ? over : LinuxScreens.ToPixels(_plan, work, scale);
        _size = bounds.Size;
        _painter = new LinuxSplashPainter(log);
        var background = Render(bounds.Size);
        var window = XCreateSimpleWindow(_display, XRootWindow(_display, screen), bounds.X, bounds.Y, (uint)bounds.Width, (uint)bounds.Height, 0, 0, background);
        if (window == 0) throw new InvalidOperationException("The splash window could not be made.");
        lock (_gate) _window = window;
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(LinuxSplashSurface));
        Describe(window, bounds);
        // Its clicks, always: unselected, X passes a click up to the window's ancestors, which are the window manager's.
        // The pointer's moves too where it draws a strip.
        XSelectInput(_display, window, ExposureMask | StructureNotifyMask | ButtonPressMask | ButtonReleaseMask
            | (_buttons is null ? 0 : PointerMotionMask | LeaveWindowMask));
        if (_overOwner != 0)
        {
            // Over the main window: transient for it before it maps, and its structure events on this connection too: a
            // manager reads a transient hint naming a window it does not manage yet as nothing (openbox stacked the main
            // window over the splash as it mapped, measured), so the hint goes up again as it maps; its moves are
            // followed, and its end noticed.
            _owner = _overOwner;
            XSetTransientForHint(_display, window, _owner);
            // Its state too, where it draws the strip: a manager may change _NET_WM_STATE (maximized, fullscreen) after it
            // configures the window, or without configuring it at all.
            _netWmState = XInternAtom(_display, "_NET_WM_STATE", 0);
            XSelectInput(_display, _owner, StructureNotifyMask | (_buttons is null ? 0 : PropertyChangeMask));
            // Mapped by Reveal, once the main window is: mapped now, it would float alone until CEF showed its owner.
            XFlush(_display);
            return;
        }
        XMapWindow(_display, window);
        XMoveWindow(_display, window, bounds.X, bounds.Y);   // a manager may place a window as it maps it
        XFlush(_display);
    }

    // CEF's UI thread, the main window shown: the splash maps over it at its client area as it is then, on the splash's
    // own connection and thread. Made already (ShowOver waited for that), so this only posts.
    public void Reveal(Action shown)
    {
        var once = 0;
        void Shown()
        {
            if (Interlocked.Exchange(ref once, 1) == 0) shown();
        }
        if (_overOwner == 0 || _failure is not null)
        {
            Shown();
            return;
        }
        Post(() =>
        {
            try
            {
                if (_window == 0) return;
                XMapWindow(_display, _window);
                Restack();
                XFlush(_display);
            }
            finally
            {
                Shown();
            }
        });
        // Disposed meanwhile: the posted work never runs.
        if (Volatile.Read(ref _disposed) != 0) Shown();
    }

    // Where the splash goes over the main window, in root coordinates (SplashGeometry.LinuxOverlayRect), and how much of
    // a frameless window's strip is then in it; null if X cannot say.
    private Rectangle? OwnerBounds(nuint owner)
    {
        if (XGetGeometry(_display, owner, out _, out _, out _, out var width, out var height, out _, out _) == 0) return null;
        XTranslateCoordinates(_display, owner, XRootWindow(_display, XDefaultScreen(_display)), 0, 0, out var x, out var y, out _);
        var client = new Rectangle(x, y, (int)width, (int)height);
        var maximized = _layout.Frameless && Maximized(owner);
        if (maximized != _ownerMaximized) _stripDirty = true;   // the restore glyph
        _ownerMaximized = maximized;
        var over = SplashGeometry.LinuxOverlayRect(client, _layout.Frameless, maximized, (int)Math.Round(SplashGeometry.ResizeBandDips * _scale));
        _stripTop = _layout.Frameless ? Math.Max(0, (int)Math.Round(_layout.StripDips * _scale) - (over.Y - client.Y)) : 0;
        return over;
    }

    // The manager's word on it: both _NET_WM_STATE_MAXIMIZED atoms, or fullscreen, where Chromium keeps no resize band
    // either.
    private bool Maximized(nuint window)
    {
        if (XGetWindowProperty(_display, window, XInternAtom(_display, "_NET_WM_STATE", 0), 0, 64, 0, 4 /* ATOM */,
                out _, out var format, out var items, out _, out var data) != 0 || data == 0)
            return false;
        try
        {
            if (format != 32) return false;
            nuint horizontal = XInternAtom(_display, "_NET_WM_STATE_MAXIMIZED_HORZ", 0), vertical = XInternAtom(_display, "_NET_WM_STATE_MAXIMIZED_VERT", 0),
                fullscreen = XInternAtom(_display, "_NET_WM_STATE_FULLSCREEN", 0);
            var atoms = (long*)data;   // format 32 arrives as C longs
            bool h = false, v = false, f = false;
            for (nuint i = 0; i < items; i++)
            {
                h |= (nuint)atoms[i] == horizontal;
                v |= (nuint)atoms[i] == vertical;
                f |= (nuint)atoms[i] == fullscreen;
            }
            return (h && v) || f;
        }
        finally
        {
            XFree(data);
        }
    }

    // What a window manager reads: a splash, undecorated, placed where it says, never given the keyboard, of this app,
    // and one it may ask to close (WM_DELETE_WINDOW, which it ignores) rather than kill: openbox kills a window's whole
    // connection without it, and Xlib then exits the process.
    private void Describe(nuint window, Rectangle bounds)
    {
        var type = (long)XInternAtom(_display, "_NET_WM_WINDOW_TYPE_SPLASH", 0);
        XChangeProperty(_display, window, XInternAtom(_display, "_NET_WM_WINDOW_TYPE", 0), 4 /* ATOM */, 32, PropModeReplace, &type, 1);
        var motif = stackalloc long[5] { 2 /* decorations */, 0, 0, 0, 0 };
        var motifAtom = XInternAtom(_display, "_MOTIF_WM_HINTS", 0);
        XChangeProperty(_display, window, motifAtom, motifAtom, 32, PropModeReplace, motif, 5);
        var size = new XSizeHints { Flags = 1 | 2 | 4 | 8, X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
        XSetWMNormalHints(_display, window, &size);
        var hints = new XWMHints { Flags = 1 /* input */, Input = 0 };
        XSetWMHints(_display, window, &hints);
        var delete = _deleteWindow = XInternAtom(_display, "WM_DELETE_WINDOW", 0);
        XSetWMProtocols(_display, window, &delete, 1);
        var name = Marshal.StringToCoTaskMemUTF8(LinuxPlatform.ProgramName);
        var cls = Marshal.StringToCoTaskMemUTF8(LinuxPlatform.ProgramClass);
        try
        {
            var classHint = new XClassHint { Name = name, Class = cls };
            XSetClassHint(_display, window, &classHint);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
            Marshal.FreeCoTaskMem(cls);
        }
    }

    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            Post(() =>
            {
                Volatile.Write(ref _renderQueued, 0);
                Present(whole: false);
            });
    }

    public void FollowOwner() => Post(Snap);

    // It covers nothing of the window beyond the render area until its first frame (Linux draws the strip until the lift).
    public void Uncover() { }

    public void FadeOut(TimeSpan duration, Action done) => Post(() =>
    {
        _fadeDone = done;
        // Without a compositor the opacity property does nothing, and a fade would only hold the splash up longer.
        if (XGetSelectionOwner(_display, XInternAtom(_display, "_NET_WM_CM_S0", 0)) == 0)
        {
            FadeDone();
            return;
        }
        _fade = duration;
        _fadeStart = Stopwatch.GetTimestamp();
        _fading = true;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_gate)
            if (_window == 0) return;   // not made yet: the thread sees the flag; or already gone
        if (Environment.CurrentManagedThreadId == _thread?.ManagedThreadId)
        {
            _closing = true;
            return;
        }
        _work.Enqueue(() => _closing = true);
        Wake();
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    private void Post(Action work)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _work.Enqueue(work);
        Wake();
    }

    // Under the lock Close takes too: a write to a descriptor it has just closed could land in one Chromium reopened.
    private void Wake()
    {
        lock (_gate)
        {
            if (_wakeWrite < 0) return;
            byte b = 1;
            write(_wakeWrite, &b, 1);
        }
    }

    private void Loop()
    {
        var fds = stackalloc PollFd[2];
        var drain = stackalloc byte[64];
        while (!_closing)
        {
            fds[0] = new PollFd { Fd = XConnectionNumber(_display), Events = PollIn };
            fds[1] = new PollFd { Fd = _wakeRead, Events = PollIn };
            // A round trip may have read events into Xlib's queue, where poll cannot see them: never sleep on those.
            var timeout = XEventsQueued(_display, 0 /* QueuedAlready */) > 0 ? 0 : _fading ? 16 : _animating ? 33 : -1;
            poll(fds, 2, timeout);
            if ((fds[1].Revents & PollIn) != 0) while (read(_wakeRead, drain, 64) > 0) { }
            while (_work.TryDequeue(out var work)) Guard(work);
            if (_closing) break;
            XEvent evt;
            while (!_closing && XPending(_display) > 0)
            {
                XNextEvent(_display, &evt);
                var copy = evt;
                Guard(() => Handle(copy));
            }
            if (_closing) break;
            if (_fading) Guard(FadeStep);
            else if (_animating) Guard(() => Present(whole: false));
        }
    }

    private void Handle(XEvent copy)
    {
        var evt = &copy;
        var about = *(nuint*)((byte*)evt + EventWindowOffset);
        switch (evt->Type)
        {
            case Expose:
                Blit(new Rectangle(Point.Empty, _painter?.Size ?? Size.Empty));
                break;
            case ConfigureNotify when about == _window:
                // The manager applied a size (a resize under one is a request, not an act): draw at it.
                var size = new Size(*(int*)((byte*)evt + ConfigureWidthOffset), *(int*)((byte*)evt + ConfigureWidthOffset + 4));
                if (size != _size)
                {
                    _size = size;
                    Present(whole: true);
                }
                break;
            case MapNotify when about == _owner && _owner != 0:
                Restack();
                break;
            case ConfigureNotify when about == _owner && _owner != 0:
                Snap();
                break;
            case DestroyNotify when about == _owner && _owner != 0:
                _owner = 0;   // nothing more to follow; the session closes the splash
                break;
            case PropertyNotify when _owner != 0 && *(nuint*)((byte*)evt + PropertyWindowOffset) == _owner
                && *(nuint*)((byte*)evt + PropertyAtomOffset) == _netWmState:
                Snap();   // maximized, fullscreen or back: the band, and the restore glyph
                break;
            case MotionNotify or ButtonPress or ButtonRelease or LeaveNotify when _buttons is not null:
                Pointer(evt);
                break;
        }
    }

    // The strip's pointer: its buttons as CaptionButtons decides (hover, press, a click on release), and a press on the
    // rest of it as StripGesture decides (a drag the manager carries out, or a double-click that maximizes).
    private void Pointer(XEvent* evt)
    {
        var at = (byte*)evt;
        int x = *(int*)(at + PointerXOffset), y = *(int*)(at + PointerXOffset + 4);
        int rootX = *(int*)(at + PointerRootXOffset), rootY = *(int*)(at + PointerRootXOffset + 4);
        var time = *(nuint*)(at + PointerTimeOffset);
        var inStrip = y >= 0 && y < _stripTop;
        var kind = inStrip ? _buttons!.At(x, y) : null;
        switch (evt->Type)
        {
            case LeaveNotify:
                _buttons!.Leave();
                break;
            case MotionNotify:
                _buttons!.Hover(kind);
                // A press whose release never came (the grab was lost) is over: no drag without the button down.
                if ((*(uint*)(at + PointerStateOffset) & Button1Mask) == 0) _gesture.Up();
                else if (_gesture.Move(rootX, rootY) == StripGesture.Act.Drag) Drag(time);
                break;
            case ButtonPress when *(uint*)(at + PointerButtonOffset) == 1 && inStrip:
                if (kind is { } button) _buttons!.Press(button);
                else if (_gesture.Down(rootX, rootY, (long)time) == StripGesture.Act.ToggleMaximize) _layout.ToggleMaximize?.Invoke();
                break;
            case ButtonRelease when *(uint*)(at + PointerButtonOffset) == 1:
                _gesture.Up();
                if (_buttons!.IsPressed) _buttons.Release(kind);
                break;
        }
    }

    // The manager moves the MAIN window from where the press was made: the pointer's grab let go of first, then
    // _NET_WM_MOVERESIZE sent to the root, as a client asks for an interactive move.
    private void Drag(nuint time)
    {
        _gesture.Up();
        if (_owner == 0) return;
        var (fromX, fromY) = _gesture.DragFrom;
        XUngrabPointer(_display, time);
        XEvent message = default;
        var at = (byte*)&message;
        message.Type = ClientMessage;
        *(nuint*)(at + ClientWindowOffset) = _owner;
        *(nuint*)(at + ClientTypeOffset) = XInternAtom(_display, "_NET_WM_MOVERESIZE", 0);
        *(int*)(at + ClientFormatOffset) = 32;
        var data = (long*)(at + ClientDataOffset);
        data[0] = fromX;
        data[1] = fromY;
        data[2] = MoveResizeMove;
        data[3] = 1;   // button 1
        data[4] = 1;   // from an application
        XSendEvent(_display, XRootWindow(_display, XDefaultScreen(_display)), 0, SubstructureRedirectMask | SubstructureNotifyMask, &message);
        XFlush(_display);
    }

    private void Guard(Action work) => AppCallback.Run(work, ex => AppCallback.Log(log, () => "[Shenora.Chromium] The splash failed to draw", LogLevel.Warning, ex));

    private void Present(bool whole)
    {
        if (_window == 0) return;
        Render(_size);
        var damage = _painter is null ? Rectangle.Empty : _lastDamage;
        Blit(whole ? new Rectangle(Point.Empty, _painter?.Size ?? Size.Empty) : damage);
    }

    private Rectangle _lastDamage;

    // Renders at the size (the component below the strip, where there is one), and answers the frame's background as an
    // X pixel, which the window is made with.
    private nuint Render(Size size)
    {
        if (_painter is null || _render is null) return 0;
        var top = Math.Min(_stripTop, Math.Max(0, size.Height - 1));
        var frame = _render(new Size(size.Width, size.Height - top), _scale, _painter);
        _lastDamage = _painter.Paint(frame, top);
        _animating = frame.Animated;
        var background = frame.Ops.Count > 0 && frame.Ops[0] is SplashFill fill ? fill.Color : Color.Black;
        if (top > 0 && _buttons is not null)
        {
            // The glyphs read on what they sit on: the splash's background, not the system's theme.
            _palette ??= CaptionButtonPalette.ForStrip(_layout.TitleBar, CaptionButtonPalette.ForBackground(background));
            var buttons = SplashGeometry.DefaultCaptionButtons(size.Width, top, _scale);
            if (!buttons.SequenceEqual(_stripButtons))
            {
                _stripButtons = buttons;
                _buttons.Set(buttons);
                _stripDirty = true;
            }
            var strip = _painter.PaintStrip(top, background, _stripButtons, _stripState, _palette, _ownerMaximized, _scale);
            if (_stripDirty) _lastDamage = _lastDamage.IsEmpty ? strip : Rectangle.Union(_lastDamage, strip);
        }
        _stripDirty = false;   // drawn, or no strip to draw: either way nothing waits on it
        return (nuint)((background.R << 16) | (background.G << 8) | background.B);
    }

    // The painter's ARGB32 bytes are the 24-bit TrueColor visual's own layout; the XImage only borrows them, and says
    // they are in this machine's byte order (least significant first), so Xlib swaps them for a server that is not.
    private void Blit(Rectangle area)
    {
        if (_painter is null || _window == 0 || area.Width <= 0 || area.Height <= 0) return;
        var data = _painter.Data;
        var size = _painter.Size;
        if (_image == 0 || data != _imageData || size != _imageSize)
        {
            if (_image != 0) XFree(_image);
            _image = XCreateImage(_display, _visual, (uint)_depth, ZPixmap, 0, data, (uint)size.Width, (uint)size.Height, 32, _painter.Stride);
            if (_image != 0) *(int*)((byte*)_image + ImageByteOrderOffset) = 0;   // LSBFirst
            _imageData = data;
            _imageSize = size;
        }
        if (_image == 0) return;
        area = Rectangle.Intersect(area, new Rectangle(Point.Empty, size));
        XPutImage(_display, _window, _gc, _image, area.X, area.Y, area.X, area.Y, (uint)area.Width, (uint)area.Height);
        XFlush(_display);
    }

    // The main window is managed now: the hint again, the splash back on top of it, and at its bounds.
    private void Restack()
    {
        if (_owner == 0 || _window == 0) return;
        XSetTransientForHint(_display, _window, _owner);
        XRaiseWindow(_display, _window);
        Snap();
    }

    // Never over the whole main window: with no compositor X counts a window covered entirely as fully obscured, and
    // Chromium stops drawing it (measured under openbox on Xvfb: 0 frames a second and hidden; 60 and visible with a row
    // left). OwnerBounds leaves a frameless window's resize band, or else that bottom row.
    private void Snap()
    {
        if (_owner == 0 || _window == 0 || OwnerBounds(_owner) is not { } bounds) return;
        XMoveResizeWindow(_display, _window, bounds.X, bounds.Y, (uint)bounds.Width, (uint)bounds.Height);
        // Maximized or restored: the strip's glyph changed, and the size may not have. Only then: every other move of the
        // window is the window's alone, and a render per move would be a render per frame of a drag.
        if (_buttons is not null && _stripDirty) Present(whole: false);
        XFlush(_display);
    }

    private void FadeStep()
    {
        var progress = Stopwatch.GetElapsedTime(_fadeStart) / _fade;
        if (progress >= 1)
        {
            FadeDone();
            return;
        }
        var opacity = (long)(uint.MaxValue * (1 - progress));
        XChangeProperty(_display, _window, XInternAtom(_display, "_NET_WM_WINDOW_OPACITY", 0), 6 /* CARDINAL */, 32, PropModeReplace, &opacity, 1);
        XFlush(_display);
    }

    // The window goes first, then whoever waited on the fade is told, as the contract says.
    private void FadeDone()
    {
        _fading = false;
        _closing = true;
        DestroyWindow();
        var done = Interlocked.Exchange(ref _fadeDone, null);
        if (done is not null) Guard(done);
    }

    private void DestroyWindow()
    {
        nuint window;
        lock (_gate)
        {
            window = _window;
            _window = 0;
        }
        if (window != 0 && _display != 0)
        {
            XDestroyWindow(_display, window);
            XFlush(_display);
        }
    }

    // The splash thread, after its loop (or a failed open): everything it made, in reverse.
    private void Close()
    {
        Interlocked.Exchange(ref _disposed, 1);
        if (_image != 0) XFree(_image);
        _image = 0;
        DestroyWindow();
        AppCallback.Run(() => _painter?.Dispose());
        _painter = null;
        SplashX11.Close(_display);
        _display = 0;
        lock (_gate)
        {
            if (_wakeRead >= 0) close(_wakeRead);
            if (_wakeWrite >= 0) close(_wakeWrite);
            _wakeRead = _wakeWrite = -1;
        }
    }
}
#endif
