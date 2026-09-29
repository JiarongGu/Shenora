using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="WindowCommandModule"/>.</summary>
public sealed class WindowCommandOptions
{
    /// <summary>
    /// The window these options describe: the main one, hosting the page's web view. A page in any other window
    /// commands its own window, not this one (see <see cref="WindowCommandModule"/>).
    /// </summary>
    public required Form Window { get; init; }

    /// <summary>
    /// Maximize/restore toggle. Default: toggles <see cref="Form.WindowState"/> — correct for a framed
    /// window, WRONG for frameless custom chrome, which leaves a gap on every edge. A frameless app
    /// wires its manual path here (e.g. <c>OptimizedForm.ToggleMaximize</c>).
    /// </summary>
    public Action? ToggleMaximize { get; init; }

    /// <summary>
    /// Authoritative maximize state. Default: <see cref="Form.WindowState"/> — a frameless app
    /// wires its own (e.g. <c>OptimizedForm.AppPlacement</c>; its manual maximize never sets
    /// WindowState).
    /// </summary>
    public Func<bool>? IsMaximized { get; init; }

    /// <summary>
    /// When set, the <c>SET_THEME</c> route is enabled: the frontend sends <c>{ dark }</c> on every
    /// effective-theme change and this callback resyncs the native chrome (DWM border, fill, splash
    /// colors), which a runtime light↔dark switch otherwise leaves in the old theme. What "dark" means
    /// stays app-defined.
    /// </summary>
    public Action<bool>? ApplyTheme { get; init; }

    /// <summary>
    /// When set, the <c>SET_CAPTION_BUTTONS</c> route is enabled: the page reports where it drew its
    /// minimize/maximize/close buttons and this hands the rectangles to the window, so the OS can
    /// treat them as real caption buttons — chiefly so Windows 11 offers Snap Layouts on the maximize
    /// button, which a page-drawn button otherwise never gets. A frameless app wires
    /// <c>OptimizedForm.SetCaptionButtons</c> here.
    /// </summary>
    public Action<IReadOnlyList<Shenora.Windows.CaptionButtonRegion>>? SetCaptionButtons { get; init; }

    /// <summary>
    /// The control the page's CSS coordinates are relative to — required only when
    /// <see cref="SetCaptionButtons"/> is set. Normally the WebView2 itself. Its
    /// <c>DeviceDpi</c> is what converts CSS px to physical px, per-monitor under PerMonitorV2.
    /// A page the kit's transports mark is read against the control showing it instead, so a form
    /// with two pages converts each one's own; this is for a send from no page.
    /// </summary>
    public Control? CoordinateSpace { get; init; }
}

/// <summary>
/// The frontend-triggered window commands. ⚠ The module is <c>SHENORA.WINDOW</c> — D64's reserved
/// prefix — so a page invoking the unprefixed name gets <c>NO_HANDLER</c>. The routes are the
/// <c>…Type</c> constants below; the client side is <c>WindowCommands</c> in @shenora/react, mirrored
/// by <c>WireMirrorTests</c>.
///
/// REGISTRATION: this facade needs the LIVE form, which does not exist when the container is built — so
/// map it late, from wherever you create the window:
/// <code>
/// dispatcher.MapModule(new WindowCommandModule(new WindowCommandOptions { Window = this, … }));
/// </code>
/// <c>dispatcher</c> there is the plain <see cref="IMessageDispatcher"/> resolved from DI; no cast is
/// needed, and late mapping is safe while requests are in flight. ⚠ NOT
/// <c>UseMessageDispatcher</c>'s configure callback, which runs at provider-build time, before any form
/// exists.
///
/// ONCE, for the main window. Each command acts on the window whose page sent it, as the kit's page transports
/// (<see cref="WebViewIpcBridge"/>, <see cref="ChromiumView"/>) report it: a <see cref="SecondaryWindows"/>
/// window's close button closes that window, not the app's. A window is the page's top-level form, so a page in a
/// form embedded in the main one (an MDI child) commands the main window. The options describe the main window. Another window
/// is commanded as itself: an <see cref="OptimizedForm"/> maximizes its own way and takes its caption buttons,
/// relative to the page that sent them, and a plain form uses <see cref="Form.WindowState"/> (an
/// <see cref="IAppMaximizable"/> one its placement); <c>SET_THEME</c> has no route there. A page whose window is gone
/// commands nothing. A programmatic send commands the main window, unless it runs in work a page's request started,
/// which is that page's as the request's own continuations are.
///
/// Threading: routes touch each form through a UI dispatcher over it, caption rectangles converted there too —
/// correct from the transport's UI-thread dispatch AND from programmatic sends off it.
/// </summary>
public sealed class WindowCommandModule : ModuleBase
{
    /// <summary>The reserved module name (mirrored by the client's <c>WindowCommands</c>).</summary>
    public const string Module = "SHENORA.WINDOW";

