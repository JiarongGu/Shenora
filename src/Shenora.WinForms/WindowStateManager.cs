namespace Shenora.WinForms;

/// <summary>
/// Persists a form's size + position across restarts, DPI-correctly. Merged from the two proven
/// family implementations (one file-based, one settings-service-based — hence the
/// <see cref="IWindowStateStore"/> seam).
///
/// THE DPI RULE (the incident both sources earned independently): the process is PerMonitorV2,
/// where a WinForms form's OUTER size/position set in code is device px and is NOT auto-scaled
/// from a logical baseline — a value saved at one monitor's DPI would be the wrong physical size
/// at another. The fix: store LOGICAL px (physical ÷ the form's current-monitor DPI via
/// <c>Control.DeviceDpi</c> — the form may be on a secondary monitor) and restore as physical
/// (× the DPI resolved fresh THIS launch — the form's OWN monitor by default, via
/// <see cref="Apply(Form)"/>'s deferred <c>HandleCreated</c> resolution). The DPI itself is
/// never persisted. At 100% (96 DPI) every conversion is the identity. An off-screen saved
/// position (a monitor was unplugged/rearranged) is discarded and the window re-centers, and a
/// size saved on a bigger display shrinks to fit the target monitor's work area (see
/// <see cref="WindowStateOptions.MaxToWorkArea"/>).
///
/// CROSS-MONITOR MIXED-DPI (adopter-review 2026-08-01, empirically verified): the handle is
/// created wherever WinForms/Windows initially places the form - typically the primary monitor,
/// since <c>Location</c> hasn't been set yet - so <c>form.DeviceDpi</c> at <c>HandleCreated</c>
/// returns the PRIMARY's DPI, not the target monitor's, if the saved position is on a
/// different-DPI secondary. The deferred <see cref="Apply(Form)"/> path therefore MOVES the
/// handle to the saved position FIRST (Windows fires <c>WM_DPICHANGED</c> synchronously as the
/// move crosses monitors, updating <c>DeviceDpi</c> to the target), then resolves the scale
/// against that updated DPI. This is not optional and there is no auto-heal to fall back on:
/// verified live in <c>devtools/_dpi-probe/</c> that WinForms' default <c>WM_DPICHANGED</c>
/// handler does NOT rescale a Form's outer <c>Size</c> - Windows' <c>SuggestedRectangle</c>
/// comes back as the current width/height unchanged, and the handler leaves it alone.
/// Positioning first makes <c>DeviceDpi</c> authoritative before <c>Size</c> is computed.
/// </summary>
public sealed class WindowStateManager(IWindowStateStore store, WindowStateOptions? options = null)
{
    private readonly WindowStateOptions _options = options ?? new WindowStateOptions();

