using System.Runtime.InteropServices;

namespace Shenora.Windows;

/// <summary>
/// What a form's caption does, for a page's drag area to do the same: a drag moves the window through the OS move
/// loop, a double click maximizes or restores it, and a right click opens its system menu. On the form's thread.
/// </summary>
internal static class FormCaption
{
    private const int WM_NCLBUTTONDOWN = 0x00A1, HTCAPTION = 2, WM_MOVING = 0x0216, WM_EXITSIZEMOVE = 0x0232;
    private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;
    private const int WM_SYSCOMMAND = 0x0112, GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const int SC_SIZE = 0xF000, SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;
    private const uint MF_BYCOMMAND = 0x0000, MF_GRAYED = 0x0001, TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    /// <summary>
    /// Shows a menu at a screen point for a window, and returns the command chosen, or 0. The system's
    /// <c>TrackPopupMenu</c>, a modal loop; a test replaces it, since a real menu would wait for a person.
    /// </summary>
    internal static Func<nint, Point, nint, int> TrackMenu { get; set; } =
        (menu, at, hwnd) => TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, at.X, at.Y, 0, hwnd, 0);

    /// <summary>
    /// Open the window's system menu at <paramref name="screen"/> (screen px), as a right click on a caption does, and
    /// carry out the choice. A modal loop, so never inline in something waiting on it. False, and nothing shown, when
    /// the window has no system menu.
    /// </summary>
    public static bool ShowSystemMenu(Form form, Point screen)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return false;
        var hwnd = form.Handle;
        var menu = GetSystemMenu(hwnd, false);
        // A window without WS_SYSMENU (a form with no ControlBox) has no menu to show.
        if (menu == 0 || GetMenuItemCount(menu) <= 0) return false;
        UpdateSystemMenu(menu, form);
        var command = TrackMenu(menu, screen, hwnd);
        // Posted, as the system's own menu hands its choice to the window after it closes.
        if (command != 0) PostMessage(hwnd, WM_SYSCOMMAND, command, 0);
        return true;
    }

    /// <summary>
    /// The system menu's items from the window's own placement, as the system sets them for a real one: a maximized
    /// window offers Restore and neither Move, Size nor Maximize. The system reads its placement from the window, and an
    /// <see cref="OptimizedForm"/> maximized its own way looks Normal to it. Close is left as the system has it.
    /// </summary>
    public static void UpdateSystemMenu(nint menu, Form form)
    {
        var hwnd = form.Handle;
        var style = GetWindowLong(hwnd, GWL_STYLE);
        var minimized = IsIconic(hwnd);
        var maximized = IsMaximized(form);
        var restored = !minimized && !maximized;
        Enable(menu, SC_RESTORE, minimized || maximized);
        Enable(menu, SC_MOVE, restored);
        Enable(menu, SC_SIZE, restored && (style & WS_THICKFRAME) != 0);
        Enable(menu, SC_MINIMIZE, !minimized && (style & WS_MINIMIZEBOX) != 0);
        Enable(menu, SC_MAXIMIZE, !maximized && (style & WS_MAXIMIZEBOX) != 0);
    }

    private static void Enable(nint menu, int command, bool enabled) =>
        EnableMenuItem(menu, (uint)command, MF_BYCOMMAND | (enabled ? 0 : MF_GRAYED));

    /// <summary>A frameless window's own work-area maximize, which never shows in <see cref="Form.WindowState"/> and
    /// keeps restore bounds the OS knows nothing of.</summary>
    public static bool ManuallyMaximized(Form form) =>
        form is IAppMaximizable { AppPlacement: WindowPlacement.Maximized } && form.WindowState != FormWindowState.Maximized;

    /// <summary>The window's own truth: an <see cref="IAppMaximizable"/>'s placement, else <see cref="Form.WindowState"/>.</summary>
    public static bool IsMaximized(Form form) =>
        form is IAppMaximizable app ? app.AppPlacement == WindowPlacement.Maximized : form.WindowState == FormWindowState.Maximized;

    public static void ToggleMaximize(Form form)
    {
        if (form is OptimizedForm optimized) optimized.ToggleMaximize();
        else form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    /// <summary>
    /// Start the OS move loop, which returns when the button is released. The window is placed by the pointer's travel
    /// since <paramref name="press"/> (screen px), as a caption's own loop places it, rather than trailing the pointer
    /// by however far it moved before the loop began. Refused, false, when the button is already up, since the loop
    /// would then follow the pointer until the next click, and for a manual maximize, whose stale restore bounds the OS
    /// would drag (as the window commands' <c>START_DRAG</c> refuses it). A real maximize or a snapped window is left
    /// to the system's own loop, as a caption's is.
    /// </summary>
    public static bool Move(Form form, Point press) => Move(form, press, LeftButtonDown);

    internal static bool Move(Form form, Point press, Func<bool> buttonDown)
    {
        if (!form.IsHandleCreated || ManuallyMaximized(form) || !buttonDown()) return false;
        var hwnd = form.Handle;
        var placement = IsZoomed(hwnd) || IsArranged(hwnd) ? null : new FirstPlacement(hwnd, press);
        try
        {
            ReleaseCapture();
            GetCursorPos(out var at);
            SendMessage(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, (nint)(((at.Y & 0xFFFF) << 16) | (at.X & 0xFFFF)));
        }
        finally
        {
            placement?.ReleaseHandle();
        }
        return true;
    }

    /// <summary>
    /// Start the OS size loop from an edge (<paramref name="hitTest"/>: an <c>HT*</c> edge code), at the cursor, while
    /// the button is still down; false, and nothing started, once it is up.
    /// </summary>
    public static bool Resize(Form form, int hitTest) => Resize(form, hitTest, LeftButtonDown);

    internal static bool Resize(Form form, int hitTest, Func<bool> buttonDown)
    {
        if (!form.IsHandleCreated || !buttonDown()) return false;
        GetCursorPos(out var at);
        ReleaseCapture();
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, hitTest, (nint)(((at.Y & 0xFFFF) << 16) | (at.X & 0xFFFF)));
        return true;
    }

    // Swapped buttons put the primary on the right.
    private static bool LeftButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON) & 0x8000) != 0;

    private static bool IsArranged(nint hwnd)
    {
        try { return IsWindowArranged(hwnd); }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// The loop's first proposal, placed where the window is moved by the pointer's travel since the press. Once: the
    /// loop builds each later proposal from where the window is by then, so correcting every one accumulates.
    /// </summary>
    private sealed class FirstPlacement : NativeWindow
    {
        private Point? _press;

        public FirstPlacement(nint hwnd, Point press)
        {
            _press = press;
            AssignHandle(hwnd);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOVING && _press is { } from && GetWindowRect(m.HWnd, out var now))
            {
                var proposed = Marshal.PtrToStructure<RECT>(m.LParam);
                var pointer = GetMessagePos();
                int x = now.Left + (short)(pointer & 0xFFFF) - from.X, y = now.Top + (short)((pointer >> 16) & 0xFFFF) - from.Y;
                Marshal.StructureToPtr(new RECT { Left = x, Top = y, Right = x + proposed.Right - proposed.Left, Bottom = y + proposed.Bottom - proposed.Top }, m.LParam, false);
                _press = null;
            }
            else if (m.Msg == WM_EXITSIZEMOVE) _press = null;
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32")] private static extern bool ReleaseCapture();
    [DllImport("user32")] private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32")] private static extern bool IsWindowArranged(nint hwnd);
    [DllImport("user32")] private static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32")] private static extern uint GetMessagePos();
    [DllImport("user32")] private static extern bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern nint GetSystemMenu(nint hwnd, bool revert);
    [DllImport("user32")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32")] private static extern int EnableMenuItem(nint menu, uint item, uint flags);
    [DllImport("user32")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
}
