using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The taskbar-button path, which exists only because the obvious one-liner is unsafe here.
/// </summary>
public class WindowActivationTests
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_APPWINDOW = 0x00040000;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>
    /// 🔴 <b>The premise, measured rather than argued</b> — and if this ever stops failing to recreate,
    /// the whole reason <c>ShowTaskbarButton</c> exists has gone and it should be deleted.
    /// <c>Form.ShowInTaskbar</c> is a <c>CreateParams</c> extended style, so WinForms' setter calls
    /// <c>RecreateHandle()</c> on an already-realized window. Harmless on an ordinary form; not harmless
    /// on the one place the kit reaches it, <c>SessionController.Reveal</c>, where the form is hosting a
    /// live WebView2 bound to that parent HWND.
    /// </summary>
    [Fact]
    public void The_property_this_avoids_really_does_recreate_the_handle() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        form.Location = new Point(-32000, -32000);
        form.Show();
        var before = form.Handle;

        form.ShowInTaskbar = true;

        Assert.NotEqual(before, form.Handle);
    });

    [Fact]
    public void ShowTaskbarButton_sets_the_style_and_keeps_the_handle() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        form.Location = new Point(-32000, -32000);
        form.Show();
        var before = form.Handle;
        Assert.Equal(0, GetWindowLong(before, GWL_EXSTYLE) & WS_EX_APPWINDOW);   // self-check

        WindowActivation.ShowTaskbarButton(form);

        Assert.Equal(before, form.Handle);                                       // the whole point
        Assert.NotEqual(0, GetWindowLong(form.Handle, GWL_EXSTYLE) & WS_EX_APPWINDOW);
    });

    [Fact]
    public void ShowTaskbarButton_is_a_no_op_on_a_window_that_is_going_away() => Sta.Run(() =>
    {
        // Reveal races teardown by construction: a driver can finish, or the user can close, between
        // the post and the body. Every other member here tolerates that, so this must too.
        var form = new Form();
        form.Show();
        form.Dispose();

        WindowActivation.ShowTaskbarButton(form);   // must not throw
    });
}