    /// <summary>
    /// Set the form's initial bounds from the saved state, DPI-corrected for this launch using
    /// the form's OWN monitor DPI. If the handle already exists the scale is resolved from
    /// <c>Control.DeviceDpi</c> and applied now; otherwise the whole apply is deferred to
    /// <see cref="Control.HandleCreated"/>, which fires before <c>Show</c> — so the restored size
    /// still lands on the initial paint without a resize flash. The 0.1.1 default resolved
    /// <see cref="DpiHelper.SystemScale"/> (the PRIMARY monitor) synchronously; adopters had to
    /// call <see cref="Apply(Form, double)"/> from <c>OnHandleCreated</c> with an explicit
    /// per-monitor scale to be accurate on a mixed-DPI setup (adopter, 2026-08-01: two pieces of
    /// kit-internal knowledge the adopter should not have owned — that <c>DeviceDpi</c> is the
    /// right source and that <c>OnHandleCreated</c> is the only moment it is valid).
    /// <para>
    /// Use <see cref="Apply(Form, double)"/> only when you need to size against a scale you
    /// resolve yourself (a test harness, a preview thumbnail against a different monitor's DPI).
    /// </para>
    /// </summary>
    public void Apply(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHandleCreated)
        {
            Apply(form, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi));
            return;
        }
        // Same shape as SecondaryWindows.Open's pre-handle intent (P5.5 H2): the marshal cannot
        // deliver anything before the handle exists, so defer to HandleCreated. Setting Size /
        // Location inside HandleCreated is still before OnLoad/OnShown, so the first paint sees
        // the restored geometry.
        //
        // Cross-monitor DPI trap (adopter-review, 2026-08-01, empirically verified): the handle
        // is created wherever WinForms/Windows initially places the form — typically the primary
        // monitor, since StartPosition/Location haven't been set yet. Reading form.DeviceDpi at
        // HandleCreated would return the PRIMARY monitor's DPI, not the target monitor's, so on
        // a mixed-DPI setup where the saved position is on a secondary monitor, sizing against
        // that DPI is wrong. MOVE the window to the saved position first (Windows sends
        // WM_DPICHANGED synchronously as the move crosses monitors, updating form.DeviceDpi to
        // the target monitor), THEN resolve the scale. WinForms' default WM_DPICHANGED handler
        // does NOT auto-rescale a Form's outer Size (verified in devtools/_dpi-probe/ — Windows'
        // SuggestedRectangle came back unchanged and the handler left Size alone), so there is
        // no self-heal to fall back on: this positioning step is load-bearing, not defence.
        void OnHandleCreated(object? sender, EventArgs e)
        {
            form.HandleCreated -= OnHandleCreated;
            PrePositionToTargetMonitor(form, store.Load());
            Apply(form, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi));
        }
        form.HandleCreated += OnHandleCreated;
    }

    /// <summary>
    /// Move the just-created handle to the saved position, if any, so <c>form.DeviceDpi</c>
    /// reflects the TARGET monitor before <see cref="Apply(Form, double)"/> reads it. A no-op if
    /// there is no saved position, or the saved position is off-screen (the caller's centre
    /// fallback handles that — leaving the window on its initial monitor is fine because that IS
    /// the monitor we'll end up on). The subsequent <see cref="Apply(Form, double)"/> sets
    /// <c>Location</c> to the same value again (idempotent).
    /// </summary>
    private static void PrePositionToTargetMonitor(Form form, WindowState? state)
    {
        if (state is not { X: { } x, Y: { } y }) return;
        var pt = new Point(x, y);
        // Cheap "does this point land on any real monitor?" check — Bounds, not WorkingArea,
        // because we're checking whether the DPI change is even meaningful, not whether the
        // window would be grabbable there. IsVisible + the caller's centre fallback own the
        // "usable position" question inside Apply.
        var onScreen = false;
        foreach (var screen in Screen.AllScreens)
            if (screen.Bounds.Contains(pt)) { onScreen = true; break; }
        if (!onScreen) return;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = pt;
    }

    /// <summary>
    /// The scale-explicit overload of <see cref="Apply(Form)"/>. See that method for the ordering
    /// contract; use this when you have a scale you want to size against directly (a test
    /// harness, a preview against a different monitor's DPI). The parameterless overload already
    /// resolves per-monitor DPI itself — most callers do not need this one.
    /// </summary>
    /// <param name="form">The form to size and place.</param>
    /// <param name="scale">The DPI scale to convert the stored logical bounds by (1.0 at 100%).</param>
    public void Apply(Form form, double scale)
    {
        ArgumentNullException.ThrowIfNull(form);
        // Don't clobber a minimum the FORM set for itself (P5.5 H2). The runner creates the form and
        // then calls Apply, so an app that sets MinimumSize in its constructor had it silently
        // replaced by these defaults — the reference composition's own 640x420 was dead code.
        if (form.MinimumSize == Size.Empty)
            form.MinimumSize = new Size(DpiHelper.Scale(_options.MinWidth, scale), DpiHelper.Scale(_options.MinHeight, scale));

        var (width, height, x, y, maximized) = ToPhysical(store.Load(), scale, _options, WorkAreas());
        form.Size = new Size(width, height);

        // Place the saved position even when maximized: WinForms maximizes onto the monitor
        // containing the pre-show bounds, so this is what keeps a maximized window on ITS
        // monitor across launches — and restore-down returns to the saved bounds instead of
        // re-centering (Save deliberately captures RestoreBounds for exactly this).
        var placed = false;
        if (x is { } px && y is { } py &&
            IsVisible(px, py, width, height, ScreenBounds(), _options))
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(px, py);
            placed = true;
        }
        if (!placed) form.StartPosition = FormStartPosition.CenterScreen;

        // Restore the maximized state through the window's OWN mechanism when it has one: setting
        // WindowState.Maximized on frameless chrome is exactly the ~6px-gap-per-edge bug its manual
        // work-area path exists to avoid (P5.5 H2). Deferred to first Shown either way — the marker
        // pattern was IAppMaximizable-only in 0.1.1, but adopter measurement (2026-08-01) showed a
        // plain Form with WindowState.Maximized set from Apply/OnHandleCreated goes back to Normal
        // by OnLoad, so the window opens restored-down however it was closed. Extend the same
        // deferral to plain forms via a one-shot Shown handler that consumes the same marker.
        if (maximized)
        {
            form.Tag = RestoreMaximizedTag;
            if (form is not IAppMaximizable) DeferMaximizeToShown(form);
        }
    }

    /// <summary>
    /// Marker <see cref="Control.Tag"/> value meaning "the saved state was maximized — apply your own
    /// maximize once you are shown". Deliberately a marker rather than a direct call: <c>Apply</c> runs
    /// BEFORE the window is realized, and a manual work-area maximize needs a real handle and a
    /// monitor to measure against.
    /// </summary>
    internal const string RestoreMaximizedTag = "shenora:restore-maximized";

    /// <summary>
    /// Consume <see cref="RestoreMaximizedTag"/> from <see cref="Form.Shown"/> for a plain form.
    /// <para>
    /// <see cref="IAppMaximizable"/> implementors (<see cref="OptimizedForm"/> is the one) override
    /// <c>OnShown</c> and consume the marker themselves — a plain <see cref="Form"/> cannot, so this
    /// subscribes a one-shot handler that applies <c>WindowState.Maximized</c> once the show
    /// sequence has finished (adopter, 2026-08-01: setting it earlier does not survive OnLoad).
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Attach the full lifecycle to <paramref name="form"/>: apply the saved geometry (per-monitor
    /// DPI accurate — via <see cref="Apply(Form)"/>, which defers to <c>HandleCreated</c> when the
    /// handle doesn't exist yet) and save it on <see cref="Form.FormClosed"/>.
    /// <para>
    /// Exists because the ORDERING is the contract and it was hand-written in two places (P5.5 H4.5):
    /// apply BEFORE the form is shown — geometry set after show causes a visible jump — and save on
    /// <c>FormClosed</c>, while the bounds are still readable. A caller who reverses those gets a
    /// window that flickers on open and forgets its position on close, with nothing failing loudly.
    /// </para>
    /// </summary>
    public void AttachTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Apply(form);
        form.FormClosed += (_, _) => Save(form);
    }

    /// <summary>
    /// The scale-explicit overload of <see cref="AttachTo(Form)"/>. Applies the geometry
    /// synchronously at the caller-supplied scale (a test harness, a preview against a different
    /// monitor's DPI). The parameterless overload already resolves per-monitor DPI itself — most
    /// callers do not need this one.
    /// </summary>
    public void AttachTo(Form form, double scale)
    {
        ArgumentNullException.ThrowIfNull(form);
        Apply(form, scale);
        form.FormClosed += (_, _) => Save(form);
    }

    /// <summary>
    /// Capture the form's CURRENT state as logical px and persist it (best-effort — never blocks
    /// close). Uses the restore-down bounds when maximized/minimized so the flag AND the normal
    /// size both survive. Call from FormClosing/FormClosed on the UI thread.
    /// </summary>
    public void Save(Form form)
    {
        try
        {
            // Prefer the window's OWN maximize truth when it manages maximizing itself (P5.5 H2).
            // Frameless chrome maximizes by hand and keeps WindowState.Normal, so reading
            // Form.WindowState/RestoreBounds persisted "not maximized" plus the WORK-AREA rect as the
            // normal size — which made restore a permanent no-op on the next launch. See
            // IAppMaximizable for the full failure chain.
            bool maximized;
            Rectangle bounds;
            if (form is IAppMaximizable app)
            {
                maximized = app.IsAppMaximized;
                bounds = maximized && app.AppRestoreBounds.Width > 0 ? app.AppRestoreBounds : form.Bounds;
                // Minimized still hides the real geometry behind RestoreBounds, whatever the chrome.
                if (form.WindowState == FormWindowState.Minimized && form.RestoreBounds.Width > 0)
                    bounds = form.RestoreBounds;
            }
            else
            {
                maximized = form.WindowState == FormWindowState.Maximized;
                bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            }
            WindowState state;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // The form's ACTUAL current-monitor DPI (could be a secondary monitor).
                state = ToLogical(bounds, maximized, DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi));
            }
            else
            {
                // Degenerate bounds (e.g. closed while minimized before first layout): keep the
                // previous geometry, update only the flag.
                state = (store.Load() ?? new WindowState(null, null, null, null, false)) with { Maximized = maximized };
            }
            store.Save(state);
        }
        catch
        {
            // window state is a nicety — never block close on it
        }
    }

    /// <summary>
    /// Pure conversion: stored LOGICAL state → PHYSICAL px at <paramref name="scale"/>. Nulls fall
    /// back to the default size; the minimum clamps in LOGICAL space; position converts only as a
    /// pair (an incomplete pair can't place a window — the caller centers instead).
    /// <para>
    /// This overload does NOT clamp to a monitor's work area, so a size saved on a big display can
    /// restore larger than a smaller display can show. Prefer
    /// <see cref="ToPhysical(WindowState?, double, WindowStateOptions, IEnumerable{Rectangle})"/>
    /// when work areas are known — that is what <see cref="Apply(Form)"/> uses.
    /// </para>
    /// </summary>
    public static (int Width, int Height, int? X, int? Y, bool Maximized) ToPhysical(
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
        return (width, height, x, y, state?.Maximized ?? false);
    }

    /// <summary>
    /// The work-area-clamped overload of
    /// <see cref="ToPhysical(WindowState?, double, WindowStateOptions)"/>. When
    /// <see cref="WindowStateOptions.MaxToWorkArea"/> is on, the physical width/height shrink to
    /// the target monitor's work area — the one the saved position falls on (or overlaps most,
    /// or the first, in that order). The MinWidth/MinHeight floor still applies. Position is not
    /// clamped: unreachable positions are already handled by <see cref="IsVisible"/> and the
    /// caller's centre fallback, which is a separate concern.
    /// </summary>
    public static (int Width, int Height, int? X, int? Y, bool Maximized) ToPhysical(
        WindowState? state, double scale, WindowStateOptions options, IEnumerable<Rectangle> workAreas)
    {
        var (width, height, x, y, maximized) = ToPhysical(state, scale, options);
        if (!options.MaxToWorkArea) return (width, height, x, y, maximized);

        var target = PickTarget(workAreas, x, y, width, height);
        if (target is not { } area) return (width, height, x, y, maximized);

        var s = scale > 0 ? scale : 1.0;
        var minW = (int)Math.Round(options.MinWidth * s);
        var minH = (int)Math.Round(options.MinHeight * s);
        width = Math.Min(width, Math.Max(minW, area.Width));
        height = Math.Min(height, Math.Max(minH, area.Height));
        return (width, height, x, y, maximized);
    }

    /// <summary>
    /// The work area to clamp against: the one containing the saved top-left; else the one with
    /// the largest overlap with the candidate rect; else the first work area the caller yielded
    /// (<see cref="WorkAreas"/> deliberately yields the PRIMARY monitor first, so this fallback
    /// matches what happens when no position is saved and the window centres onto the primary).
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
    public static WindowState ToLogical(Rectangle bounds, bool maximized, double scale)
    {
        var s = scale > 0 ? scale : 1.0;
        return new WindowState(
            (int)Math.Round(bounds.Width / s), (int)Math.Round(bounds.Height / s),
            (int)Math.Round(bounds.X / s), (int)Math.Round(bounds.Y / s), maximized);
    }

    /// <summary>
    /// Pure check: at least a grabbable strip of the window (options' MinVisible, physical px)
    /// overlaps one of <paramref name="screens"/>.
    /// </summary>
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
    // GetMonitorInfo rect (winforms-shell.md); that is fine for a shrink-to-fit safety clamp,
    // which is over-conservative by a small margin rather than off by a monitor. Manual maximize
    // in OptimizedForm still uses GetMonitorInfo directly, where the ~12 px MATTERS (a visible gap
    // per edge is exactly what the manual path exists to remove).
    //
    // Primary FIRST — Screen.AllScreens is not documented to be primary-first (only that Primary
    // is one of its elements), and PickTarget's fallback picks the first work area it sees. A
    // "shrunk to the primary" fallback is defensible; a "shrunk to whichever monitor AllScreens
    // happened to yield first on this launch" fallback is not.
    private static IEnumerable<Rectangle> WorkAreas()
    {
        var primary = Screen.PrimaryScreen;
        if (primary is not null) yield return primary.WorkingArea;
        foreach (var screen in Screen.AllScreens)
            if (!ReferenceEquals(screen, primary)) yield return screen.WorkingArea;
    }
}
