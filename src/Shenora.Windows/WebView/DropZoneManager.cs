using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora;
using Shenora.Core.Events;
using Shenora.Core.Shell;
using Shenora.Engine.Files;
// `WebView2` alone resolves to the NAMESPACE in here, hence the alias.
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

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
/// Native drag-drop zones synced to page elements: transparent overlays are positioned over the page's
/// zone elements to capture REAL OS file paths, including drags from other apps while this one is in
/// the background. The client side is <c>useDropZone</c> in @shenora/react; the routes arrive through
/// <see cref="DropZoneModule"/>.
///
/// WHY NOT HTML5 DROP: a page-side drop yields a <c>File</c> whose only accessor is its CONTENT, so the
/// bytes must be read into the renderer and pushed across the IPC boundary — a full copy of every
/// dropped file, eagerly, before the app knows whether it wants any of them. A path is a string: open
/// it lazily, stream it, hash it incrementally, move or link it without copying.
/// </summary>
public sealed class DropZoneManager : IDisposable
{
    /// <summary>The reserved module name (mirrored by the client's <c>useDropZone</c>).</summary>
    public const string Module = "SHENORA.DROPZONE";

    /// <summary>Event: the pointer entered a zone while dragging: <c>{ zoneId }</c>.</summary>
    public const string DragEnterEvent = "DRAG_ENTER";

    /// <summary>Event: the pointer left a zone, or the drag ended elsewhere: <c>{ zoneId }</c>.</summary>
    public const string DragLeaveEvent = "DRAG_LEAVE";

    /// <summary>Event: files were dropped: <c>{ zoneId, files, position }</c>. The payload the whole
    /// mechanism exists to deliver — a page cannot learn a dropped file's PATH any other way.
    /// </summary>
    public const string FileDropEvent = "FILE_DROP";

    private readonly DropZoneManagerOptions _options;
    private readonly ILogger<DropZoneManager> _logger;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;
    private readonly Dictionary<string, DropZoneOverlay> _overlays = [];
    // Last CSS bounds per zone, so a DPI change can re-derive every overlay's physical bounds without
    // waiting for the page to resend them.
    private readonly Dictionary<string, (int X, int Y, int Width, int Height)> _cssBounds = [];
    private bool _disposed;

