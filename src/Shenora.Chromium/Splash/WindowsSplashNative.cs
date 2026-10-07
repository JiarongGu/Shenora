#if CEF_WINDOWS
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>The Win32, GDI, GDI+ and DWM calls the Windows splash makes.</summary>
internal static unsafe class WindowsSplashNative
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    public const int GWLP_HWNDPARENT = -8, GWLP_USERDATA = -21;
    public const uint WM_NCCREATE = 0x81, WM_DESTROY = 0x2, WM_TIMER = 0x113, WM_MOUSEACTIVATE = 0x21, WM_APP = 0x8000;
    public const int MA_NOACTIVATE = 3, SW_SHOWNOACTIVATE = 4;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const uint ULW_ALPHA = 2;
    public const uint DT_CENTER = 0x1, DT_WORDBREAK = 0x10, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_EDITCONTROL = 0x2000;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const uint MONITORINFOF_PRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Cx, Cy; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public POINT Pt; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] public struct CREATESTRUCTW { public nint CreateParams, Instance, Menu, Parent; public int Cy, Cx, Y, X; public int Style; public nint Name, Class; public uint ExStyle; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONTW
    {
        public int Height, Width, Escapement, Orientation, Weight;
        public byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NONCLIENTMETRICSW
    {
        public uint Size;
        public int BorderWidth, ScrollWidth, ScrollHeight, CaptionWidth, CaptionHeight;
        public LOGFONTW CaptionFont;
        public int SmCaptionWidth, SmCaptionHeight;
        public LOGFONTW SmCaptionFont;
        public int MenuWidth, MenuHeight;
        public LOGFONTW MenuFont, StatusFont, MessageFont;
        public int PaddedBorderWidth;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GdiplusStartupInput { public uint Version; public nint DebugEventCallback; public int SuppressBackgroundThread, SuppressExternalCodecs; }

    [DllImport("user32")] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, nint cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern int DestroyWindow(nint hwnd);
    [DllImport("user32")] public static extern int IsWindow(nint hwnd);
    [DllImport("user32")] public static extern int ShowWindow(nint hwnd, int cmd);
    [DllImport("user32")] public static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32")] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32")] public static extern int GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32")] public static extern int GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32")] public static extern int ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32")] public static extern int IsIconic(nint hwnd);
    [DllImport("user32")] public static extern int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32")] public static extern int GetMessageW(MSG* msg, nint hwnd, uint min, uint max);
    [DllImport("user32")] public static extern int TranslateMessage(MSG* msg);
    [DllImport("user32")] public static extern nint DispatchMessageW(MSG* msg);
    [DllImport("user32")] public static extern void PostQuitMessage(int code);
    [DllImport("user32")] public static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32")] public static extern int KillTimer(nint hwnd, nuint id);
    [DllImport("user32", SetLastError = true)]
    public static extern int UpdateLayeredWindow(nint hwnd, nint dst, POINT* at, SIZE* size, nint src, POINT* srcAt, uint key, BLENDFUNCTION* blend, uint flags);
    [DllImport("user32")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] public static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32")] public static extern int GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [DllImport("user32")] public static extern int EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data);
    [DllImport("user32")] public static extern nint GetDC(nint hwnd);
    [DllImport("user32")] public static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int DrawTextW(nint dc, string text, int length, RECT* rect, uint format);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern int SystemParametersInfoW(uint action, uint param, ref NONCLIENTMETRICSW value, uint winIni);

    [DllImport("gdi32")] public static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] public static extern int DeleteDC(nint dc);
    [DllImport("gdi32")] public static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32")] public static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32")] public static extern int DeleteObject(nint obj);
    [DllImport("gdi32")] public static extern int GdiFlush();
    [DllImport("gdi32")] public static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32")] public static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    public static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("shcore")] public static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, RECT* value, int size);
    [DllImport("dwmapi")] public static extern int DwmSetWindowAttribute(nint hwnd, int attribute, int* value, int size);
    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    [DllImport("shlwapi")] public static extern nint SHCreateMemStream(byte* data, uint size);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);

    [DllImport("gdiplus")] public static extern int GdiplusStartup(out nint token, GdiplusStartupInput* input, nint output);
    [DllImport("gdiplus")] public static extern void GdiplusShutdown(nint token);
    [DllImport("gdiplus")] public static extern int GdipCreateFromHDC(nint dc, out nint graphics);
    [DllImport("gdiplus")] public static extern int GdipDeleteGraphics(nint graphics);
    [DllImport("gdiplus")] public static extern int GdipSetSmoothingMode(nint graphics, int mode);
    [DllImport("gdiplus")] public static extern int GdipSetInterpolationMode(nint graphics, int mode);
    [DllImport("gdiplus")] public static extern int GdipSetPixelOffsetMode(nint graphics, int mode);
    [DllImport("gdiplus")] public static extern int GdipCreateSolidFill(uint argb, out nint brush);
    [DllImport("gdiplus")] public static extern int GdipDeleteBrush(nint brush);
    [DllImport("gdiplus")] public static extern int GdipFillRectangle(nint graphics, nint brush, float x, float y, float width, float height);
    [DllImport("gdiplus", CharSet = CharSet.Unicode)] public static extern int GdipCreateBitmapFromFile(string file, out nint bitmap);
    [DllImport("gdiplus")] public static extern int GdipCreateBitmapFromStream(nint stream, out nint bitmap);
    [DllImport("gdiplus")] public static extern int GdipDrawImageRectRect(nint graphics, nint image, float x, float y, float width, float height,
        float srcX, float srcY, float srcWidth, float srcHeight, int srcUnit, nint attributes, nint callback, nint callbackData);
    [DllImport("gdiplus")] public static extern int GdipGetImageWidth(nint image, out uint width);
    [DllImport("gdiplus")] public static extern int GdipGetImageHeight(nint image, out uint height);
    [DllImport("gdiplus")] public static extern int GdipCreateImageAttributes(out nint attributes);
    [DllImport("gdiplus")] public static extern int GdipSetImageAttributesWrapMode(nint attributes, int wrap, uint argb, int clamp);
    [DllImport("gdiplus")] public static extern int GdipDisposeImageAttributes(nint attributes);
    [DllImport("gdiplus")] public static extern int GdipDisposeImage(nint image);

    /// <summary>Release a COM object (IUnknown::Release).</summary>
    public static void Release(nint unknown)
    {
        if (unknown != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);
    }
}
#endif
