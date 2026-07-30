using Microsoft.Extensions.Logging;
// Inside namespace Shenora.WebView2 the bare identifier "WebView2" resolves to the namespace, so
// the control type needs an alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2;

/// <summary>
/// Transparent overlay that captures OS file drag-drop events for one zone, ported from the
/// primary desktop sibling. Internal — <see cref="DropZoneManager"/> owns the lifecycle.
///
/// Visibility logic:
/// - Mouse outside the zone → always visible (ready to catch a drag).
/// - Mouse inside + dragging → visible (the drag takes precedence).
/// - Mouse inside + not dragging → hidden, after a DOM occlusion check (so hover effects on the
///   page's elements still work — the overlay would eat them).
/// Mouse tracking is driven by the page's mouseenter/mouseleave (the SHOW route) — native
/// MouseLeave alone is unreliable through the WebView.
/// </summary>
internal sealed class DropZoneOverlay : Panel
{
    private readonly ILogger _logger;
    private readonly WebView2Control _webView;
    private readonly Action<string[], Point> _onFileDrop;
    private readonly Action _onDragEnter;
    private readonly Action _onDragLeave;

    private bool _mouseIsInside;
    private bool _isDragging;
    private bool _pendingOcclusionCheck;
    private bool _isDisposed;
    private bool _formIsActive = true;

    // The overlay can be disposed (zone unregistered / navigation / form close) while an async
    // occlusion check or a queued SHOW/form-event is still pending. Touching Visible/Handle/
    // Bounds after that throws ObjectDisposedException — every control-mutating path checks
    // this first (found live in the source).
    private bool Dead => _isDisposed || IsDisposed;

    public DropZoneOverlay(string zoneId, WebView2Control webView, ILogger logger,
        Action<string[], Point> onFileDrop, Action onDragEnter, Action onDragLeave)
    {
        ZoneId = zoneId;
        _webView = webView;
        _logger = logger;
        _onFileDrop = onFileDrop;
        _onDragEnter = onDragEnter;
        _onDragLeave = onDragLeave;

        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        AllowDrop = true;

        DragEnter += OnZoneDragEnter;
        DragDrop += OnZoneDragDrop;
        DragLeave += OnZoneDragLeave;
        DragOver += OnZoneDragOver;
        MouseEnter += OnZoneMouseEnter;
        MouseLeave += OnZoneMouseLeave;

        // Visible until the mouse enters — an overlay that starts hidden never catches a drag.
        Visible = true;
    }

