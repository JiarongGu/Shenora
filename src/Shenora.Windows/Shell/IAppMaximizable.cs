using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// A window whose maximized state <see cref="Form.WindowState"/> does not fully say. Implement it on a form that
/// maximizes its own way, and <see cref="WindowStateManager"/> and the page's caption prefer it over the WinForms
/// properties. <see cref="OptimizedForm"/> implements it so that a window minimized from maximized is saved as
/// maximized.
/// <para>
/// 🔴 <b>A form that maximizes by hand, keeping <c>WindowState.Normal</c>, must implement it</b>: reading the WinForms
/// properties persisted <c>Maximized: false</c> together with the WORK-AREA rect, so the next launch filled the work
/// area believing it was not maximized, and clicking maximize captured the work-area rect as the restore bounds, making
/// RESTORE A PERMANENT NO-OP.
/// </para>
/// </summary>
public interface IAppMaximizable
{
    /// <summary>The authoritative placement, however this window implements it.</summary>
    WindowPlacement AppPlacement { get; }

    /// <summary>
    /// The bounds to restore to when un-maximizing — the window's real windowed geometry, which is
    /// what must be persisted instead of the current (work-area-sized) bounds. It may be empty when the
    /// window has never been maximized, in which case the current bounds are already correct.
    /// </summary>
    Rectangle AppRestoreBounds { get; }
}