    /// <summary>Zones clear on DOCUMENT change, hooked here — so there is no ordering contract to remember.</summary>
    public DropZoneManager(DropZoneManagerOptions options, ILogger<DropZoneManager>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<DropZoneManager>.Instance;
        // The one marshalling owner. Its guarded body matters here: a posted overlay update throws if
        // the window closes between the post and its execution, and there is no caller on that stack.
        _ui = new Shenora.Windows.WinFormsUiDispatcher(_options.ParentForm,
            ex => _logger.LogWarning(ex, "Drop-zone UI work failed."));

        // Overlay visibility tracks form activation: an INACTIVE form shows every overlay — that is
        // what makes background drag-drop from other apps work.
        _options.ParentForm.Deactivate += OnFormDeactivate;
        _options.ParentForm.Activated += OnFormActivated;
        // A monitor with a different DPI remaps every stored CSS rect to new physical bounds.
        _options.ParentForm.DpiChanged += OnDpiChanged;

        // The core may not exist yet — an app constructs this before the (slow) WebView2 init — so hook
        // whichever is true now and let initialization finish the job.
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
    /// 🔴 <c>ContentLoading</c>, never <c>NavigationStarting</c> — the latter also fires for navigations
    /// that never replace the document (one a policy cancels, one that fails before committing), and
    /// destroying the live page's overlays for those would be a bug. Same choice as the IPC ready gate.
    /// </para>
    /// <para>
    /// ⚠ Never key this on the READY handshake instead: a <c>REGISTER</c> arriving before <c>READY</c>
    /// would be wiped AFTER being acked, leaving the client sure its zone is live and the host with no
    /// record of it, silent on both sides. React runs CHILD effects before PARENT effects, so a
    /// root-component <c>notifyReady</c> produces exactly that.
    /// </para>
    /// </summary>
    private void HookDocumentChange()
    {
        if (_options.WebView.CoreWebView2 is not { } core) return;
        // Detach-then-attach so this is IDEMPOTENT: CoreWebView2InitializationCompleted can fire more
        // than once (a retried init), and a double subscription would clear twice per navigation.
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
    /// document starts loading, so a reloaded or navigated page simply re-registers its own. ⚠ Do not
    /// reintroduce a handshake-time clear — see <see cref="HookDocumentChange"/> for why it races.
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
        // Unhook FIRST — a form outliving the manager otherwise fires these on disposed overlays.
        _options.ParentForm.Deactivate -= OnFormDeactivate;
        _options.ParentForm.Activated -= OnFormActivated;
        _options.ParentForm.DpiChanged -= OnDpiChanged;
        _options.WebView.CoreWebView2InitializationCompleted -= OnCoreInitialized;
        // Guarded: touching CoreWebView2 after the control is disposed throws, and a manager outliving
        // its WebView is exactly the teardown order this runs in.
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
    /// coordinates. PointToScreen/PointToClient are raw Win32 calls working in PHYSICAL pixels; at
    /// 150 % each CSS pixel is 1.5 physical. Uses the CONTROL's DeviceDpi — per-monitor under
    /// PerMonitorV2, where a process-global scale factor is wrong on a mixed-DPI desktop.
    /// </summary>
    private (int X, int Y, int Width, int Height) ToFormBounds(int cssX, int cssY, int cssWidth, int cssHeight)
    {
        // DpiHelper owns device-DPI conversion and guards a non-positive DeviceDpi.
        var scale = Shenora.Windows.DpiHelper.ScaleFromDeviceDpi(_options.WebView.DeviceDpi);
        var physX = (int)Math.Round(cssX * scale);
        var physY = (int)Math.Round(cssY * scale);
        var physW = (int)Math.Round(cssWidth * scale);
        var physH = (int)Math.Round(cssHeight * scale);
        var screen = _options.WebView.PointToScreen(new Point(physX, physY));
        var form = _options.ParentForm.PointToClient(screen);
        return (form.X, form.Y, physW, physH);
    }

    // Marshal overlay work to the UI thread NON-BLOCKING: PointToScreen/Controls.Add are UI-thread-only
    // and a BLOCKING Invoke from a worker thread can deadlock the UI, so the manager stays callable
    // from any thread.
    //   TRUE  = handled here (posted, or deliberately dropped).
    //   FALSE = the caller should proceed INLINE, which now means only "we are already on the UI
    //           thread" — this returns a bool rather than calling back because re-invoking the caller
    //           from here recursed without end.
    private bool MarshalToUi(Action action)
    {
        if (_ui.IsOnUiThread) return false;

        if (_ui.Post(action)) return true;

        // ⚠ Not Ready (no handle yet) or Gone — DROP, never fall through to the inline path, which
        // would run PointToScreen / Controls.Add on a worker thread and force handle creation there.
        // Zones are registered by the page, which cannot have loaded before the form was realized.
        _logger.LogDebug("Drop-zone UI work skipped — the host window is {State}.", _ui.State);
        return true;
    }

    // Wire events (the bridge forwards the bus to the page). `Emit`, not `_ = EmitAsync(…)`: every one
    // of these is reached from a WinForms drag event, where an escaping exception has no caller on the
    // stack, and Emit is the member that says discarding is safe without reading the bus.
    internal void NotifyDragEnter(string zoneId) =>
        _options.EventBus.Emit(Module, DragEnterEvent, new { ZoneId = zoneId });

    internal void NotifyDragLeave(string zoneId) =>
        _options.EventBus.Emit(Module, DragLeaveEvent, new { ZoneId = zoneId });

    internal void NotifyFileDrop(string zoneId, string[] files, Point position) =>
        _options.EventBus.Emit(Module, FileDropEvent,
            new { ZoneId = zoneId, Files = files, Position = new { position.X, position.Y } });

    /// <summary>Internal seams for tests.</summary>
    internal bool HasZone(string zoneId) => _overlays.ContainsKey(zoneId);

    internal int ZoneCount => _overlays.Count;

    internal DropZoneOverlay? TryGetOverlay(string zoneId) =>
        _overlays.TryGetValue(zoneId, out var overlay) ? overlay : null;
}
