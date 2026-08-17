using System.Runtime.InteropServices;

namespace Shenora.Windows;

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

    // GetWindowLong/SetWindowLong rather than the ...Ptr forms: GWL_EXSTYLE holds a 32-bit value on
    // every architecture, and the Ptr entry points do not exist on x86 (they are macros there), so the
    // narrow pair is the portable one for this index.
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int SW_HIDE = 0, SW_SHOW = 5;

    /// <summary>
    /// Give <paramref name="form"/> a taskbar button, WITHOUT recreating its window handle.
    ///
    /// <para>
    /// 🔴 <b><c>Form.ShowInTaskbar = true</c> destroys and recreates the HWND</b>, because the flag is a
    /// <c>CreateParams</c> extended style and WinForms' setter calls <c>RecreateHandle()</c> whenever the
    /// handle already exists. Measured on this machine: the handle moved from <c>31595706</c> to
    /// <c>31661242</c> across the assignment. That is fine for an ordinary form and materially not fine
    /// for one HOSTING A LIVE WEBVIEW2 — the browser is bound to a parent HWND, and the one place this
    /// is reached (<c>SessionController.Reveal</c>) is precisely a window with a running browser in it,
    /// mid-session, at the moment a user is about to be shown it.
    /// </para>
    /// <para>
    /// So the style is set DIRECTLY. The hide/show either side is what makes the shell notice: the
    /// taskbar reads the flag when a window is shown, so flipping it on a visible window changes nothing
    /// until the next show. Neither call recreates a handle.
    /// </para>
    /// <para>
    /// ⚠ <b>WinForms' own <c>ShowInTaskbar</c> property still reports <c>false</c> afterwards</b>, and
    /// that is the honest cost of not going through it: a LATER change that legitimately recreates the
    /// handle (<c>FormBorderStyle</c>, <c>TopMost</c>) rebuilds <c>CreateParams</c> from WinForms' state
    /// and drops the button again. Acceptable where this is used — a session window is revealed once and
    /// then closed — and a caller that restyles a revealed window must call this again.
    /// </para>
    /// </summary>
    internal static void ShowTaskbarButton(Form form)
    {
        if (form is null || form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            var handle = form.Handle;
            var style = GetWindowLong(handle, GWL_EXSTYLE);
            if ((style & WS_EX_APPWINDOW) != 0) return;

            ShowWindow(handle, SW_HIDE);
            SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_APPWINDOW);
            ShowWindow(handle, SW_SHOW);
        }
        catch (Exception)
        {
            // Racing teardown, exactly as BringToFront: a window that is going away does not need a
            // taskbar button, and failing to give it one is never worth an exception on the UI thread.
        }
    }

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
