using System.Text.Json;

namespace Shenora.Core.Shell;

/// <summary>How a window is sized against its monitor, beyond its restore geometry. An enum rather than a
/// <c>bool</c> because full screen is a real third state, and adding an enum member is additive where
/// widening a persisted <c>bool</c> is not.</summary>
public enum WindowPlacement
{
    /// <summary>Ordinary windowed geometry — the <see cref="WindowState"/> rect is the whole truth.</summary>
    Normal,

    /// <summary>Filling the monitor's WORK AREA (taskbar excluded). The rect is what to restore to.</summary>
    Maximized,
}

/// <summary>
/// A window's persisted geometry in LOGICAL px (DPI-independent) plus how it is placed, which both desktop shells
/// save and restore (D88). Null size falls back to the shell's default (<see cref="WindowStateOptions.DefaultWidth"/>
/// in the WinForms shell, the window's own size in the Chromium shell); position is meaningful only as a pair. The
/// DPI is deliberately NOT part of the state — it is resolved fresh every launch.
/// <para>
/// ⚠ <b>This record IS the on-disk format</b> (<see cref="JsonFileWindowStateStore"/> serialises it), so
/// its shape is a compatibility surface with state saved by earlier versions.
/// </para>
/// </summary>
/// <param name="Width">The restored width in logical px; null for the default.</param>
/// <param name="Height">The restored height in logical px; null for the default.</param>
/// <param name="X">The left edge in logical px; with <paramref name="Y"/>, or neither.</param>
/// <param name="Y">The top edge in logical px; with <paramref name="X"/>, or neither.</param>
/// <param name="Placement">Maximized or not; the rect is what a maximized window restores to.</param>
public sealed record WindowState(int? Width, int? Height, int? X, int? Y, WindowPlacement Placement);

/// <summary>Defaults and limits for restoring a <see cref="WindowState"/>.</summary>
public sealed class WindowStateOptions
{
    /// <summary>Default logical size used when no state is saved, in the WinForms shell. The Chromium shell opens
    /// its window at <c>ChromiumWindowOptions</c>' own size instead.</summary>
    public int DefaultWidth { get; init; } = 1280;

    /// <inheritdoc cref="DefaultWidth"/>
    public int DefaultHeight { get; init; } = 800;

    /// <summary>Minimum logical window size: a restored size never goes below it, and the WinForms shell also
    /// applies it as the form's DPI-scaled MinimumSize.</summary>
    public int MinWidth { get; init; } = 800;

    /// <inheritdoc cref="MinWidth"/>
    public int MinHeight { get; init; } = 600;

    /// <summary>How much of the window (physical px in the WinForms shell, logical in the Chromium shell) must
    /// overlap some monitor for a saved position to be reused — at least a grabbable title strip, so an unplugged
    /// monitor's off-screen position never strands the window (it re-centers instead).</summary>
    public int MinVisibleWidth { get; init; } = 120;

    /// <inheritdoc cref="MinVisibleWidth"/>
    public int MinVisibleHeight { get; init; } = 60;

    /// <summary>
    /// Shrink the restored width/height to the target monitor's work area when a size saved on a bigger display
    /// would overflow a smaller one. Default true — a window bigger than its monitor is one the user cannot resize
    /// back down. The <see cref="MinWidth"/>/<see cref="MinHeight"/> floor still applies, and position is validated
    /// separately, against <see cref="MinVisibleWidth"/>/<see cref="MinVisibleHeight"/>.
    /// </summary>
    public bool MaxToWorkArea { get; init; } = true;
}

/// <summary>
/// Where <see cref="WindowState"/> lives — implement it over an app's own settings pipeline, or use
/// <see cref="JsonFileWindowStateStore"/>. ⚠ Implementations must be best-effort: never throw from
/// <see cref="Save"/>, which runs on the close path.
/// </summary>
public interface IWindowStateStore
{
    /// <summary>The saved state, or null when absent/unreadable.</summary>
    WindowState? Load();

    /// <summary>Persist the state (best-effort — swallow storage failures).</summary>
    void Save(WindowState state);
}

/// <summary>
/// The simplest <see cref="IWindowStateStore"/>: one JSON file (e.g. <c>window.json</c> in one of the app's data
/// areas). Both directions are best-effort — a corrupt or unwritable file must never break startup or close.
/// </summary>
public sealed class JsonFileWindowStateStore(string filePath) : IWindowStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The persisted state, or null when absent OR unreadable — a corrupt file must not stop startup.</summary>
    public WindowState? Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<WindowState>(File.ReadAllText(filePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persist the state. Best-effort: a failure here must never take the app down on exit.</summary>
    public void Save(WindowState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(state, Json));
        }
        catch
        {
            // window state is a nicety — never block close on it
        }
    }
}

/// <summary>Main-window state persistence: <c>WindowsHostOptions.WindowState</c> and
/// <c>ChromiumHostOptions.WindowState</c>.</summary>
public sealed class WindowStateHostOptions
{
    /// <summary>
    /// Where the state lives (e.g. a <see cref="JsonFileWindowStateStore"/> under one of the
    /// app's data areas). Required — the framework does not invent a storage location.
    /// </summary>
    public required Func<IServiceProvider, IWindowStateStore> Store { get; init; }

    /// <summary>Sizing defaults/minimums. Null = <see cref="WindowStateOptions"/> defaults.</summary>
    public WindowStateOptions? Options { get; init; }
}
