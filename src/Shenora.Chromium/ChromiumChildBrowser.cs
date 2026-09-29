using System.Drawing;
using System.Runtime.InteropServices;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Core.Shell;

namespace Shenora.Chromium;

/// <summary>Inputs for <see cref="ChromiumChildBrowser"/>.</summary>
public sealed class ChromiumChildBrowserOptions
{
    /// <summary>The page to open, relative to the app's origin (or its dev server). Null means its root.</summary>
    public string? Path { get; init; }

    /// <summary>What shows before the page paints, so a dark page never flashes white.</summary>
    public Color? BackgroundColor { get; init; }

    /// <summary>The browser's name, in the log.</summary>
    public string Name { get; init; } = "browser";

    /// <summary>
    /// The thread the page's IPC is dispatched on, which is the one that owns the parent window. Null means the
    /// app's own <see cref="IUiDispatcher"/>, which is right for the main window only: a window on a thread of its
    /// own (the kit's <c>SecondaryWindows</c>) passes a dispatcher over itself.
    /// ⚠ A WinForms host uses <c>ChromiumView</c> instead, whose dispatcher also marks the page as its own: the kit's
    /// window commands find the page's window that way, and a page given a plain dispatcher commands the main window.
    /// </summary>
    public IUiDispatcher? UiDispatcher { get; init; }

    /// <summary>
    /// The keyboard focus is leaving the page, <c>true</c> for Tab past its last element and <c>false</c> for Shift+Tab
    /// past its first: the host moves it to its next or previous control. WebView2's name for the same event. Called on
    /// the thread of <see cref="UiDispatcher"/>. Null leaves the focus in the page.
    /// </summary>
    public Action<bool>? MoveFocusRequested { get; init; }

    /// <summary>
    /// The page's <c>-webkit-app-region: drag</c> area asks the host to move its window, to maximize or restore it, or to
    /// open its system menu, as a caption would. A mouse press there is held until the pointer passes the system's drag
    /// threshold, so a still click asks nothing, and a right click asks for the menu as it is released. Neither reaches
    /// the page; a touch or a pen does. Called on the thread of <see cref="UiDispatcher"/>, after the press: ⚠ the button
    /// may be up by then, so check it before starting a move loop, which would otherwise follow the pointer until the
    /// next click. Null leaves every press to the page.
    /// </summary>
    public Action<ChromiumDragAreaPress>? DragAreaPressed { get; init; }
}

/// <summary>What a page's <c>-webkit-app-region: drag</c> area asks of its window
/// (<see cref="ChromiumChildBrowserOptions.DragAreaPressed"/>).</summary>
/// <param name="Action">What to do. A host ignores an action it does not know: a later version may add one.</param>
/// <param name="Position">Where, in screen pixels. For <see cref="ChromiumDragAreaAction.Move"/>, where the press began:
/// a move loop started now should place the window by the pointer's travel since then, as a caption's does, or the
/// window trails the pointer by the threshold.</param>
public readonly record struct ChromiumDragAreaPress(ChromiumDragAreaAction Action, Point Position);

/// <summary>What a press in a page's drag area asks of its window, as the same gesture on a caption would.</summary>
public enum ChromiumDragAreaAction
{
    /// <summary>A press has moved past the system's drag threshold with the button down: move the window.</summary>
    Move,

    /// <summary>A double click: maximize the window, or restore it.</summary>
    ToggleMaximize,

    /// <summary>A right click, released in the area: open the window's system menu at the release.</summary>
    ShowSystemMenu,
}

/// <summary>
/// A page in a window the host owns (D83): a Chromium browser as a child of a native window, served from the
/// <see cref="ChromiumEngine"/>'s origin, with its IPC dispatched on the thread owning the parent window
/// (<see cref="ChromiumChildBrowserOptions.UiDispatcher"/>). The browser's own window belongs to CEF's UI thread;
/// the host places it with <see cref="SetBounds"/>. Windows only, today.
/// <para>
/// <see cref="Dispose"/> closes the browser and leaves its parent open; destroying the parent closes it too.
/// ⚠ Either way, stop the engine only once <see cref="Closed"/> has completed.
/// </para>
/// </summary>
public sealed unsafe class ChromiumChildBrowser : IDisposable
{
#if !CEF_WINDOWS
#pragma warning disable CS0649, CS0169   // assigned by the Windows constructor only
#endif
    private readonly ChromiumEngine _engine;
    private readonly ChromiumBrowser _browser;
    private readonly nint _parent;
#if !CEF_WINDOWS
#pragma warning restore CS0649, CS0169
#endif
    private readonly object _lock = new();
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Rectangle? _bounds;
    private int _disposed;
    private int _finished;

