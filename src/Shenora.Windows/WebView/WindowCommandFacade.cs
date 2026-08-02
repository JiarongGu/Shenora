using System.Runtime.InteropServices;
using System.Text.Json;
using Shenora.Ipc;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="WindowCommandFacade"/>.</summary>
public sealed class WindowCommandOptions
{
    /// <summary>The window the commands target — the form hosting the WebView2.</summary>
    public required Form Window { get; init; }

    /// <summary>
    /// Maximize/restore toggle. Default: toggles <see cref="Form.WindowState"/> — correct for a
    /// framed window, WRONG for frameless custom chrome (a borderless window maximized via
    /// WindowState leaves a ~6 px gap per edge; measured). A frameless app wires its manual
    /// path here (e.g. Shenora.Windows <c>OptimizedForm.ToggleMaximize</c>).
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

    /// <summary>
    /// When set, the <c>SET_CAPTION_BUTTONS</c> route is enabled: the page reports where it drew its
    /// minimize/maximize/close buttons and this hands the rectangles to the window, so the OS can
    /// treat them as real caption buttons — chiefly so Windows 11 offers **Snap Layouts** on the
    /// maximize button, which a page-drawn button otherwise never gets (P5.6).
    /// <para>
    /// A frameless app wires <c>OptimizedForm.SetCaptionButtons</c> here. A delegate rather than a
    /// direct call for the same reason as <see cref="ToggleMaximize"/>: <see cref="Window"/> is a
    /// plain <see cref="Form"/>, and this package does not get to assume which form type an app used.
    /// </para>
    /// </summary>
    public Action<IReadOnlyList<Shenora.Windows.CaptionButtonRegion>>? SetCaptionButtons { get; init; }

    /// <summary>
    /// The control the page's CSS coordinates are relative to — required only when
    /// <see cref="SetCaptionButtons"/> is set. Normally the WebView2 itself. Its
    /// <c>DeviceDpi</c> is what converts CSS px to physical px, per-monitor under PerMonitorV2.
    /// </summary>
    public Control? CoordinateSpace { get; init; }
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
/// Lives in Shenora.Windows (not Shenora.Windows) because the commands arrive over the
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

    /// <summary>Window commands over IPC. Every route is opt-in: an unset callback answers NO_HANDLER.</summary>
    public WindowCommandFacade(WindowCommandOptions options, Microsoft.Extensions.Logging.ILogger<WindowCommandFacade>? logger = null)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // One marshalling owner (P5.5 H4.2). It also GUARDS the posted body, which matters here more
        // than anywhere: SET_THEME runs an app-supplied callback and CLOSE runs app FormClosing logic,
        // and an exception from either used to become an unhandled UI-thread exception.
        _ui = new Shenora.Windows.WinFormsUiDispatcher(_options.Window);
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
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

            case "SET_CAPTION_BUTTONS" when _options.SetCaptionButtons is { } setCaptionButtons:
                // The page re-sends this on every layout change, so it must be cheap and total: a
                // stale rectangle silently moves the hit-test away from the button the user sees,
                // which presents as "the close button sometimes does nothing".
                var regions = ParseCaptionButtons(request.Payload);
                Post(() => setCaptionButtons(regions));
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
    /// <para>
    /// CONSEQUENCE for the two handoff routes, accepted deliberately: because a transport dispatches
    /// on the UI thread, <c>SendMessage(WM_NCLBUTTONDOWN)</c> runs inline and blocks for the WHOLE OS
    /// move/size loop, so their <c>Done()</c> reaches the page only after the user releases the mouse
    /// (a drag past the client's request timeout rejects a promise whose work already succeeded). That
    /// is the cost of correct mouse-down timing — do NOT "fix" it by forcing a post, which is the very
    /// regression the paragraph above records. It also means a test must dispatch these two routes
    /// from a worker thread or it enters the modal loop itself (P5.5 H7).
    /// </para>
    /// </summary>
    private void Post(Action action) => _ui.Post(action);

    /// <summary>
    /// <c>{ buttons: [{ kind, x, y, width, height }] }</c> in CSS px → client-px regions.
    /// <para>
    /// Unknown kinds and malformed entries are SKIPPED rather than failing the whole call: the page
    /// sends this on every layout change, and rejecting a batch because one entry was odd would drop
    /// the other two buttons' hit-tests as collateral. An entry with no positive size is skipped for
    /// the same reason a zero-size zone is meaningless.
    /// </para>
    /// </summary>
    private IReadOnlyList<Shenora.Windows.CaptionButtonRegion> ParseCaptionButtons(JsonElement? payload)
    {
        var regions = new List<Shenora.Windows.CaptionButtonRegion>(3);
        if (payload is not { } root || root.ValueKind != JsonValueKind.Object) return regions;
        if (!root.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array) return regions;

        // Same conversion the drop-zone overlays use (DropZoneManager.ToFormBounds): CSS px →
        // physical px via the CONTROL's DeviceDpi — per-monitor under PerMonitorV2, where a
        // process-global scale factor is wrong on a mixed-DPI desktop.
        var space = _options.CoordinateSpace ?? _options.Window;
        var scale = Shenora.Windows.DpiHelper.ScaleFromDeviceDpi(space.DeviceDpi);

        foreach (var entry in buttons.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (ParseKind(entry) is not { } kind) continue;

            static int Px(JsonElement e, string name, double scale) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? (int)Math.Round(v.GetDouble() * scale)
                    : 0;

            var width = Px(entry, "width", scale);
            var height = Px(entry, "height", scale);
            if (width <= 0 || height <= 0) continue;

            var topLeft = new Point(Px(entry, "x", scale), Px(entry, "y", scale));
            // Through screen coordinates, because the page's origin is the CONTROL and the hit-test
            // works in the FORM's client space — identical whenever the WebView2 fills the form, and
            // correct when it does not.
            var client = _options.Window.PointToClient(space.PointToScreen(topLeft));
            regions.Add(new Shenora.Windows.CaptionButtonRegion(kind, new Rectangle(client.X, client.Y, width, height)));
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
