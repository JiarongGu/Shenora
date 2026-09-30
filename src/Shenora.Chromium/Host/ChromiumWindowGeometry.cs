using System.Drawing;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The main window's saved size and place (<see cref="ChromiumHostOptions.WindowState"/>): planned as the window
/// opens, written as it closes. CEF's Views measure in device-independent pixels on every OS, which is what a
/// <see cref="WindowState"/> holds, so the numbers pass through unscaled; the DPI of the display a window lands on is
/// CEF's to apply. UI thread.
/// <para>
/// CEF has no "restore bounds" for a maximized window, so the shell keeps the bounds the window last SETTLED at while
/// neither maximized, minimized nor full screen, and those are what a maximized window saves. Settled, because macOS
/// animates a zoom and reports each frame as a normal window's bounds until the last (measured: a maximize from the
/// page saved 1673×949, a frame of the animation, as the size to restore to).
/// </para>
/// </summary>
internal sealed unsafe class ChromiumWindowGeometry(IWindowStateStore store, WindowStateOptions options, ILogger? log)
{
    /// <summary>What a window opens at: its size, its place (null only when no display is known, and CEF places it),
    /// and whether it opens maximized.</summary>
    internal readonly record struct Plan(int Width, int Height, int? X, int? Y, bool Maximized);

    /// <summary>How long normal bounds must hold to count as where the window was left, rather than a frame of an
    /// animation passing through (one frame is ~16 ms; a zoom, a few hundred).</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private Rectangle? _normal;               // where the window was last left while normal
    private (Rectangle Bounds, long At)? _latest;   // the newest normal bounds, not yet known to have settled
    private bool _maximized;

    /// <summary>The window's minimum size, which a restored size is floored at too.</summary>
    public Size Minimum => new(options.MinWidth, options.MinHeight);

    /// <summary>Where this window's state is kept.</summary>
    internal IWindowStateStore Store => store;

    /// <summary>
    /// The plan for a window whose own size is <paramref name="width"/>×<paramref name="height"/>, against the
    /// displays' work areas, primary first. Size: a saved one, else the window's own, never below the options'
    /// minimum, and shrunk to its display's work area. Place: the saved one when enough of the window lands on a work
    /// area to grab; otherwise centred on the primary display's. Always a place when a display is known, so the window
    /// has the bounds it restores to from the start, a maximized one included.
    /// </summary>
    internal static Plan PlanFor(WindowState? saved, WindowStateOptions options, int width, int height, IReadOnlyList<Rectangle> workAreas)
    {
        var w = Math.Max(saved?.Width ?? width, options.MinWidth);
        var h = Math.Max(saved?.Height ?? height, options.MinHeight);
        var maximized = saved?.Placement == WindowPlacement.Maximized;
        if (saved is { X: { } sx, Y: { } sy } && Target(workAreas, sx, sy, w, h) is { } savedArea)
        {
            var (sw, sh) = Fit(w, h, savedArea, options);
            if (Reachable(sx, sy, sw, sh, workAreas, options)) return new Plan(sw, sh, sx, sy, maximized);
        }
        if (workAreas.Count == 0) return new Plan(w, h, null, null, maximized);
        var primary = workAreas[0];
        var (cw, ch) = Fit(w, h, primary, options);
        return new Plan(cw, ch, primary.X + (primary.Width - cw) / 2, primary.Y + (primary.Height - ch) / 2, maximized);
    }

    // Shrunk to the work area when the options say so, never below the minimum.
    private static (int Width, int Height) Fit(int width, int height, Rectangle area, WindowStateOptions options) =>
        options.MaxToWorkArea
            ? (Math.Min(width, Math.Max(options.MinWidth, area.Width)), Math.Min(height, Math.Max(options.MinHeight, area.Height)))
            : (width, height);

    /// <summary>What the window saves: its normal bounds (the last it had while normal, when it is not normal now),
    /// and whether it is maximized. Null when there is nothing to save.</summary>
    internal static WindowState? StateFor(Rectangle? normal, Rectangle current, bool isNormal, bool maximized)
    {
        var bounds = isNormal ? current : normal ?? current;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        return new WindowState(bounds.Width, bounds.Height, bounds.X, bounds.Y, maximized ? WindowPlacement.Maximized : WindowPlacement.Normal);
    }

    /// <summary>The plan for this window, from what the store holds and the displays there are now. Its place is
    /// also the window's normal bounds until they change, so a window closed while still maximized saves what it
    /// restores to.</summary>
    public Plan Restore(int width, int height)
    {
        WindowState? saved = null;
        try { saved = store.Load(); }
        catch (Exception ex) { AppCallback.Log(log, () => "[Shenora.Chromium] The window state could not be read; the window opens at its own size", LogLevel.Warning, ex); }
        var plan = PlanFor(saved, options, width, height, WorkAreas());
        if (plan is { X: { } x, Y: { } y }) _normal = new Rectangle(x, y, plan.Width, plan.Height);
        _maximized = plan.Maximized;
        return plan;
    }

    /// <summary>The window moved, resized, maximized or restored: keep its bounds while it is normal, and whether it is
    /// maximized while it shows, since a minimized or hidden window can say "not maximized" of one that is.</summary>
    public void Changed(_cef_window_t* window, _cef_rect_t bounds) =>
        Observe(Shows(window), IsNormal(window), window->is_maximized(window) == 1,
            new Rectangle(bounds.x, bounds.y, bounds.width, bounds.height), Environment.TickCount64);

