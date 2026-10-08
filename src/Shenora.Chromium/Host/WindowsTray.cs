#if CEF_WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

/// <summary>
/// The notification-area icon: <c>Shell_NotifyIcon</c> on a hidden window of CEF's UI thread, whose loop pumps its
/// messages. A double click, or Enter on the focused icon, shows the main window; a right click opens the menu. When
/// Explorer restarts it re-adds the icon, which the old taskbar took with it.
/// </summary>
internal sealed unsafe class WindowsTray : NativeTray
{
    /// <summary>The message the icon's events arrive as.</summary>
    internal const uint CallbackMessage = 0x8000 + 0x100;   // WM_APP + 0x100

    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4, IconId = 1;
    private const uint WM_NULL = 0, WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, NIN_KEYSELECT = 0x0401;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint WS_POPUP = 0x80000000;

    // One tray per process: the window procedure finds it here.
    private static WindowsTray? _current;
    private static nint _class;
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly ChromiumTray _tray;
    private readonly ILogger? _log;
    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly bool _ownsIcon;

    /// <summary>Shows the menu and returns the chosen item's command, 0 for none. A seam, so a test can choose.</summary>
    internal static Func<nint, int, int, nint, int> TrackMenu { get; set; } =
        (menu, x, y, hwnd) => TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD, x, y, hwnd, 0);

    public WindowsTray(ChromiumTray tray, ILogger? log)
    {
        _tray = tray;
        _log = log;
        _hwnd = CreateWindowExW(0, WindowClass(), 0, WS_POPUP, 0, 0, 0, 0, 0, 0, GetModuleHandleW(null), 0);
        if (_hwnd == 0) throw new InvalidOperationException($"The tray's window could not be created ({Marshal.GetLastPInvokeError()}).");
        (_icon, _ownsIcon) = LoadIcon(tray.IconPath, log);
        _current = this;
        Add();
    }

    /// <summary>What an icon event asks for; the event is the low word of the callback's lParam (version 4).</summary>
    internal static WindowsTrayEvent Decode(nint lParam) => (uint)(lParam & 0xFFFF) switch
    {
        WM_LBUTTONDBLCLK or NIN_KEYSELECT => WindowsTrayEvent.ShowWindow,
        WM_CONTEXTMENU => WindowsTrayEvent.Menu,
        _ => WindowsTrayEvent.None,
    };

    /// <summary>The menu for <paramref name="entries"/>: command i+1 for entry i, the default item bold.</summary>
    internal static nint BuildMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        var menu = CreatePopupMenu();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.IsSeparator)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                continue;
            }
            AppendMenuW(menu, MF_STRING | (entry.Enabled ? 0 : MF_GRAYED) | (entry.Checked ? MF_CHECKED : 0), (nuint)(i + 1), entry.Text);
            if (entry.IsDefault) SetMenuDefaultItem(menu, (uint)(i + 1), 0);
        }
        return menu;
    }

    private void Add()
    {
        var data = Data();
        if (Shell_NotifyIconW(NIM_ADD, &data) == 0)
            AppCallback.Log(_log, () => "[Shenora.Chromium] The tray icon could not be added", LogLevel.Warning);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private NOTIFYICONDATAW Data()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var text = _tray.Text;
        var length = Math.Min(text.Length, 127);
        for (var i = 0; i < length; i++) data.szTip[i] = text[i];
        return data;
    }

    private void OnIcon(nint lParam, nint wParam)
    {
        switch (Decode(lParam))
        {
            case WindowsTrayEvent.ShowWindow:
                _tray.ShowWindow();
                break;
            case WindowsTrayEvent.Menu:
                // Version 4 gives the menu's anchor, in screen coordinates, in wParam.
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var entries = _tray.Menu();
        var menu = BuildMenu(entries);
        try
        {
            // Without it the menu stays open when the person clicks elsewhere (TrackPopupMenu's documented remark).
            SetForegroundWindow(_hwnd);
            var chosen = TrackMenu(menu, x, y, _hwnd);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            if (chosen > 0 && chosen <= entries.Count) _tray.Choose(entries[chosen - 1]);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public override void Dispose()
    {
        if (_current != this) return;
        _current = null;
        var data = Data();
        Shell_NotifyIconW(NIM_DELETE, &data);
        DestroyWindow(_hwnd);
        if (_ownsIcon) DestroyIcon(_icon);
    }

    /// <summary>The icon file's, else the running executable's (CEF's launcher, which the app may have given its own
    /// icon), else the system's application icon.</summary>
    private static (nint Icon, bool Owned) LoadIcon(string? path, ILogger? log)
    {
        int cx = GetSystemMetrics(49), cy = GetSystemMetrics(50);   // SM_CXSMICON, SM_CYSMICON
        if (path is not null)
        {
            var loaded = LoadImageW(0, path, 1 /* IMAGE_ICON */, cx, cy, 0x10 /* LR_LOADFROMFILE */);
            if (loaded != 0) return (loaded, true);
            AppCallback.Log(log, () => $"[Shenora.Chromium] The tray icon {path} could not be loaded; using the app's own", LogLevel.Warning);
        }
        if (Environment.ProcessPath is { } exe)
        {
            nint small = 0;
            if (ExtractIconExW(exe, 0, null, &small, 1) > 0 && small != 0) return (small, true);
        }
        return (LoadIconW(0, 32512 /* IDI_APPLICATION */), false);
    }

    private static nint WindowClass()
    {
        if (_class != 0) return _class;
        var name = Marshal.StringToHGlobalUni("ShenoraChromiumTray");   // kept for the process, as the class is
        var cls = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = name,
        };
        if (RegisterClassExW(&cls) == 0) throw new InvalidOperationException($"The tray's window class could not be registered ({Marshal.GetLastPInvokeError()}).");
        return _class = name;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (_current is { } tray && hwnd == tray._hwnd)
        {
            if (msg == CallbackMessage)
            {
                AppCallback.Run(() => tray.OnIcon(lParam, wParam), ex => AppCallback.Log(tray._log, () => "[Shenora.Chromium] The tray's event failed", LogLevel.Error, ex));
                return 0;
            }
            if (msg == TaskbarCreated)
            {
                AppCallback.Run(tray.Add);
                return 0;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;   // a union with uTimeout
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern int Shell_NotifyIconW(uint message, NOTIFYICONDATAW* data);
    [DllImport("shell32", CharSet = CharSet.Unicode)] private static extern uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32", SetLastError = true)] private static extern ushort RegisterClassExW(WNDCLASSEXW* cls);
    [DllImport("user32", SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, nint className, nint title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] private static extern int DestroyWindow(nint hwnd);
    [DllImport("user32")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern nint CreatePopupMenu();
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32")] private static extern int SetMenuDefaultItem(nint menu, uint item, uint byPosition);
    [DllImport("user32")] private static extern int DestroyMenu(nint menu);
    [DllImport("user32")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);
    [DllImport("user32")] private static extern int SetForegroundWindow(nint hwnd);
    [DllImport("user32")] private static extern int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32")] private static extern nint LoadIconW(nint instance, nint name);
    [DllImport("user32")] private static extern int DestroyIcon(nint icon);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}

/// <summary>What an event on the icon asks for.</summary>
internal enum WindowsTrayEvent { None, ShowWindow, Menu }
#endif