    /// <summary>Route: minimize the window. No payload.</summary>
    public const string MinimizeType = "MINIMIZE";

    /// <summary>Route: maximize if restored, restore if maximized. No payload.</summary>
    public const string ToggleMaximizeType = "TOGGLE_MAXIMIZE";

    /// <summary>Route: close the window (the app's <c>FormClosing</c> logic still runs). No payload.</summary>
    public const string CloseType = "CLOSE";

    /// <summary>Route: is it maximized? Answers <c>{ maximized }</c> — authoritative for the chrome's glyph,
    /// since a manual work-area maximize never shows in <c>WindowState</c>.</summary>
    public const string IsMaximizedType = "IS_MAXIMIZED";

    /// <summary>Route: begin an OS window-move loop (the page's header on mousedown). No payload.</summary>
    public const string StartDragType = "START_DRAG";

    /// <summary>Route: begin an OS resize loop: <c>{ edge }</c> — <c>top</c>, <c>topLeft</c> or
    /// <c>topRight</c>.</summary>
    public const string StartResizeType = "START_RESIZE";

    /// <summary>Route: <c>{ dark }</c>. Opt-in — unset <see cref="WindowCommandOptions.ApplyTheme"/>
    /// answers <c>NO_ROUTE</c>.</summary>
    public const string SetThemeType = "SET_THEME";

    /// <summary>Route: <c>{ buttons }</c>, the caption-button hit rectangles. Opt-in — unset
    /// <see cref="WindowCommandOptions.SetCaptionButtons"/> answers <c>NO_ROUTE</c>.</summary>
    public const string SetCaptionButtonsType = "SET_CAPTION_BUTTONS";

    // Borderless-window drag/resize: hand off to the OS window-move/-size loop — the page can't drive
    // native drag itself.
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    private readonly WindowCommandOptions _options;
    private readonly Target _own;
    private readonly Microsoft.Extensions.Logging.ILogger? _log;

