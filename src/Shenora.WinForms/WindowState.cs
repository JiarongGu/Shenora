namespace Shenora.WinForms;

/// <summary>
/// A window's persisted geometry in LOGICAL px (DPI-independent) plus the maximized flag.
/// Null size falls back to the configured default; position is meaningful only as a pair.
/// The DPI is deliberately NOT part of the state — it is resolved fresh every launch (each
/// launch can be a different monitor/DPI), see <see cref="WindowStateManager"/>.
/// </summary>
public sealed record WindowState(int? Width, int? Height, int? X, int? Y, bool Maximized);

/// <summary>Defaults and limits for <see cref="WindowStateManager"/>. All family-proven values.</summary>
public sealed class WindowStateOptions
{
    /// <summary>Default logical size used when no state is saved.</summary>
    public int DefaultWidth { get; init; } = 1280;

    /// <inheritdoc cref="DefaultWidth"/>
    public int DefaultHeight { get; init; } = 800;

    /// <summary>Minimum logical window size (also applied as the form's DPI-scaled MinimumSize).</summary>
    public int MinWidth { get; init; } = 800;

    /// <inheritdoc cref="MinWidth"/>
    public int MinHeight { get; init; } = 600;

    /// <summary>
    /// Minimum PHYSICAL px of the window that must overlap some monitor for a saved position to
    /// be reused — at least a grabbable title strip, so an unplugged/rearranged monitor's
    /// off-screen position never strands the window (it re-centers instead).
    /// </summary>
    public int MinVisibleWidth { get; init; } = 120;

    /// <inheritdoc cref="MinVisibleWidth"/>
    public int MinVisibleHeight { get; init; } = 60;
}

/// <summary>
/// Where <see cref="WindowState"/> lives. Apps with their own settings pipeline implement this
/// over it; simple apps use <see cref="JsonFileWindowStateStore"/>. Implementations must be
/// best-effort: window state is a nicety — never throw from <see cref="Save"/>.
/// </summary>
public interface IWindowStateStore
{
    /// <summary>The saved state, or null when absent/unreadable.</summary>
    WindowState? Load();

    /// <summary>Persist the state (best-effort — swallow storage failures).</summary>
    void Save(WindowState state);
}
