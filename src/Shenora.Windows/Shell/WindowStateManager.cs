namespace Shenora.Windows;

/// <summary>
/// Persists a form's size + position across restarts, DPI-correctly.
///
/// THE DPI RULE: the process is PerMonitorV2, where a form's OUTER size/position set in code is device
/// px and is NOT auto-scaled from a logical baseline — a value saved at one monitor's DPI is the wrong
/// physical size at another. So the store holds LOGICAL px (physical ÷ the form's current-monitor
/// <c>Control.DeviceDpi</c>) and restore multiplies by the DPI resolved fresh THIS launch. The DPI
/// itself is never persisted; at 100% (96 DPI) every conversion is the identity. An off-screen saved
/// position (a monitor was unplugged or rearranged) is discarded and the window re-centers, and a size
/// saved on a bigger display shrinks to the target monitor's work area
/// (<see cref="WindowStateOptions.MaxToWorkArea"/>).
///
/// 🔴 CROSS-MONITOR MIXED DPI: the handle is created wherever Windows first places the form — typically
/// the PRIMARY monitor, since <c>Location</c> is not set yet — so <c>form.DeviceDpi</c> read at
/// <c>HandleCreated</c> is the primary's, not the target's. <see cref="Apply(Form)"/> therefore MOVES
/// the handle to the saved position FIRST (the move fires <c>WM_DPICHANGED</c> synchronously as it
/// crosses monitors, updating <c>DeviceDpi</c>), then resolves the scale against that. Nothing
/// self-heals it afterwards: WinForms' default <c>WM_DPICHANGED</c> handler does NOT rescale a Form's
/// outer <c>Size</c> — Windows' <c>SuggestedRectangle</c> comes back unchanged and the handler leaves
/// it alone.
/// </summary>
public sealed class WindowStateManager(IWindowStateStore store, WindowStateOptions? options = null)
{
    private readonly WindowStateOptions _options = options ?? new WindowStateOptions();

