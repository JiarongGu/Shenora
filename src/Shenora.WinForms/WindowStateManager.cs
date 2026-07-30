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
/// (× the primary DPI resolved fresh THIS launch). The DPI itself is never persisted. At 100%
/// (96 DPI) every conversion is the identity. An off-screen saved position (a monitor was
/// unplugged/rearranged) is discarded and the window re-centers.
/// </summary>
public sealed class WindowStateManager(IWindowStateStore store, WindowStateOptions? options = null)
{
    private readonly WindowStateOptions _options = options ?? new WindowStateOptions();

    /// <summary>
    /// Set the form's initial bounds from the saved state, DPI-corrected for this launch.
    /// No saved state → the centered default size. Also sets the DPI-scaled minimum size.
    /// Call BEFORE the form is shown (geometry set after show causes a visible jump).
    /// </summary>
    public void Apply(Form form)
    {
        var scale = DpiHelper.SystemScale();
        // Don't clobber a minimum the FORM set for itself (P5.5 H2). The runner creates the form and
        // then calls Apply, so an app that sets MinimumSize in its constructor had it silently
        // replaced by these defaults — the reference composition's own 640x420 was dead code.
        if (form.MinimumSize == Size.Empty)
            form.MinimumSize = new Size(DpiHelper.Scale(_options.MinWidth, scale), DpiHelper.Scale(_options.MinHeight, scale));

        var (width, height, x, y, maximized) = ToPhysical(store.Load(), scale, _options);
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
    public void AttachTo(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        Apply(form);
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
}
