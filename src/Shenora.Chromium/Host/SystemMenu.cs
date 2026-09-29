#if CEF_WINDOWS
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>
/// A window's system menu, which a caption's right click and Alt+Space open. CEF's window maximizes the system's own
/// way, so the system sets its items right.
/// </summary>
internal static class SystemMenu
{
    private const int WM_SYSCOMMAND = 0x0112, GWL_STYLE = -16, WS_SYSMENU = 0x00080000;
    private const uint TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;

    /// <summary>
    /// Give the window the system menu a frameless CEF window is created without. UI thread, as the window is created.
    /// </summary>
    public static void Ensure(nint hwnd)
    {
        var style = GetWindowLongW(hwnd, GWL_STYLE);
        if ((style & WS_SYSMENU) == 0) SetWindowLongW(hwnd, GWL_STYLE, style | WS_SYSMENU);
    }

    /// <summary>
    /// Show it at the pointer and carry out the choice (<c>SHOW_SYSTEM_MENU</c>). A modal loop, so never inline in
    /// something waiting on it. On the thread that owns <paramref name="hwnd"/>. False, and nothing shown, when the
    /// window has no system menu.
    /// </summary>
    public static bool ShowAtPointer(nint hwnd) => GetCursorPos(out var at) != 0 && ShowAt(hwnd, at.X, at.Y);

    /// <summary>Show it at a screen point; otherwise as <see cref="ShowAtPointer"/>.</summary>
    public static bool ShowAt(nint hwnd, int x, int y)
    {
        var menu = GetSystemMenu(hwnd, 0);
        // A window without WS_SYSMENU has no menu to show.
        if (menu == 0 || GetMenuItemCount(menu) <= 0) return false;
        var command = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, x, y, 0, hwnd, 0);
        // Posted, as the system's own menu hands its choice to the window after it closes.
        if (command != 0) PostMessage(hwnd, WM_SYSCOMMAND, command, 0);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32")] private static extern nint GetSystemMenu(nint hwnd, int revert);
    [DllImport("user32")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32")] private static extern int GetCursorPos(out POINT point);
    [DllImport("user32")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32")] private static extern int PostMessage(nint hwnd, int msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32")] private static extern int SetWindowLongW(nint hwnd, int index, int value);
}
#endif