    public string ZoneId { get; }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Nothing — the overlay is a transparent hit surface.
    }

    /// <summary>The page reports the mouse left the element (the reliable path — see class doc).</summary>
    public void OnFrontendMouseLeave()
    {
        _mouseIsInside = false;
        ShowOverlay("frontend mouse left");
    }

    /// <summary>
    /// Parent-form activation sync: an INACTIVE form always shows the overlay — that is what
    /// makes drag-drop FROM OTHER APPS work while this app is in the background.
    /// </summary>
    public void SetFormActive(bool isActive)
    {
        _formIsActive = isActive;
        if (!isActive)
        {
            if (!_isDragging) ShowOverlay("form inactive");
        }
        else if (_mouseIsInside && !_isDragging)
        {
            HideOverlay("form active, mouse inside");
        }
        else
        {
            ShowOverlay("form active, mouse outside");
        }
    }

    public void UpdateOverlayBounds(int x, int y, int width, int height)
    {
        if (Dead) return;
        if (Left != x || Top != y || Width != width || Height != height)
            SetBounds(x, y, width, height);
    }

    private void OnZoneMouseEnter(object? sender, EventArgs e)
    {
        _mouseIsInside = true;
        if (!Visible) return;
        // Hide when the mouse enters so the page's hover effects work — the element's own
        // mouseenter fires through the (transparent) overlay even on an inactive form.
        if (!_isDragging) HideOverlay("mouse entered overlay");
    }

    private void OnZoneMouseLeave(object? sender, EventArgs e)
    {
        _mouseIsInside = false;
        // Don't show here — the page sends SHOW when its element's mouseleave fires.
    }

    private void OnZoneDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            _isDragging = true;
            _onDragEnter();
            CheckOcclusion(); // a hidden/covered zone must not light up under the drag
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnZoneDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnZoneDragLeave(object? sender, EventArgs e)
    {
        _isDragging = false;
        _onDragLeave();
        if (_mouseIsInside) HideOverlay("drag ended, mouse inside");
    }

    private void OnZoneDragDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                var clientPos = PointToClient(new Point(e.X, e.Y));
                _onFileDrop(files, clientPos);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling drop on zone {ZoneId}", ZoneId);
        }
        finally
        {
            _isDragging = false;
            if (_mouseIsInside) HideOverlay("drag ended, mouse inside");
        }
    }

    /// <summary>
    /// Ask the DOM whether the zone element is actually visible where the overlay sits — a
    /// dialog/panel covering it must also suppress the overlay, or drags land on covered UI.
    /// </summary>
    private async void CheckOcclusion()
    {
        if (_pendingOcclusionCheck) return;
        _pendingOcclusionCheck = true;
        try
        {
            // The zone id is app-supplied and reaches a script: JSON-inject + CSS.escape, never
            // raw interpolation (the webview2-hosting injection rule — a quote in the id would
            // break the selector and fail the check open forever).
            var zoneIdJson = System.Text.Json.JsonSerializer.Serialize(ZoneId);
            var script = $$"""
                (function() {
                    const zoneId = {{zoneIdJson}};
                    const elem = document.querySelector('[data-drop-zone-id="' + CSS.escape(zoneId) + '"]');
                    if (!elem) return true;
                    const rect = elem.getBoundingClientRect();
                    if (rect.width === 0 || rect.height === 0) return true;
                    const testPoints = [
                        { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 },
                        { x: rect.left + 10, y: rect.top + 10 },
                        { x: rect.right - 10, y: rect.top + 10 },
                        { x: rect.left + 10, y: rect.bottom - 10 },
                        { x: rect.right - 10, y: rect.bottom - 10 }
                    ];
                    for (const point of testPoints) {
                        const topElement = document.elementFromPoint(point.x, point.y);
                        if (topElement && (topElement === elem || elem.contains(topElement))) continue;
                        if (topElement) return true;
                    }
                    return false;
                })();
                """;

            var result = await _webView.ExecuteScriptAsync(script);
            if (Dead) return; // torn down during the await — do not touch the control

            if (string.Equals(result?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                HideOverlay("occluded");
            else
                ShowOverlay("not occluded");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Occlusion check failed for zone {ZoneId}", ZoneId);
            ShowOverlay("occlusion check error"); // fail open — a drop target beats a hover glitch
        }
        finally
        {
            _pendingOcclusionCheck = false;
        }
    }

    private void ShowOverlay(string reason)
    {
        if (Dead || Visible) return;
        Visible = true;
        BringToFront();
        _logger.LogTrace("Zone {ZoneId} shown: {Reason} (formActive: {FormActive})", ZoneId, reason, _formIsActive);
    }

    private void HideOverlay(string reason)
    {
        if (Dead || !Visible) return;
        Visible = false;
        _logger.LogTrace("Zone {ZoneId} hidden: {Reason} (formActive: {FormActive})", ZoneId, reason, _formIsActive);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            DragEnter -= OnZoneDragEnter;
            DragDrop -= OnZoneDragDrop;
            DragLeave -= OnZoneDragLeave;
            DragOver -= OnZoneDragOver;
            MouseEnter -= OnZoneMouseEnter;
            MouseLeave -= OnZoneMouseLeave;
        }
        base.Dispose(disposing);
    }
}
