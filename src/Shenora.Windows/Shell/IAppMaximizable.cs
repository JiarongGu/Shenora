namespace Shenora.Windows;

/// <summary>
/// A window whose maximized state is NOT <see cref="Form.WindowState"/>.
/// <para>
/// Frameless custom chrome maximizes by hand — <c>SetWindowPos</c> to the monitor's work area, keeping
/// <c>WindowState.Normal</c> — because <c>WindowState.Maximized</c> on a borderless window leaves a
/// ~6 px gap per edge (measured). That is deliberate and correct, but it means
/// <c>Form.WindowState</c> and <c>Form.RestoreBounds</c> LIE about such a window, and
/// <see cref="WindowStateManager"/> was reading exactly those two properties.
/// </para>
/// <para>
/// The consequence was a P0 that was live in the reference composition (P5.5 H2): closing while
/// maximized persisted <c>Maximized: false</c> together with the WORK-AREA rect as the normal bounds.
/// On the next launch the window filled the work area while believing it was not maximized, so
/// <c>WM_NCCALCSIZE</c> took the normal-inset branch and the border gap the whole technique exists to
/// remove came back; the page's chrome glyph showed the wrong state; and clicking maximize captured
/// the work-area rect as the restore bounds, making RESTORE A PERMANENT NO-OP — the user could never
/// get a windowed size back except by dragging an edge.
/// </para>
/// <para>
/// Implement this on any form that manages its own maximize (<see cref="OptimizedForm"/> does when
/// <c>FramelessChrome</c> is on) and <see cref="WindowStateManager"/> will prefer it over the
/// WinForms properties.
/// </para>
/// </summary>
public interface IAppMaximizable
{
    /// <summary>The authoritative maximized state, however this window implements maximizing.</summary>
    bool IsAppMaximized { get; }

    /// <summary>
    /// The bounds to restore to when un-maximizing — the window's real windowed geometry, which is
    /// what must be persisted instead of the current (work-area-sized) bounds. Empty when the window
    /// has never been maximized, in which case the current bounds are already correct.
    /// </summary>
    Rectangle AppRestoreBounds { get; }
}
