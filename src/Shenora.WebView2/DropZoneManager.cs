using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora.Core;
// Inside namespace Shenora.WebView2 the bare identifier "WebView2" resolves to the namespace, so
// the control type needs an alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2;

/// <summary>Inputs for <see cref="DropZoneManager"/>.</summary>
public sealed class DropZoneManagerOptions
{
    /// <summary>The WebView the zone elements live in (coordinate anchor + DOM occlusion checks).</summary>
    public required WebView2Control WebView { get; init; }

    /// <summary>The form the overlays are parented to (usually the WebView's form).</summary>
    public required Form ParentForm { get; init; }

    /// <summary>
    /// Where drop events are emitted (module <see cref="DropZoneManager.Module"/>, types
    /// DRAG_ENTER / DRAG_LEAVE / FILE_DROP). A <see cref="WebViewIpcBridge"/> wired to the same
    /// bus forwards them to the page, where <c>useDropZone</c> consumes them.
    /// </summary>
    public required IEventBus EventBus { get; init; }
}

/// <summary>
/// Native drag-drop zones synced to page elements, ported from the primary desktop sibling (its
/// third copy was already annotated "ported from…" — this ends that): transparent overlays are
/// positioned over the page's zone elements to capture REAL OS file paths (the page only ever
/// sees blob URLs), including drags from other apps while this one is in the background.
/// The client side is <c>useDropZone</c> in @shenora/react; the routes arrive through
/// <see cref="DropZoneFacade"/>.
///
/// Placed in Shenora.WebView2 (the design sketch said WinForms) because it drives the WebView —
/// coordinates anchor on the control and occlusion checks run DOM scripts — and the facade
/// needs Ipc, which the WinForms package deliberately doesn't reference.
/// </summary>
public sealed class DropZoneManager : IDisposable
{
    /// <summary>The reserved module name (mirrored by the client's <c>useDropZone</c>).</summary>
    public const string Module = "DROP_ZONE";

    private readonly DropZoneManagerOptions _options;
    private readonly ILogger<DropZoneManager> _logger;
    private readonly Shenora.Core.IUiDispatcher _ui;
    private readonly Dictionary<string, DropZoneOverlay> _overlays = [];
    // Last CSS bounds per zone — so a DPI change (window moved to another monitor) can re-derive
    // every overlay's physical bounds without waiting for the page to resend them (P2.3b).
    private readonly Dictionary<string, (int X, int Y, int Width, int Height)> _cssBounds = [];
    private bool _disposed;

    public DropZoneManager(DropZoneManagerOptions options, ILogger<DropZoneManager>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<DropZoneManager>.Instance;
        // One marshalling owner (P5.5 H4.2). The guarded body matters here: a posted overlay update
        // can throw ObjectDisposedException or a cross-thread error if the window closes between the
        // post and its execution, and that used to surface as an unhandled UI-thread exception.
        _ui = new Shenora.WinForms.WinFormsUiDispatcher(_options.ParentForm,
            ex => _logger.LogWarning(ex, "Drop-zone UI work failed."));

        // Overlay visibility tracks form activation: an inactive form shows every overlay —
        // that's what makes background drag-drop from other apps work.
        _options.ParentForm.Deactivate += OnFormDeactivate;
        _options.ParentForm.Activated += OnFormActivated;
        // Window moved to a monitor with a different DPI: every stored CSS rect maps to new
        // physical bounds. The page's own resize path converges on the same result; this covers
        // the gap until it does.
        _options.ParentForm.DpiChanged += OnDpiChanged;

        // Zones belong to the DOCUMENT that registered them, so they are cleared when a new document
        // begins loading. The core may not exist yet — an app constructs this before the (slow)
        // WebView2 init — so hook whichever is true now and let initialization finish the job.
        if (_options.WebView.CoreWebView2 is not null) HookDocumentChange();
        else _options.WebView.CoreWebView2InitializationCompleted += OnCoreInitialized;
    }

