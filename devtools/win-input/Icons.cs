// win-input icons — what icon the shell shows for each of the process's top-level windows, read-only.
//
// Per window: WM_GETICON (big, small) and the class icon (GCLP_HICON, GCLP_HICONSM), each with its size and its
// DOMINANT opaque colour, beside the dominant colour of the process exe's own first icon group. The colour, not
// the pixels: the shell may scale an icon, so an exact comparison fails on the right icon. Give the app an icon
// of a colour Chromium's logo does not use and the verdict reads itself.
//   win-input icons [--proc N]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

internal static class WinIcons
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr GetClassLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr module, IntPtr name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr obj, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll")] private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResNameProc cb, IntPtr param);

    private const uint WM_GETICON = 0x007F, SMTO_ABORTIFHUNG = 0x0002, GW_OWNER = 4, IMAGE_ICON = 1;
    private const int ICON_SMALL = 0, ICON_BIG = 1, GCLP_HICON = -14, GCLP_HICONSM = -34;
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x2, LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x20;
    private static readonly IntPtr RT_GROUP_ICON = new(14);

    public static int Report(string proc)
    {
        var processes = Process.GetProcessesByName(proc);
        if (processes.Length == 0) { Console.Error.WriteLine($"win-input icons: no process '{proc}'"); return 1; }
        var pids = processes.Select(p => (uint)p.Id).ToHashSet();

        string? exe = null;
        foreach (var p in processes) { try { exe = p.MainModule?.FileName; } catch { } if (exe != null) break; }
        Console.WriteLine($"exe: {exe ?? "(unreadable)"} — its own icon: {ExeIcon(exe, 32)}");

        var count = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            count++;
            Console.WriteLine($"hwnd=0x{h.ToInt64():X} class='{Text(h, GetClassNameW)}' title='{Text(h, GetWindowTextW)}'");
            Console.WriteLine($"  WM_GETICON big:   {Describe(Message(h, ICON_BIG))}");
            Console.WriteLine($"  WM_GETICON small: {Describe(Message(h, ICON_SMALL))}");
            Console.WriteLine($"  class icon:       {Describe(GetClassLongPtrW(h, GCLP_HICON))}");
            Console.WriteLine($"  class icon small: {Describe(GetClassLongPtrW(h, GCLP_HICONSM))}");
            return true;
        }, IntPtr.Zero);
        if (count == 0) Console.WriteLine("(no visible unowned top-level window)");
        return 0;
    }

    private static IntPtr Message(IntPtr h, int which) =>
        SendMessageTimeoutW(h, WM_GETICON, new IntPtr(which), IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out var icon) != IntPtr.Zero
            ? icon : IntPtr.Zero;

    private delegate int TextGetter(IntPtr h, StringBuilder sb, int max);

    private static string Text(IntPtr h, TextGetter get) { var sb = new StringBuilder(256); get(h, sb, 256); return sb.ToString(); }

    /// <summary>The first icon group of <paramref name="exe"/>, loaded at <paramref name="size"/>.</summary>
    private static string ExeIcon(string? exe, int size)
    {
        if (exe is null) return "none";
        var module = LoadLibraryExW(exe, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == IntPtr.Zero) return "unreadable";
        try
        {
            // The first INTEGER group, as the shell picks it: a named one's pointer dies with the enumeration.
            IntPtr first = IntPtr.Zero;
            EnumResourceNamesW(module, RT_GROUP_ICON, (_, _, name, _) =>
            {
                if (name.ToInt64() is > 0 and < 0x10000) { first = name; return false; }
                return true;
            }, IntPtr.Zero);
            if (first == IntPtr.Zero) return "none (no integer icon group)";
            var icon = LoadImageW(module, first, IMAGE_ICON, size, size, 0);
            try { return $"group {first.ToInt64()}, {Describe(icon)}"; }
            finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
        }
        finally { FreeLibrary(module); }
    }

    private static string Describe(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return "none";
        if (!GetIconInfo(icon, out var info)) return $"0x{icon.ToInt64():X} (not an icon)";
        try
        {
            var bitmap = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), out var bm);
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bm.bmWidth, biHeight = -bm.bmHeight,
                biPlanes = 1, biBitCount = 32,
            };
            var pixels = new byte[bm.bmWidth * bm.bmHeight * 4];
            var dc = GetDC(IntPtr.Zero);
            try { GetDIBits(dc, bitmap, 0, (uint)bm.bmHeight, pixels, ref header, 0); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            return $"0x{icon.ToInt64():X} {bm.bmWidth}x{bm.bmHeight} dominant={Dominant(pixels)}";
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    /// <summary>The most frequent opaque colour, quantised to 4 bits a channel, as #RRGGBB.</summary>
    private static string Dominant(byte[] bgra)
    {
        var anyAlpha = false;
        for (var i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) { anyAlpha = true; break; }
        var counts = new Dictionary<int, int>();
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (anyAlpha && bgra[i + 3] < 128) continue;
            var key = (bgra[i + 2] & 0xF0) << 16 | (bgra[i + 1] & 0xF0) << 8 | bgra[i] & 0xF0;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts.Count == 0 ? "transparent" : $"#{counts.MaxBy(pair => pair.Value).Key:X6}";
    }
}
