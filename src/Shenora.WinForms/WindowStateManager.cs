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
/// (× the DPI resolved fresh THIS launch — the primary monitor by default, or the caller's
/// explicit scale for per-monitor accuracy: see <see cref="Apply(Form, double)"/>). The DPI itself
/// is never persisted. At 100% (96 DPI) every conversion is the identity. An off-screen saved
/// position (a monitor was unplugged/rearranged) is discarded and the window re-centers, and a
/// size saved on a bigger display shrinks to fit the target monitor's work area (see
/// <see cref="WindowStateOptions.MaxToWorkArea"/>).
/// </summary>
public sealed class WindowStateManager(IWindowStateStore store, WindowStateOptions? options = null)
{
    private readonly WindowStateOptions _options = options ?? new WindowStateOptions();

    /// <summary>
    /// Set the form's initial bounds from the saved state, DPI-corrected for this launch using the
    /// PRIMARY monitor's scale — usable before the form has a handle, so before any device DPI is
    /// available. Call BEFORE the form is shown (geometry set after show causes a visible jump).
    /// <para>
    /// For per-monitor accuracy on a mixed-DPI setup — the form's real monitor may not be the
    /// primary — call <see cref="Apply(Form, double)"/> from <c>OnHandleCreated</c> with
    /// <c>DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi)</c>. That is still before <c>Show</c> so
    /// there is no resize flash, and by then the handle sits on the actual target monitor.
    /// </para>
    /// </summary>
    public void Apply(Form form) => Apply(form, DpiHelper.SystemScale());

    /// <summary>
    /// The scale-explicit overload of <see cref="Apply(Form)"/>. See that method for the ordering
    /// contract; use this when you know the target monitor's DPI (e.g. from <c>form.DeviceDpi</c>
    /// after handle creation) and want the restored size sized against it instead of the primary
    /// monitor's.
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
        // work-area path exists to avoid (P5.5 H2). The form is not shown yet, so a manual maximize is
        // deferred to its first Shown — IAppMaximizable implementors apply it there.
        if (maximized && form is not IAppMaximizable)
            form.WindowState = FormWindowState.Maximized;
        else if (maximized)
            form.Tag = RestoreMaximizedTag;   // picked up by OptimizedForm on Shown
    }

    /// <summary>
    /// Marker <see cref="Control.Tag"/> value meaning "the saved state was maximized — apply your own
    /// maximize once you are shown". Deliberately a marker rather than a direct call: <c>Apply</c> runs
    /// BEFORE the window is realized, and a manual work-area maximize needs a real handle and a
    /// monitor to measure against.
    /// </summary>
    internal const string RestoreMaximizedTag = "shenora:restore-maximized";

    /// <summary>
    /// Attach the full lifecycle to <paramref name="form"/>: apply the saved geometry NOW and save it
    /// on <see cref="Form.FormClosed"/>.
    /// <para>
    /// Exists because the ORDERING is the contract and it was hand-written in two places (P5.5 H4.5):
    /// apply BEFORE the form is shown — geometry set after show causes a visible jump — and save on
    /// <c>FormClosed</c>, while the bounds are still readable. A caller who reverses those gets a
    /// window that flickers on open and forgets its position on close, with nothing failing loudly.
    /// </para>
    /// </summary>
    public void AttachTo(Form form) => AttachTo(form, DpiHelper.SystemScale());

    /// <summary>
    /// The scale-explicit overload of <see cref="AttachTo(Form)"/>, so a caller that wants
    /// per-monitor DPI accuracy (via <c>DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi)</c> from
    /// <c>OnHandleCreated</c>) does not lose the save-on-close ordering guarantee this method
    /// exists to protect (P5.5 H4.5) — that adopter used to have to hand-roll the FormClosed hook
    /// alongside <see cref="Apply(Form, double)"/>, re-introducing exactly the hazard AttachTo
    /// removes.
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
