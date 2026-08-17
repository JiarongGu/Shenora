namespace Shenora.Windows;

/// <summary>
/// How a window is sized against its monitor, beyond its restore geometry.
/// <para>
/// ⚠ An ENUM rather than a <c>bool</c> because the third state is real: a media or streaming window
/// wants FULL SCREEN (the whole monitor, no work-area inset), which is a different thing from
/// maximized. It is not implemented yet, so it is not a member yet — but adding one to an enum is
/// ADDITIVE, while widening a <c>bool</c> that is also a PERSISTED FIELD is not.
/// </para>
/// </summary>
public enum WindowPlacement
{
    /// <summary>Ordinary windowed geometry — the <see cref="WindowState"/> rect is the whole truth.</summary>
    Normal,

    /// <summary>Filling the monitor's WORK AREA (taskbar excluded). The rect is what to restore to.</summary>
    Maximized,
}

/// <summary>
/// A window's persisted geometry in LOGICAL px (DPI-independent) plus how it is placed.
/// Null size falls back to the configured default; position is meaningful only as a pair.
/// The DPI is deliberately NOT part of the state — it is resolved fresh every launch (each
/// launch can be a different monitor/DPI), see <see cref="WindowStateManager"/>.
/// <para>
/// ⚠ <b>This record IS the on-disk format</b> (<see cref="JsonFileWindowStateStore"/> serialises it),
/// so its shape is a compatibility surface with state saved by earlier versions. State written before
/// <see cref="Placement"/> existed carries a <c>maximized</c> boolean this does not read: it
/// deserialises to <see cref="WindowPlacement.Normal"/>, so such a window opens windowed ONCE and is
/// correct from the next save on. Geometry is unaffected.
/// </para>
/// </summary>
public sealed record WindowState(int? Width, int? Height, int? X, int? Y, WindowPlacement Placement);

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

    /// <summary>
    /// Shrink the restored physical width/height to the target monitor's work area when a size
    /// saved on a bigger display would overflow a smaller one (moving to a laptop, unplugging an
    /// external monitor). Default true — a window bigger than its monitor is one the user cannot
    /// resize back down, which is a worse state than a window slightly smaller than they saved.
    /// The <see cref="MinWidth"/>/<see cref="MinHeight"/> floor still applies, and position is
    /// validated separately by <see cref="WindowStateManager.IsVisible"/>.
    /// </summary>
    public bool MaxToWorkArea { get; init; } = true;
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