    /// <summary>Create the browser as a child of <paramref name="parentWindow"/>, filling its client area until
    /// <see cref="SetBounds"/> says otherwise. It appears once the engine is ready (<see cref="Created"/>).</summary>
    /// <param name="engine">The started engine.</param>
    /// <param name="parentWindow">The native window to create the browser in: an <c>HWND</c>.</param>
    /// <param name="options">Its page, and what shows before the page paints.</param>
    /// <exception cref="InvalidOperationException">The engine has not been started, which off Windows it never is: the
    /// package is the Windows build today, and the engine refuses to start elsewhere.</exception>
    public ChromiumChildBrowser(ChromiumEngine engine, nint parentWindow, ChromiumChildBrowserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (parentWindow == 0) throw new ArgumentException("A parent window is required.", nameof(parentWindow));
#if !CEF_WINDOWS
        throw new PlatformNotSupportedException("A Chromium child browser is Windows only, today.");
#else
        options ??= new ChromiumChildBrowserOptions();
        var pages = engine.Pages();
        _engine = engine;
        _parent = parentWindow;
        var ui = options.UiDispatcher ?? pages.Ui;
        _browser = new ChromiumBrowser(options.Name, pages.Serving, pages.Origins, browser => NewBridge(browser, pages, ui), pages.Log, pages.Urls)
        {
            Host = new BrowserHost(this, options.MoveFocusRequested is { } moveFocus ? forward => ui.Post(() => AppCallback.Run(() => moveFocus(forward))) : null,
                options.DragAreaPressed is { } pressed
                    ? new ChildDragArea(press => ui.Post(() => AppCallback.Run(() => pressed(press),
                        ex => AppCallback.Log(pages.Log, () => "[Shenora.Chromium] The host's drag-area callback failed", Microsoft.Extensions.Logging.LogLevel.Warning, ex))), pages.Log)
                    : null),
        };
        var url = options.Path is { } path ? new Uri(pages.Root, path) : pages.Root;
        var background = options.BackgroundColor;
        engine.BrowserOpened(this);
        engine.Ready.ContinueWith(ready =>
        {
            if (ready.Exception is { } failed) Finish(failed.InnerException ?? failed);
            else if (!CefTask.Post(cef_thread_id_t.TID_UI, () => Create(url, background)))
                Finish(new ObjectDisposedException(nameof(ChromiumEngine), "The Chromium engine has stopped."));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
#endif
    }

    /// <summary>Completes once the browser exists. Faults when it could not be created, and is cancelled when it was
    /// disposed first; either way <see cref="Closed"/> completes too.</summary>
    public Task Created => _created.Task;

    /// <summary>Completes once the browser has closed, or will never open.</summary>
    public Task Closed => _closed.Task;

    /// <summary>The browser's own window while it exists, else 0. Any thread.</summary>
    public nint WindowHandle => _browser.WindowHandle;

    /// <summary>Place the browser in its parent, in the parent's client pixels. Any thread; before the browser exists,
    /// it opens there.</summary>
    /// <param name="bounds">The browser's rectangle.</param>
    public void SetBounds(Rectangle bounds)
    {
        nint window;
        lock (_lock)
        {
            _bounds = bounds;
            window = WindowHandle;
        }
        if (window != 0) Place(window, bounds);
    }

    /// <summary>Give the page the keyboard focus. Any thread.</summary>
    public void Focus() => _browser.Focus();

    /// <summary>Close the browser, skipping the page's <c>beforeunload</c>. <see cref="Closed"/> completes once it has.
    /// Any thread; idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Before the browser exists this does nothing, and its creation (Create, or BrowserCreated) closes it.
        if (!_browser.CloseBrowser(force: true))
            Finish(new ObjectDisposedException(nameof(ChromiumEngine), "The Chromium engine has stopped."));
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

#if CEF_WINDOWS
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000,
        WS_TABSTOP = 0x00010000;

    // CEF's UI thread.
    private void Create(Uri url, Color? background)
    {
        if (IsDisposed) { Finish(null); return; }
        Rectangle bounds;
        lock (_lock) bounds = _bounds ?? ClientArea(_parent);
        var info = new _cef_window_info_t
        {
            size = (nuint)sizeof(_cef_window_info_t),
            style = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | WS_TABSTOP,
            parent_window = _parent,
            bounds = new _cef_rect_t { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height },
            runtime_style = cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY,   // D84
        };
        var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
        if (background is { } color) settings.background_color = (uint)color.ToArgb();
        var text = url.AbsoluteUri;
        int created;
        fixed (char* p = text)
        {
            var s = CefStrings.View(p, text.Length);
            created = Cef.cef_browser_host_create_browser(&info, _browser.ClientForCef(), &s, &settings, null, null);
        }
        if (created != 1) Finish(new InvalidOperationException($"CEF would not create the browser '{_browser.Name}'."));
    }

    private static Rectangle ClientArea(nint window) =>
        GetClientRect(window, out var r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;

    private static void Place(nint window, Rectangle bounds) =>
        SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out RECT rect);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
#else
    private static void Place(nint window, Rectangle bounds) { }
#endif

    /// <summary>Closed, or never opened: once, whichever comes first.</summary>
    private void Finish(Exception? failure)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;
        _browser.Bridge.Dispose();
        _browser.Retire();
        _engine.BrowserClosed(this);
        if (failure is null) _created.TrySetCanceled();
        else _created.TrySetException(failure);
        _closed.TrySetResult();
    }

    private void BrowserCreated()
    {
        _created.TrySetResult();
        if (IsDisposed) { _browser.CloseBrowser(force: true); return; }
        Rectangle? bounds;
        lock (_lock) bounds = _bounds;
        if (bounds is { } b && WindowHandle is var window and not 0) Place(window, b);
    }

    // CEF's default asks the TOP-LEVEL window to close, which is the host's, not the browser's to close: a form that
    // waits for its browser before closing cancels that request, and the two then wait on each other. The browser's
    // own window goes instead, on CEF's UI thread, which owns it. Posted, so CEF has returned from do_close first.
    private bool CloseRequested()
    {
#if CEF_WINDOWS
        var window = WindowHandle;
        if (window == 0) return false;
        return CefTask.Post(cef_thread_id_t.TID_UI, () => DestroyWindow(window));
#else
        return false;
#endif
    }

    private static ChromiumIpcBridge NewBridge(ChromiumBrowser browser, ChromiumPages pages, IUiDispatcher ui) =>
        new(new ChromiumIpcBridgeOptions
            {
                Dispatcher = pages.Dispatcher, EventBus = pages.Events, Shell = pages.Shell, Log = pages.Log,
                EnterWindow = () => ChromiumBrowserContext.Enter(browser),
            },
            ui,
            // The bridge runs on the host's thread and the page's browser on CEF's: posted in order, pushed in order.
            message => CefTask.Post(cef_thread_id_t.TID_UI, () => browser.Push(message)),
            (delay, work) => After(ui, delay, work));

    // The flush tick, on the host's thread. A refused post, once that thread is gone, schedules nothing further.
    private static bool After(IUiDispatcher ui, TimeSpan delay, Action work)
    {
        if (ui.State == UiTargetState.Gone) return false;
        Task.Delay(delay).ContinueWith(_ => ui.Post(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return true;
    }

    /// <summary>What the page's browser asks of its host. CEF's UI thread.</summary>
    private sealed class BrowserHost(ChromiumChildBrowser owner, Action<bool>? moveFocus, ChildDragArea? dragArea) : IChromiumBrowserHost
    {
        // Posted to the host's thread, which owns its other controls.
        public void MoveFocusRequested(bool forward) => moveFocus?.Invoke(forward);
        public void DraggableRegionsChanged(nuint count, _cef_draggable_region_t* regions) => dragArea?.Update(owner.WindowHandle, count, regions);
        public void TitleChanged(string title) { }
        public void DocumentStarted() => dragArea?.DocumentStarted(owner.WindowHandle);
        public void BrowserCreated()
        {
            owner.BrowserCreated();
        }
        public void BrowserClosed()
        {
            dragArea?.Dispose();
            owner.Finish(null);
        }
        public bool CloseRequested() => owner.CloseRequested();
    }
}
