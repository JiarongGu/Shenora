using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// The ONE "bring this window to the front" sequence, for the single-instance activation filter,
/// <see cref="SecondaryWindows.Activate"/>, <see cref="TrayIcon"/>'s restore and the session
/// controller's reveal.
/// <para>
/// ⚠ <b>The ORDER is the part people get wrong:</b> un-minimize BEFORE activating (activating a
/// minimized window leaves it minimized), then <c>Show</c>/<c>Activate</c>/<c>BringToFront</c> for the
/// managed side, and finally <c>SetForegroundWindow</c> for the OS side — without which restoring while
/// another app holds the foreground leaves the window behind everything, visible only in the taskbar.
/// </para>
/// </summary>
internal static class WindowActivation
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    // The narrow pair, not the ...Ptr forms: GWL_EXSTYLE is 32-bit on every architecture and the Ptr
    // entry points do not exist on x86.
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
    /// <para>
    /// 🔴 <b><c>Form.ShowInTaskbar = true</c> destroys and recreates the HWND</b> — the flag is a
    /// <c>CreateParams</c> extended style and WinForms' setter calls <c>RecreateHandle()</c> (measured).
    /// Fine for an ordinary form, not for one HOSTING A LIVE WEBVIEW2, which is exactly where this is
    /// used. So the style is set directly; the hide/show either side is what makes the shell notice,
    /// since the taskbar only reads the flag when a window is shown.
    /// </para>
    /// <para>
    /// ⚠ <b>WinForms' own <c>ShowInTaskbar</c> still reports <c>false</c> afterwards</b>, so a later
    /// change that legitimately recreates the handle (<c>FormBorderStyle</c>, <c>TopMost</c>) rebuilds
    /// <c>CreateParams</c> from WinForms' state and drops the button again — call this again.
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
            // Racing teardown — a window that is going away does not need a taskbar button.
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