    private void OnCoreInitialized(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
    {
        // A failed init has no core to hook, and the host already surfaces that failure loudly.
        if (e.IsSuccess) HookDocumentChange();
    }

    /// <summary>
    /// Clear the zones when a new document starts loading, so overlay lifetime follows the PAGE.
    /// <para>
    /// <c>ContentLoading</c>, never <c>NavigationStarting</c> — the same choice, for the same reason,
    /// as the IPC ready gate (P5.5 H3): <c>NavigationStarting</c> also fires for navigations that
    /// never replace the document (one a policy cancels, one that fails before committing), and
    /// destroying the live page's overlays for those would be a bug.
    /// </para>
    /// <para>
    /// This replaces clearing on the READY handshake, which was ORDER-DEPENDENT and silently wrong:
    /// a <c>REGISTER</c> that arrived before <c>READY</c> was wiped AFTER being acked, so the client
    /// believed its zone was live while the host had forgotten it, with nothing logged on either
    /// side. React runs CHILD effects before PARENT effects, so a root-component <c>notifyReady</c> —
    /// the obvious reading of "call it once at startup" — produced exactly that. Keying on the
    /// document instead cannot race the client at all, because the clear happens before the new page
    /// can send anything.
    /// </para>
    /// </summary>
    private void HookDocumentChange()
    {
        if (_options.WebView.CoreWebView2 is not { } core) return;
        // Detach-then-attach so this is IDEMPOTENT: CoreWebView2InitializationCompleted can fire more
        // than once (a retried init after a failure), and a double subscription would leak a handler
        // and clear twice per navigation. Removing a handler that was never added is a no-op.
        core.ContentLoading -= OnContentLoading;
        core.ContentLoading += OnContentLoading;
    }

    private void OnContentLoading(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2ContentLoadingEventArgs e) => ClearAll();

    /// <summary>Create (or re-bounds) the overlay for a zone. CSS (logical) pixels in.</summary>
    public void RegisterZone(string zoneId, int cssX, int cssY, int cssWidth, int cssHeight)
    {
        if (MarshalToUi(() => RegisterZone(zoneId, cssX, cssY, cssWidth, cssHeight))) return;
        ArgumentException.ThrowIfNullOrEmpty(zoneId);
        _cssBounds[zoneId] = (cssX, cssY, cssWidth, cssHeight);

        if (_overlays.TryGetValue(zoneId, out var existing))
        {
            var (x, y, w, h) = ToFormBounds(cssX, cssY, cssWidth, cssHeight);
            existing.UpdateOverlayBounds(x, y, w, h);
            return;
        }

        try
        {
            var overlay = new DropZoneOverlay(zoneId, _options.WebView, _logger,
                (files, pos) => NotifyFileDrop(zoneId, files, pos),
                () => NotifyDragEnter(zoneId),
                () => NotifyDragLeave(zoneId));
            var (x, y, w, h) = ToFormBounds(cssX, cssY, cssWidth, cssHeight);
            overlay.SetBounds(x, y, w, h);
            _options.ParentForm.Controls.Add(overlay);
            overlay.BringToFront();
            _overlays[zoneId] = overlay;
            _logger.LogDebug("Drop zone registered: {ZoneId} ({Count} total)", zoneId, _overlays.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create drop-zone overlay {ZoneId}", zoneId);
        }
    }

    /// <summary>Alias of <see cref="RegisterZone"/> — updating is registering with new bounds.</summary>
    public void UpdateZoneBounds(string zoneId, int cssX, int cssY, int cssWidth, int cssHeight) =>
        RegisterZone(zoneId, cssX, cssY, cssWidth, cssHeight);

    /// <summary>Tear a zone's overlay down.</summary>
    public void UnregisterZone(string zoneId)
    {
        if (MarshalToUi(() => UnregisterZone(zoneId))) return;
        _cssBounds.Remove(zoneId);
        if (_overlays.Remove(zoneId, out var overlay))
        {
            _options.ParentForm.Controls.Remove(overlay);
            overlay.Dispose();
            _logger.LogDebug("Drop zone unregistered: {ZoneId}", zoneId);
        }
    }

    /// <summary>The page's mouse left the zone element — show the overlay again (see the overlay's visibility logic).</summary>
    public void ShowOverlay(string zoneId)
    {
        if (MarshalToUi(() => ShowOverlay(zoneId))) return;
        if (_overlays.TryGetValue(zoneId, out var overlay))
            overlay.OnFrontendMouseLeave();
    }

    /// <summary>
    /// Remove every zone. You rarely need to call this: the manager clears itself whenever a new
    /// document starts loading, so a reloaded or navigated page simply re-registers its own.
    /// <para>
    /// It used to be the APP's job, from the ready handshake, and that carried an ordering contract
    /// sharp enough to need documenting in four places: a <c>REGISTER</c> arriving before <c>READY</c>
    /// was destroyed AFTER being acked, leaving the client sure its zone was live and the host with
    /// no record of it — silent on both sides. Keying on the document removed the contract rather
    /// than documenting it, so those warnings are gone; do not reintroduce a handshake-time clear.
    /// </para>
    /// </summary>
    public void ClearAll()
    {
        if (MarshalToUi(ClearAll)) return;
        foreach (var (_, overlay) in _overlays.ToList())
        {
            _options.ParentForm.Controls.Remove(overlay);
            overlay.Dispose();
        }
        _overlays.Clear();
        _cssBounds.Clear();
        _logger.LogDebug("All drop zones cleared");
    }

    /// <summary>Detach the form handlers and destroy every overlay.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Unhook FIRST: the source's handlers lingered when the form outlived the session and
        // fired on disposed overlays.
        _options.ParentForm.Deactivate -= OnFormDeactivate;
        _options.ParentForm.Activated -= OnFormActivated;
        _options.ParentForm.DpiChanged -= OnDpiChanged;
        _options.WebView.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        // Guarded: touching CoreWebView2 after the control is disposed throws, and a manager
        // outliving its WebView is exactly the teardown order this runs in.
        try
        {
            if (!_options.WebView.IsDisposed && _options.WebView.CoreWebView2 is { } core)
                core.ContentLoading -= OnContentLoading;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Detaching the document-change handler failed (the browser is already gone).");
        }
        ClearAll();
    }

    private void OnFormDeactivate(object? sender, EventArgs e)
    {
        foreach (var overlay in _overlays.Values) overlay.SetFormActive(false);
    }

    private void OnFormActivated(object? sender, EventArgs e)
    {
        foreach (var overlay in _overlays.Values) overlay.SetFormActive(true);
    }

    private void OnDpiChanged(object? sender, DpiChangedEventArgs e) => ReapplyAllZoneBounds();

    /// <summary>Re-derive every overlay's physical bounds from the stored CSS rects. Internal seam for tests.</summary>
    internal void ReapplyAllZoneBounds()
    {
        if (MarshalToUi(ReapplyAllZoneBounds)) return;
        foreach (var (zoneId, css) in _cssBounds)
        {
            if (!_overlays.TryGetValue(zoneId, out var overlay)) continue;
            var (x, y, w, h) = ToFormBounds(css.X, css.Y, css.Width, css.Height);
            overlay.UpdateOverlayBounds(x, y, w, h);
        }
    }

    /// <summary>
    /// CSS (logical) pixels from getBoundingClientRect → physical pixels → parent-form client
    /// coordinates. PointToScreen/PointToClient are raw Win32 calls working in PHYSICAL pixels;
    /// at 150 % each CSS pixel is 1.5 physical. Uses the CONTROL's DeviceDpi — per-monitor under
    /// PerMonitorV2 (the source used a process-global scale factor, wrong on mixed-DPI setups).
    /// </summary>
    private (int X, int Y, int Width, int Height) ToFormBounds(int cssX, int cssY, int cssWidth, int cssHeight)
    {
        // DpiHelper owns device-DPI conversion (P5.5 H4.5) — reachable since the re-layer (D19), and
        // it guards a non-positive DeviceDpi, which the hand-rolled `/ 96.0` did not.
        var scale = Shenora.WinForms.DpiHelper.ScaleFromDeviceDpi(_options.WebView.DeviceDpi);
        var physX = (int)Math.Round(cssX * scale);
        var physY = (int)Math.Round(cssY * scale);
        var physW = (int)Math.Round(cssWidth * scale);
        var physH = (int)Math.Round(cssHeight * scale);
        var screen = _options.WebView.PointToScreen(new Point(physX, physY));
        var form = _options.ParentForm.PointToClient(screen);
        return (form.X, form.Y, physW, physH);
    }

    // Marshal to the UI thread NON-BLOCKING (BeginInvoke). Overlay management uses Win32/
    // WinForms calls (PointToScreen, Controls.Add) that are UI-thread-only — and a BLOCKING
    // Invoke from a worker thread can deadlock the UI (this caused an AppHang in the source when
    // IPC was dispatched off the UI thread). BeginInvoke never blocks the caller, so the manager
    // is safe to call from any thread. IsHandleCreated FIRST — pre-handle, InvokeRequired lies,
    // AND there is nothing to marshal to yet: return false so the caller proceeds inline
    // (re-invoking the caller here recursed without end — found in review).
    private bool MarshalToUi(Action action)
    {
        // TRUE  = handled here (posted, or deliberately dropped).
        // FALSE = the caller should proceed INLINE — and now that means only one thing: we are
        //         already on the UI thread. Re-invoking the caller from here recursed without end
        //         (found in review), which is why this returns a bool at all rather than calling back.
        if (_ui.IsOnUiThread) return false;

        if (_ui.Post(action)) return true;

        // Not Ready (no handle yet) or Gone. This used to fall into the inline path too — which ran
        // PointToScreen / Controls.Add ON A WORKER THREAD, forcing handle creation from the wrong
        // thread. Dropping is the correct answer: zones are registered by the page, which cannot
        // have loaded before the form was realized.
        _logger.LogDebug("Drop-zone UI work skipped — the host window is {State}.", _ui.State);
        return true;
    }

    // Wire events (the bridge forwards the bus to the page). Fire-and-forget: emission must
    // never block the drag/drop handlers.
    internal void NotifyDragEnter(string zoneId) =>
        _ = _options.EventBus.EmitAsync(Module, "DRAG_ENTER", new { ZoneId = zoneId });

    internal void NotifyDragLeave(string zoneId) =>
        _ = _options.EventBus.EmitAsync(Module, "DRAG_LEAVE", new { ZoneId = zoneId });

    internal void NotifyFileDrop(string zoneId, string[] files, Point position) =>
        _ = _options.EventBus.EmitAsync(Module, "FILE_DROP",
            new { ZoneId = zoneId, Files = files, Position = new { position.X, position.Y } });

    /// <summary>Internal seams for tests.</summary>
    internal bool HasZone(string zoneId) => _overlays.ContainsKey(zoneId);

    internal int ZoneCount => _overlays.Count;

    internal DropZoneOverlay? TryGetOverlay(string zoneId) =>
        _overlays.TryGetValue(zoneId, out var overlay) ? overlay : null;
}
