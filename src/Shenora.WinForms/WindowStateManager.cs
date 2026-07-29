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

        if (maximized) form.WindowState = FormWindowState.Maximized;
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
            var maximized = form.WindowState == FormWindowState.Maximized;
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
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