    /// <summary>Window commands over IPC. Every route is opt-in: an unset callback answers NO_ROUTE.</summary>
    public WindowCommandModule(WindowCommandOptions options, Microsoft.Extensions.Logging.ILogger<WindowCommandModule>? logger = null)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = logger;
        _own = new Target(_options.Window, _options.CoordinateSpace ?? _options.Window, _options.ToggleMaximize,
            _options.IsMaximized, _options.ApplyTheme, _options.SetCaptionButtons);
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        var isPage = PageSender.IsPage(out var sender);
        if (For(isPage, sender) is not { } window) return Gone(request);
        var form = window.Form;
        // A page's rectangles are relative to the control showing it; a send from no page has the options'.
        var space = sender ?? window.Space;
        switch (request.Type.ToUpperInvariant())
        {
            case MinimizeType:
                window.Post(() => form.WindowState = FormWindowState.Minimized);
                return Done();

            case ToggleMaximizeType:
                window.Post(window.ToggleMaximize ?? (() =>
                    form.WindowState = form.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized));
                return Done();

            case CloseType:
                window.Post(form.Close);
                return Done();

            case IsMaximizedType:
                // Plain read — a bool snapshot is benign cross-thread.
                return Task.FromResult<object?>(new { Maximized = window.Maximized() });

            case StartDragType:
                // ⚠ Refused while maximized: a manual work-area maximize keeps WindowState.Normal, so
                // the OS would drag the maximized-size window with stale restore bounds. The page's
                // header restores first, as native caption drags do.
                if (window.Maximized())
                    return Done();
                window.Post(() =>
                {
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                });
                return Done();

            case StartResizeType:
                // Frameless TOP-edge resize only — the WM_NCCALCSIZE technique keeps the native
                // side/bottom borders, and the WebView2 covers the form's top so its hit-test never
                // sees it. ⚠ lParam MUST be the cursor screen pos, or the size loop starts at (0,0)
                // and doesn't track.
                var edge = PayloadHelper.GetOptionalValue<string>(request.Payload, "edge");
                var hitTest = edge switch { "topLeft" => HTTOPLEFT, "topRight" => HTTOPRIGHT, _ => HTTOP };
                window.Post(() =>
                {
                    GetCursorPos(out var pt);
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
                });
                return Done();

            case SetThemeType when window.ApplyTheme is { } applyTheme:
                var dark = PayloadHelper.GetOptionalValue<bool?>(request.Payload, "dark") ?? true;
                window.Post(() => applyTheme(dark));
                return Done();

            case SetCaptionButtonsType when window.SetCaptionButtons is { } setCaptionButtons:
                // ⚠ A stale rectangle silently moves the hit-test away from the button the user sees,
                // which presents as "the close button sometimes does nothing".
                var css = ParseCaptionButtons(request.Payload);
                window.Post(() => setCaptionButtons(ToClient(css, form, space)));
                return Done();

            default:
                throw UnknownType(request);   // ModuleBase owns the shape
        }
    }

    /// <summary>
    /// The window a command acts on: the options' own, unless the page that sent it is in another window. There it
    /// is that window, so a <see cref="SecondaryWindows"/> window's close button closes that window rather than the
    /// app's. Such a window is an <see cref="OptimizedForm"/>'s own (its maximize, its placement, its caption
    /// buttons, over the sending page) or a plain form's; its theme has no route, since what the options' callback
    /// does is the app's. The window is the page's TOP-LEVEL form, so a page in a form embedded in the main one (an MDI
    /// child, a <c>TopLevel = false</c> panel) commands the main window. Null when the sending page has no window any
    /// more (it is closing, or its control has been collected).
    /// </summary>
    private Target? For(bool isPage, Control? sender)
    {
        if (!isPage)
        {
            // A page whose transport does not mark it (a ChromiumChildBrowser handed a UI dispatcher of its own) arrives
            // here from its own window's thread, and would command the main window.
            if (Application.MessageLoop && _options.Window.IsHandleCreated && _options.Window.InvokeRequired)
                _log?.LogWarning("A window command came from another window's thread with no page marked, so it acts on the main window. " +
                    "Host a Chromium page in a ChromiumView, whose page marks itself.");
            return _own;
        }
        if (sender is null || sender.IsDisposed || sender.TopLevelControl is not Form form) return null;
        if (ReferenceEquals(form, _options.Window)) return _own;
        Func<bool>? maximized = form is IAppMaximizable app ? () => app.AppPlacement == WindowPlacement.Maximized : null;
        return form is OptimizedForm optimized
            ? new Target(form, sender, optimized.ToggleMaximize, maximized, applyTheme: null, optimized.SetCaptionButtons)
            : new Target(form, sender, null, maximized, null, null);
    }

    // No window to command: the commands do nothing, and the opt-in routes, which a window's callbacks answer, have none.
    private Task<object?> Gone(IpcRequest request) => request.Type.ToUpperInvariant() switch
    {
        IsMaximizedType => Task.FromResult<object?>(new { Maximized = false }),
        MinimizeType or ToggleMaximizeType or CloseType or StartDragType or StartResizeType => Done(),
        _ => throw UnknownType(request),
    };

    /// <summary>A window, what its page's coordinates are relative to, and its callbacks (null: the default, or no route).</summary>
    private sealed class Target(Form form, Control space, Action? toggleMaximize, Func<bool>? isMaximized,
        Action<bool>? applyTheme, Action<IReadOnlyList<Shenora.Windows.CaptionButtonRegion>>? setCaptionButtons)
    {
        // The one marshalling owner. It also GUARDS the posted body, which matters here: SET_THEME runs
        // an app-supplied callback and CLOSE runs app FormClosing logic, and an exception from either
        // has no caller on the stack.
        private readonly Shenora.Windows.WinFormsUiDispatcher _ui = new(form);

        public Form Form => form;
        public Control Space => space;
        public Action? ToggleMaximize => toggleMaximize;
        public Action<bool>? ApplyTheme => applyTheme;
        public Action<IReadOnlyList<Shenora.Windows.CaptionButtonRegion>>? SetCaptionButtons => setCaptionButtons;

        public bool Maximized() => isMaximized?.Invoke() ?? form.WindowState == FormWindowState.Maximized;

        /// <summary>
        /// Post to the form's UI thread through the one owner — INLINE when the caller is already on it,
        /// which <c>START_DRAG</c> needs (the OS window-move loop must start while the button is still
        /// down).
        /// <para>
        /// ⚠ CONSEQUENCE for the two handoff routes: dispatched from the UI thread,
        /// <c>SendMessage(WM_NCLBUTTONDOWN)</c> runs inline and blocks for the WHOLE OS move/size loop, so
        /// their <c>Done()</c> reaches the page only after the user releases the mouse — a long drag can
        /// pass the client's request timeout and reject a promise whose work succeeded. Do NOT "fix" it by
        /// forcing a post; that loses the mouse-down timing. A test must dispatch these two routes from a
        /// worker thread or it enters the modal loop itself.
        /// </para>
        /// </summary>
        public void Post(Action action) => _ui.Post(action);
    }

    /// <summary>
    /// <c>{ buttons: [{ kind, x, y, width, height }] }</c> in CSS px, read from the payload while it is the request's.
    /// Unknown kinds and malformed entries are SKIPPED rather than failing the whole call — rejecting a batch over one
    /// odd entry would drop the other buttons' hit-tests as collateral.
    /// </summary>
    private static List<(Shenora.Windows.CaptionButtonKind Kind, double X, double Y, double Width, double Height)> ParseCaptionButtons(JsonElement? payload)
    {
        var css = new List<(Shenora.Windows.CaptionButtonKind, double, double, double, double)>(3);
        if (payload is not { } root || root.ValueKind != JsonValueKind.Object) return css;
        if (!root.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array) return css;

        static double Css(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        foreach (var entry in buttons.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ParseKind(entry) is not { } kind) continue;
            css.Add((kind, Css(entry, "x"), Css(entry, "y"), Css(entry, "width"), Css(entry, "height")));
        }
        return css;
    }

    /// <summary>
    /// CSS px, relative to <paramref name="space"/>, → the form's client px. Zero-size rectangles are skipped. On the
    /// form's thread: the conversion goes through both windows' handles.
    /// </summary>
    private static IReadOnlyList<Shenora.Windows.CaptionButtonRegion> ToClient(
        List<(Shenora.Windows.CaptionButtonKind Kind, double X, double Y, double Width, double Height)> css, Form form, Control space)
    {
        // CSS px → physical px via the CONTROL's DeviceDpi — per-monitor under PerMonitorV2, where a
        // process-global scale factor is wrong on a mixed-DPI desktop (same as
        // DropZoneManager.ToFormBounds).
        var scale = Shenora.Windows.DpiHelper.ScaleFromDeviceDpi(space.DeviceDpi);
        int Px(double value) => (int)Math.Round(value * scale);

        var regions = new List<Shenora.Windows.CaptionButtonRegion>(css.Count);
        foreach (var (kind, x, y, width, height) in css)
        {
            if (Px(width) <= 0 || Px(height) <= 0) continue;
            // Through screen coordinates: the page's origin is the CONTROL, the hit-test works in the
            // FORM's client space, and the two differ whenever the WebView2 does not fill the form.
            var client = form.PointToClient(space.PointToScreen(new Point(Px(x), Px(y))));
            regions.Add(new Shenora.Windows.CaptionButtonRegion(kind, new Rectangle(client.X, client.Y, Px(width), Px(height))));
        }
        return regions;
    }

    private static Shenora.Windows.CaptionButtonKind? ParseKind(JsonElement entry) =>
        entry.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
            ? kind.GetString()?.ToUpperInvariant() switch
            {
                "MINIMIZE" => Shenora.Windows.CaptionButtonKind.Minimize,
                "MAXIMIZE" => Shenora.Windows.CaptionButtonKind.Maximize,
                "CLOSE" => Shenora.Windows.CaptionButtonKind.Close,
                _ => null,
            }
            : null;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
}