    /// <summary>
    /// One change at <paramref name="now"/> (ms). Normal bounds wait to be known as settled: bounds that held for
    /// <see cref="Settle"/> by the next change (or by the close) are kept, and bounds a change to not-normal (a
    /// maximize) replaced sooner are dropped, so the frames an animation passes through never become the size to
    /// restore to. A window that does not show (minimized, hidden) changes nothing but what has settled.
    /// </summary>
    internal void Observe(bool shows, bool normal, bool maximized, Rectangle bounds, long now)
    {
        SettleBy(now);
        if (!shows) return;
        _maximized = maximized;
        _latest = normal ? (bounds, now) : null;
    }

    /// <summary>
    /// What closing at <paramref name="now"/> saves: the window's state as <see cref="Save"/> reads it. Maximized if
    /// the window says so, or said so while it last showed (a minimized or hidden one can deny it). A window
    /// maximized as it closes drops its newest normal-looking bounds rather than settling them: they are its
    /// maximize's last frame, which macOS reports as a normal window's and may follow with no maximized change at all
    /// (measured: 2 zooms in 15 then saved the full-screen frame as the size to restore to).
    /// </summary>
    internal WindowState? Closing(bool shows, bool normal, bool maximized, Rectangle current, long now)
    {
        var isMaximized = maximized || (!shows && _maximized);
        if (isMaximized) _latest = null;
        else SettleBy(now);
        return StateFor(_normal, current, shows && normal, isMaximized);
    }

    private void SettleBy(long now)
    {
        if (_latest is { } latest && now - latest.At >= (long)Settle.TotalMilliseconds) { _normal = latest.Bounds; _latest = null; }
    }

    /// <summary>The window is closing: save its state. Never throws; a store that fails is logged.</summary>
    public void Save(_cef_window_t* window)
    {
        var view = (_cef_view_t*)window;
        var current = view->get_bounds_in_screen(view);
        var state = Closing(Shows(window), IsNormal(window), window->is_maximized(window) == 1,
            new Rectangle(current.x, current.y, current.width, current.height), Environment.TickCount64);
        if (state is null) return;
        AppCallback.Run(() => store.Save(state),
            ex => AppCallback.Log(log, () => "[Shenora.Chromium] The window state could not be saved", LogLevel.Warning, ex));
    }

    // On screen as itself: neither minimized nor hidden (the tray's close hides it).
    private static bool Shows(_cef_window_t* window) =>
        ((_cef_view_t*)window)->is_visible((_cef_view_t*)window) == 1 && window->is_minimized(window) != 1;

    private static bool IsNormal(_cef_window_t* window) =>
        window->is_maximized(window) != 1 && window->is_minimized(window) != 1 && window->is_fullscreen(window) != 1;

    // The work areas of every display, the primary's first: the fallback a centred window lands on.
    private static List<Rectangle> WorkAreas()
    {
        var areas = new List<Rectangle>();
        var count = Cef.cef_display_get_count();
        if (count == 0) return areas;
        var displays = new _cef_display_t*[(int)count];
        long? primaryId;
        using (var primary = new CefRef<_cef_display_t>(Cef.cef_display_get_primary()))
            primaryId = primary.IsNull ? null : primary.Ptr->get_id(primary.Ptr);
        fixed (_cef_display_t** all = displays)
        {
            var got = count;
            Cef.cef_display_get_alls(&got, all);
            for (var i = 0; i < (int)Math.Min(got, count); i++)
            {
                using var display = new CefRef<_cef_display_t>(all[i]);
                if (display.IsNull) continue;
                var area = display.Ptr->get_work_area(display.Ptr);
                var rect = new Rectangle(area.x, area.y, area.width, area.height);
                if (display.Ptr->get_id(display.Ptr) == primaryId) areas.Insert(0, rect);
                else areas.Add(rect);
            }
        }
        return areas;
    }

    // The work area to size against: the one holding the saved top-left, else the one it overlaps most, else the first.
    private static Rectangle? Target(IReadOnlyList<Rectangle> workAreas, int px, int py, int width, int height)
    {
        if (workAreas.Count == 0) return null;
        Rectangle? best = null;
        var bestOverlap = 0;
        foreach (var area in workAreas)
        {
            if (area.Contains(px, py)) return area;
            var overlap = Overlap(area, px, py, width, height);
            var size = overlap.Width * overlap.Height;
            if (size > bestOverlap) { bestOverlap = size; best = area; }
        }
        return best ?? workAreas[0];
    }

    // Enough of the window on some work area to grab it: a strip of the options' size.
    private static bool Reachable(int x, int y, int width, int height, IReadOnlyList<Rectangle> workAreas, WindowStateOptions options)
    {
        foreach (var area in workAreas)
        {
            var overlap = Overlap(area, x, y, width, height);
            if (overlap.Width >= options.MinVisibleWidth && overlap.Height >= options.MinVisibleHeight) return true;
        }
        return false;
    }

    private static Size Overlap(Rectangle area, int x, int y, int width, int height) =>
        new(Math.Max(0, Math.Min(x + width, area.Right) - Math.Max(x, area.Left)),
            Math.Max(0, Math.Min(y + height, area.Bottom) - Math.Max(y, area.Top)));
}
