namespace Shenora.Windows;

/// <summary>
/// A window whose maximized state is NOT <see cref="Form.WindowState"/>. Implement it on any form that
/// manages its own maximize (<see cref="OptimizedForm"/> does when <c>FramelessChrome</c> is on) and
/// <see cref="WindowStateManager"/> prefers it over the WinForms properties.
/// <para>
/// 🔴 <b><c>Form.WindowState</c> and <c>Form.RestoreBounds</c> LIE about such a window</b>, because
/// frameless chrome maximizes by hand and keeps <c>WindowState.Normal</c>. Reading them instead
/// persisted <c>Maximized: false</c> together with the WORK-AREA rect: the next launch filled the work
/// area believing it was not maximized (so the border gap the technique exists to remove came back and
/// the page's glyph was wrong), and clicking maximize captured the work-area rect as the restore
/// bounds, making RESTORE A PERMANENT NO-OP.
/// </para>
/// </summary>
public interface IAppMaximizable
{
    /// <summary>The authoritative placement, however this window implements it.</summary>
    WindowPlacement AppPlacement { get; }

    /// <summary>
    /// The bounds to restore to when un-maximizing — the window's real windowed geometry, which is
    /// what must be persisted instead of the current (work-area-sized) bounds. Empty when the window
    /// has never been maximized, in which case the current bounds are already correct.
    /// </summary>
    Rectangle AppRestoreBounds { get; }
}
