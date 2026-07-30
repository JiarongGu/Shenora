using System.Runtime.InteropServices;

namespace Shenora.WinForms;

/// <summary>
/// The ONE "bring this window to the front" sequence. It existed four times — the single-instance
/// activation filter, <see cref="SecondaryWindows.Activate"/>, <see cref="TrayIcon"/>'s restore, and
/// the session controller's reveal — and each copy was incomplete in a DIFFERENT way, which is what
/// made this worth collapsing rather than just noting (P5.5 H4.5). The tray copy in particular
/// omitted <c>SetForegroundWindow</c>, so restoring from the tray while another app held the
/// foreground could leave the window behind everything — visible in the taskbar, not on screen.
/// <para>
/// Order matters and is the part people get wrong: <b>un-minimize BEFORE activating</b> (activating a
/// minimized window leaves it minimized), then <c>Show</c>/<c>Activate</c>/<c>BringToFront</c> for the
/// managed side, and finally <c>SetForegroundWindow</c> for the OS side — WinForms' own methods do
/// not always cross the foreground-permission boundary.
/// </para>
/// </summary>
internal static class WindowActivation
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Restore, show and foreground <paramref name="form"/>. Safe to call on a disposed/unrealized
    /// form (no-op). MUST be called on the form's own UI thread — callers marshal first.
    /// </summary>
    internal static void BringToFront(Form form)
    {
        if (form is null || form.IsDisposed) return;
        try
        {
            // Un-minimize first: Activate() on a minimized window does not restore it.
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            if (!form.Visible) form.Show();
            form.Activate();
            form.BringToFront();
            if (form.IsHandleCreated) SetForegroundWindow(form.Handle);
        }
        catch (Exception)
        {
            // Racing teardown — bringing a dying window forward is a no-op, never an error.
        }
    }
}
