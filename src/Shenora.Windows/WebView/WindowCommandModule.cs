using System.Runtime.InteropServices;
using System.Text.Json;
using Shenora.Core.Shell;
using Shenora.Core.Ipc;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="WindowCommandModule"/>.</summary>
public sealed class WindowCommandOptions
{
    /// <summary>The window the commands target — the form hosting the WebView2.</summary>
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
/// Threading: routes touch the form through the one UI dispatcher — correct from the transport's
/// UI-thread dispatch AND from programmatic sends off it.
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
    /// answers <c>NO_HANDLER</c>.</summary>
    public const string SetThemeType = "SET_THEME";

    /// <summary>Route: <c>{ buttons }</c>, the caption-button hit rectangles. Opt-in — unset
    /// <see cref="WindowCommandOptions.SetCaptionButtons"/> answers <c>NO_HANDLER</c>.</summary>
    public const string SetCaptionButtonsType = "SET_CAPTION_BUTTONS";

    // Borderless-window drag/resize: hand off to the OS window-move/-size loop — the page can't drive
    // native drag itself.
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

    private readonly WindowCommandOptions _options;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;

    /// <summary>Window commands over IPC. Every route is opt-in: an unset callback answers NO_HANDLER.</summary>
    public WindowCommandModule(WindowCommandOptions options, Microsoft.Extensions.Logging.ILogger<WindowCommandModule>? logger = null)
        : base(logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        // The one marshalling owner. It also GUARDS the posted body, which matters here: SET_THEME runs
        // an app-supplied callback and CLOSE runs app FormClosing logic, and an exception from either
        // has no caller on the stack.
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
            case MinimizeType:
                Post(() => form.WindowState = FormWindowState.Minimized);
                return Done();

            case ToggleMaximizeType:
                Post(_options.ToggleMaximize ?? (() =>
                    form.WindowState = form.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized));
                return Done();

            case CloseType:
                Post(form.Close);
                return Done();

            case IsMaximizedType:
                // Plain read — a bool snapshot is benign cross-thread.
                var maximized = _options.IsMaximized?.Invoke() ?? (form.WindowState == FormWindowState.Maximized);
                return Task.FromResult<object?>(new { Maximized = maximized });

            case StartDragType:
                // ⚠ Refused while maximized: a manual work-area maximize keeps WindowState.Normal, so
                // the OS would drag the maximized-size window with stale restore bounds. The page's
                // header restores first, as native caption drags do.
                if (_options.IsMaximized?.Invoke() ?? form.WindowState == FormWindowState.Maximized)
                    return Done();
                Post(() =>
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
                Post(() =>
                {
                    GetCursorPos(out var pt);
                    ReleaseCapture();
                    SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)hitTest, (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF)));
                });
                return Done();

            case SetThemeType when _options.ApplyTheme is { } applyTheme:
                var dark = PayloadHelper.GetOptionalValue<bool?>(request.Payload, "dark") ?? true;
                Post(() => applyTheme(dark));
                return Done();

            case SetCaptionButtonsType when _options.SetCaptionButtons is { } setCaptionButtons:
                // ⚠ A stale rectangle silently moves the hit-test away from the button the user sees,
                // which presents as "the close button sometimes does nothing".
                var regions = ParseCaptionButtons(request.Payload);
                Post(() => setCaptionButtons(regions));
                return Done();

            default:
                throw UnknownType(request);   // ModuleBase owns the shape
        }
    }

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
    private void Post(Action action) => _ui.Post(action);

    /// <summary>
    /// <c>{ buttons: [{ kind, x, y, width, height }] }</c> in CSS px → client-px regions. Unknown kinds,
    /// malformed entries and zero-size rectangles are SKIPPED rather than failing the whole call —
    /// rejecting a batch over one odd entry would drop the other buttons' hit-tests as collateral.
    /// </summary>
    private IReadOnlyList<Shenora.Windows.CaptionButtonRegion> ParseCaptionButtons(JsonElement? payload)
    {
        var regions = new List<Shenora.Windows.CaptionButtonRegion>(3);
        if (payload is not { } root || root.ValueKind != JsonValueKind.Object) return regions;
        if (!root.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Array) return regions;

        // CSS px → physical px via the CONTROL's DeviceDpi — per-monitor under PerMonitorV2, where a
        // process-global scale factor is wrong on a mixed-DPI desktop (same as
        // DropZoneManager.ToFormBounds).
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
            // Through screen coordinates: the page's origin is the CONTROL, the hit-test works in the
            // FORM's client space, and the two differ whenever the WebView2 does not fill the form.
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
