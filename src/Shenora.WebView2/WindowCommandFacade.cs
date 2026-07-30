using System.Runtime.InteropServices;
using Shenora.Ipc;

namespace Shenora.WebView2;

/// <summary>Inputs for <see cref="WindowCommandFacade"/>.</summary>
public sealed class WindowCommandOptions
{
    /// <summary>The window the commands target — the form hosting the WebView2.</summary>
    public required Form Window { get; init; }

    /// <summary>
    /// Maximize/restore toggle. Default: toggles <see cref="Form.WindowState"/> — correct for a
    /// framed window, WRONG for frameless custom chrome (a borderless window maximized via
    /// WindowState leaves a ~6 px gap per edge; measured). A frameless app wires its manual
    /// path here (e.g. Shenora.WinForms <c>OptimizedForm.ToggleMaximize</c>).
    /// </summary>
    public Action? ToggleMaximize { get; init; }

    /// <summary>
    /// Authoritative maximize state. Default: <see cref="Form.WindowState"/> — a frameless app
    /// wires its own (e.g. <c>OptimizedForm.IsAppMaximized</c>; its manual maximize never sets
    /// WindowState).
    /// </summary>
    public Func<bool>? IsMaximized { get; init; }

    /// <summary>
    /// When set, the <c>SET_THEME</c> route is enabled: the frontend sends <c>{ dark }</c> on
    /// every effective-theme change and this callback resyncs the native chrome (DWM border,
    /// fill, splash colors — a runtime light↔dark switch otherwise leaves the frame in the old
    /// theme; measured). What "dark" means (the actual colors) stays app-defined — headless.
    /// </summary>
    public Action<bool>? ApplyTheme { get; init; }
}

/// <summary>
/// The frontend-triggered window commands, ported from the second desktop sibling's session
/// routes: module <c>WINDOW</c>, types <c>MINIMIZE</c> / <c>TOGGLE_MAXIMIZE</c> / <c>CLOSE</c> /
/// <c>IS_MAXIMIZED</c> / <c>START_DRAG</c> / <c>START_RESIZE</c> (+ optional <c>SET_THEME</c>).
/// The client side is <c>WindowCommands</c> in @shenora/react.
///
/// REGISTRATION: this facade needs the LIVE form, which does not exist when the container is built — so
/// map it late, from wherever you create the window:
/// <code>
/// dispatcher.MapModule(new WindowCommandFacade(new WindowCommandOptions { Window = this, … }));
/// </code>
/// <c>dispatcher</c> there is the plain <see cref="IMessageDispatcher"/> resolved from DI; no cast is
/// needed, and late mapping is safe while requests are in flight. This doc previously pointed at
/// <c>AddMessageDispatcher</c>'s configure callback, which CANNOT work: that callback runs at
/// provider-build time, before any form exists (P5.5 H6). It also required a downcast to
/// <c>MessageDispatcher</c> until the mapping helpers moved onto the interface.
///
/// Lives in Shenora.WebView2 (not Shenora.WinForms) because the commands arrive over the
/// bridge and need Shenora.Ipc — which the WinForms package deliberately doesn't reference
/// (packages depend only downward; the app composes them).
///
/// Threading: routes touch the form via non-blocking <c>BeginInvoke</c> posts (the source
/// shape) — correct from the transport's UI-thread dispatch AND from programmatic sends off it.
/// </summary>
public sealed class WindowCommandFacade : BaseFacade
{
    /// <summary>The reserved module name (mirrored by the client's <c>WindowCommands</c>).</summary>
    public const string Module = "WINDOW";

    // Borderless-window drag/resize: hand off to the OS window-move/-size loop — the reliable
    // WebView2 technique (the page can't drive native drag itself).
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    private readonly WindowCommandOptions _options;
    private readonly Shenora.Core.IUiDispatcher _ui;

    public WindowCommandFacade(WindowCommandOptions options, Microsoft.Extensions.Logging.ILogger<WindowCommandFacade>? logger = null)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // One marshalling owner (P5.5 H4.2). It also GUARDS the posted body, which matters here more
        // than anywhere: SET_THEME runs an app-supplied callback and CLOSE runs app FormClosing logic,
        // and an exception from either used to become an unhandled UI-thread exception.
        _ui = new Shenora.WinForms.WinFormsUiDispatcher(_options.Window);
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(IpcRequest request)
    {
        var form = _options.Window;
        switch (request.Type.ToUpperInvariant())
        {
            case "MINIMIZE":
                Post(() => form.WindowState = FormWindowState.Minimized);
                return Done();

            case "TOGGLE_MAXIMIZE":
                Post(_options.ToggleMaximize ?? (() =>
                    form.WindowState = form.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized));
                return Done();

            case "CLOSE":
                Post(form.Close);
                return Done();

            case "IS_MAXIMIZED":
                // Authoritative state for the chrome's max/restore glyph (a manual work-area
                // maximize never shows in WindowState). Plain read — a bool snapshot is benign
                // cross-thread.
                var maximized = _options.IsMaximized?.Invoke() ?? (form.WindowState == FormWindowState.Maximized);
                return Task.FromResult<object?>(new { Maximized = maximized });

            case "START_DRAG":
                // The page's header sends this on mousedown over its empty area. Hand off to the
                // OS move loop (ReleaseCapture + WM_NCLBUTTONDOWN/HTCAPTION) so the window drags
                // natively — snap and multi-monitor included. Refused while maximized: the
                // manual work-area maximize keeps WindowState.Normal, so the OS would happily
                // drag the maximized-size window with stale restore bounds (found in review) —
                // the page's header should restore first (as native caption drags do).
                if (_options.IsMaximized?.Invoke() ?? form.WindowState == FormWindowState.Maximized)
                    return Done();
                Post(() =>
                {
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                });
                return Done();

            case "START_RESIZE":
                // Frameless TOP-edge resize: the WebView2 covers the form's top, so its hit-test
                // never sees it — a thin page-side strip sends this on mousedown. Only the top
                // edges exist by design (the WM_NCCALCSIZE technique keeps the native side/bottom
                // borders). lParam MUST be the cursor screen pos, or the size loop starts at
                // (0,0) and doesn't track (measured).
                var edge = PayloadHelper.GetOptionalValue<string>(request.Payload, "edge");
                var hitTest = edge switch { "topLeft" => HTTOPLEFT, "topRight" => HTTOPRIGHT, _ => HTTOP };
                Post(() =>
                {
                    GetCursorPos(out var pt);
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
                });
                return Done();

            case "SET_THEME" when _options.ApplyTheme is { } applyTheme:
                var dark = PayloadHelper.GetOptionalValue<bool?>(request.Payload, "dark") ?? true;
                Post(() => applyTheme(dark));
                return Done();

            default:
                throw UnknownType(request);   // BaseFacade owns the shape (P5.5 H4.5)
        }
    }

    /// <summary>
    /// Best-effort non-blocking post to the form's UI thread, through the one owner.
    /// <para>
    /// Behaviour CHANGE from the source shape, and it is a fix: this used to call
    /// <c>BeginInvoke</c> unconditionally, so a command arriving already ON the UI thread was still
    /// deferred to the next message — which loses <c>START_DRAG</c>'s mouse-down timing, since the OS
    /// window-move loop must start while the button is still down. The dispatcher runs inline when
    /// the caller is already on the UI thread and posts otherwise.
    /// </para>
    /// </summary>
    private void Post(Action action) => _ui.Post(action);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point pt);
}