    /// <summary>
    /// Set the form's initial bounds from the saved state, DPI-corrected for this launch using the
    /// form's OWN monitor DPI. If the handle already exists the scale is resolved from
    /// <c>Control.DeviceDpi</c> and applied now; otherwise the whole apply defers to
    /// <see cref="Control.HandleCreated"/>, which fires before <c>Show</c> — so the restored size still
    /// lands on the initial paint without a resize flash.
    /// </summary>
    public void Apply(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHandleCreated)
        {
            Apply(form, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi));
            return;
        }
        // HandleCreated is still before OnLoad/OnShown, so the first paint sees the restored geometry.
        //
        // 🔴 POSITION BEFORE RESOLVING THE SCALE — the cross-monitor DPI trap in the class doc: reading
        // form.DeviceDpi without moving first gives the PRIMARY monitor's DPI, and nothing self-heals it.
        void OnHandleCreated(object? sender, EventArgs e)
        {
            form.HandleCreated -= OnHandleCreated;
            PrePositionToTargetMonitor(form, store.Load());
            Apply(form, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi));
        }
        form.HandleCreated += OnHandleCreated;
    }

    /// <summary>
    /// Move the just-created handle to the saved position so <c>form.DeviceDpi</c> reflects the TARGET
    /// monitor before <see cref="Apply(Form, double)"/> reads it. No-op when there is no saved position
    /// or it is off-screen; the subsequent apply sets the same <c>Location</c> again (idempotent).
    /// </summary>
    private static void PrePositionToTargetMonitor(Form form, WindowState? state)
    {
        if (state is not { X: { } x, Y: { } y }) return;
        var pt = new Point(x, y);
        // Bounds, not WorkingArea: the question is whether the DPI change is meaningful, not whether the
        // window would be grabbable there. IsVisible + Apply's centre fallback own "usable position".
        var onScreen = false;
        foreach (var screen in Screen.AllScreens)
            if (screen.Bounds.Contains(pt)) { onScreen = true; break; }
        if (!onScreen) return;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = pt;
    }

    /// <summary>The scale-explicit overload of <see cref="Apply(Form)"/> — for a scale you resolve
    /// yourself (a test harness, a preview against a different monitor's DPI).</summary>
    /// <param name="form">The form to size and place.</param>
    /// <param name="scale">The DPI scale to convert the stored logical bounds by (1.0 at 100%).</param>
    public void Apply(Form form, double scale)
    {
        ArgumentNullException.ThrowIfNull(form);
        // Don't clobber a minimum the FORM set for itself: the runner creates the form and then calls
        // Apply, so an app setting MinimumSize in its constructor had it silently replaced.
        if (form.MinimumSize == Size.Empty)
            form.MinimumSize = new Size(DpiHelper.Scale(_options.MinWidth, scale), DpiHelper.Scale(_options.MinHeight, scale));

        var (width, height, x, y, placement) = ToPhysical(store.Load(), scale, _options, WorkAreas());
        form.Size = new Size(width, height);

        // Place the saved position even when maximized: WinForms maximizes onto the monitor containing
        // the pre-show bounds, so this keeps a maximized window on ITS monitor across launches — and
        // restore-down returns to the saved bounds instead of re-centering.
        var placed = false;
        if (x is { } px && y is { } py &&
            IsVisible(px, py, width, height, ScreenBounds(), _options))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(px, py);
            placed = true;
        }
        if (!placed) form.StartPosition = FormStartPosition.CenterScreen;

        // Restore maximized through the window's OWN mechanism when it has one: WindowState.Maximized on
        // frameless chrome is the ~6px-gap-per-edge bug its manual work-area path exists to avoid.
        // Deferred to first Shown either way — set earlier, a plain Form is back to Normal by OnLoad.
        if (placement != WindowPlacement.Normal)
        {
            form.Tag = RestoreMaximizedTag;
            if (form is not IAppMaximizable) DeferMaximizeToShown(form);
        }
    }

    /// <summary>
    /// Marker <see cref="Control.Tag"/> value meaning "the saved state was maximized — apply your own
    /// maximize once you are shown". A marker rather than a direct call because <c>Apply</c> runs BEFORE
    /// the window is realized, and a manual work-area maximize needs a real handle and a monitor.
    /// </summary>
    internal const string RestoreMaximizedTag = "shenora:restore-maximized";

    /// <summary>Consume <see cref="RestoreMaximizedTag"/> from <see cref="Form.Shown"/> for a plain form.
    /// <see cref="IAppMaximizable"/> implementors (<see cref="OptimizedForm"/>) consume it in
    /// <c>OnShown</c> themselves; a plain <see cref="Form"/> cannot, and <c>WindowState.Maximized</c> set
    /// earlier does not survive <c>OnLoad</c>.</summary>
    private static void DeferMaximizeToShown(Form form)
    {
        void OnShown(object? sender, EventArgs e)
        {
            form.Shown -= OnShown;
            if (!ReferenceEquals(form.Tag, RestoreMaximizedTag)) return;
            form.Tag = null;
            form.WindowState = FormWindowState.Maximized;
        }
        form.Shown += OnShown;
    }

    /// <summary>Apply the saved geometry (per-monitor DPI accurate, via <see cref="Apply(Form)"/>) and
    /// save it on <see cref="Form.FormClosed"/>. The ORDERING is the contract — apply BEFORE the form is
    /// shown (geometry set after causes a visible jump), save on <c>FormClosed</c> while the bounds are
    /// still readable. Reversed, the window flickers on open and forgets its position, silently.</summary>
    public void AttachTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Apply(form);
        form.FormClosed += (_, _) => Save(form);
    }

    /// <summary>The scale-explicit overload of <see cref="AttachTo(Form)"/> — applies the geometry
    /// synchronously at the caller-supplied scale (a test harness, a preview against another DPI).</summary>
    public void AttachTo(Form form, double scale)
    {
        ArgumentNullException.ThrowIfNull(form);
        Apply(form, scale);
        form.FormClosed += (_, _) => Save(form);
    }

    /// <summary>Capture the form's CURRENT state as logical px and persist it (best-effort — never blocks
    /// close). Uses the restore-down bounds when maximized/minimized so the flag AND the normal size both
    /// survive. Call from FormClosing/FormClosed on the UI thread.</summary>
    public void Save(Form form)
    {
        try
        {
            // Prefer the window's OWN maximize truth when it manages maximizing itself: frameless chrome
            // maximizes by hand and keeps WindowState.Normal, so Form.WindowState/RestoreBounds persists
            // "not maximized" plus the WORK-AREA rect, making restore a permanent no-op next launch.
            WindowPlacement placement;
            Rectangle bounds;
            if (form is IAppMaximizable app)
            {
                placement = app.AppPlacement;
                var appBounds = placement != WindowPlacement.Normal && app.AppRestoreBounds.Width > 0;
                bounds = appBounds ? app.AppRestoreBounds : form.Bounds;
                // Minimized hides the real geometry behind RestoreBounds — but only when the app's own
                // restore truth was NOT already taken. After a manual work-area maximize the fill was an
                // ordinary resize to WinForms, so Form.RestoreBounds holds the WORK-AREA rect; letting it
                // overwrite AppRestoreBounds here persisted that rect as the windowed size, and the next
                // launch's maximize re-derived its restore target from it — restore-down a permanent no-op.
                if (!appBounds && form.WindowState == FormWindowState.Minimized && form.RestoreBounds.Width > 0)
                    bounds = form.RestoreBounds;
            }
            else
            {
                placement = form.WindowState == FormWindowState.Maximized
                    ? WindowPlacement.Maximized : WindowPlacement.Normal;
                bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            }
            WindowState state;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // The form's ACTUAL current-monitor DPI (could be a secondary monitor).
                state = ToLogical(bounds, placement, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi));
            }
            else
            {
                // Degenerate bounds (closed while minimized before first layout): keep the previous
                // geometry, update only the flag.
                state = (store.Load() ?? new WindowState(null, null, null, null, WindowPlacement.Normal))
                    with { Placement = placement };
            }
            store.Save(state);
        }
        catch
        {
            // window state is a nicety — never block close on it
        }
    }

    /// <summary>
    /// Pure conversion: stored LOGICAL state → PHYSICAL px at <paramref name="scale"/>. Nulls fall back
    /// to the default size; the minimum clamps in LOGICAL space; position converts only as a pair (an
    /// incomplete pair can't place a window — the caller centers instead). Does NOT clamp to a work
    /// area — prefer
    /// <see cref="ToPhysical(WindowState?, double, WindowStateOptions, IEnumerable{Rectangle})"/>.
    /// <para>
    /// ⚠ <b>INTERNAL, and re-examined 2026-08-18 against the `DerivedCacheKey` mistake</b> — that type
    /// was demoted on "no consumer outside this assembly", which is unfalsifiable from inside the repo,
    /// and an adopter had one. The reason to keep THESE internal is a positive one instead: every
    /// Form-shaped use is already public — <c>Apply(form, scale)</c>, <c>AttachTo(form, scale)</c>,
    /// <c>Save(form)</c>, plus <see cref="WindowState"/> and <c>IWindowStateStore</c> for a custom
    /// store — so an app never has to reproduce this arithmetic to persist a window. And unlike a cache
    /// key, a wrong answer here is VISIBLE and self-correcting: a slightly misplaced window that the
    /// user moves and the next save fixes, not a silently orphaned cache.
    /// 🔴 If an adopter ever needs the conversion WITHOUT a Form, that is the harvest signal (D15) —
    /// promote it then, rather than guessing now.
    /// </para>
    /// </summary>
    internal static (int Width, int Height, int? X, int? Y, WindowPlacement Placement) ToPhysical(
        WindowState? state, double scale, WindowStateOptions options)
    {
        var s = scale > 0 ? scale : 1.0;
        var logicalWidth = Math.Max(state?.Width ?? options.DefaultWidth, options.MinWidth);
        var logicalHeight = Math.Max(state?.Height ?? options.DefaultHeight, options.MinHeight);
        var width = (int)Math.Round(logicalWidth * s);
        var height = (int)Math.Round(logicalHeight * s);

        int? x = null, y = null;
        if (state is { X: { } lx, Y: { } ly })
        {
            x = (int)Math.Round(lx * s);
            y = (int)Math.Round(ly * s);
        }
        return (width, height, x, y, state?.Placement ?? WindowPlacement.Normal);
    }

    /// <summary>
    /// The work-area-clamped overload of
    /// <see cref="ToPhysical(WindowState?, double, WindowStateOptions)"/>. With
    /// <see cref="WindowStateOptions.MaxToWorkArea"/> on, width/height shrink to the target monitor's
    /// work area (the one the saved position falls on, or overlaps most, or the first). The Min floor
    /// still applies; position is not clamped — <see cref="IsVisible"/> owns that.
    /// </summary>
    internal static (int Width, int Height, int? X, int? Y, WindowPlacement Placement) ToPhysical(
        WindowState? state, double scale, WindowStateOptions options, IEnumerable<Rectangle> workAreas)
    {
        var (width, height, x, y, placement) = ToPhysical(state, scale, options);
        if (!options.MaxToWorkArea) return (width, height, x, y, placement);

        var target = PickTarget(workAreas, x, y, width, height);
        if (target is not { } area) return (width, height, x, y, placement);

        var s = scale > 0 ? scale : 1.0;
        var minW = (int)Math.Round(options.MinWidth * s);
        var minH = (int)Math.Round(options.MinHeight * s);
        width = Math.Min(width, Math.Max(minW, area.Width));
        height = Math.Min(height, Math.Max(minH, area.Height));
        return (width, height, x, y, placement);
    }

    /// <summary>
    /// The work area to clamp against: the one containing the saved top-left; else the largest overlap
    /// with the candidate rect; else the first the caller yielded (<see cref="WorkAreas"/> yields the
    /// PRIMARY monitor first, matching where an unsaved window centres).
    /// </summary>
    private static Rectangle? PickTarget(IEnumerable<Rectangle> workAreas, int? x, int? y, int width, int height)
    {
        Rectangle? first = null;
        Rectangle? containing = null;
        Rectangle? bestOverlap = null;
        var bestOverlapArea = 0;
        foreach (var area in workAreas)
        {
            first ??= area;
            if (x is not { } px || y is not { } py) continue;
            if (containing is null && area.Contains(px, py)) containing = area;
            var ow = Math.Max(0, Math.Min(px + width, area.Right) - Math.Max(px, area.Left));
            var oh = Math.Max(0, Math.Min(py + height, area.Bottom) - Math.Max(py, area.Top));
            var overlap = ow * oh;
            if (overlap > bestOverlapArea) { bestOverlapArea = overlap; bestOverlap = area; }
        }
        return containing ?? bestOverlap ?? first;
    }

    /// <summary>Pure conversion: PHYSICAL bounds at <paramref name="scale"/> → stored LOGICAL state.</summary>
    internal static WindowState ToLogical(Rectangle bounds, WindowPlacement placement, double scale)
    {
        var s = scale > 0 ? scale : 1.0;
        return new WindowState(
            (int)Math.Round(bounds.Width / s), (int)Math.Round(bounds.Height / s),
            (int)Math.Round(bounds.X / s), (int)Math.Round(bounds.Y / s), placement);
    }

    /// <summary>Pure check: at least a grabbable strip of the window (options' MinVisible, physical px)
    /// overlaps one of <paramref name="screens"/>.</summary>
    public static bool IsVisible(int x, int y, int width, int height,
        IEnumerable<Rectangle> screens, WindowStateOptions options)
    {
        foreach (var b in screens)
        {
            var overlapW = Math.Min(x + width, b.Right) - Math.Max(x, b.Left);
            var overlapH = Math.Min(y + height, b.Bottom) - Math.Max(y, b.Top);
            if (overlapW >= options.MinVisibleWidth && overlapH >= options.MinVisibleHeight) return true;
        }
        return false;
    }

    private static IEnumerable<Rectangle> ScreenBounds()
    {
        foreach (var screen in Screen.AllScreens) yield return screen.Bounds;
    }

    // Managed WorkingArea is ~12 px short per edge on a HiDPI monitor vs the exact P/Invoke
    // GetMonitorInfo rect — fine for a shrink-to-fit clamp (over-conservative, not off by a monitor),
    // but OptimizedForm's manual maximize uses GetMonitorInfo, where the ~12 px MATTERS.
    //
    // Primary FIRST: Screen.AllScreens is not documented to be primary-first (only that Primary is one
    // of its elements), and PickTarget's fallback picks the first work area it sees.
    private static IEnumerable<Rectangle> WorkAreas()
    {
        var primary = Screen.PrimaryScreen;
        if (primary is not null) yield return primary.WorkingArea;
        foreach (var screen in Screen.AllScreens)
            if (!ReferenceEquals(screen, primary)) yield return screen.WorkingArea;
    }
}
