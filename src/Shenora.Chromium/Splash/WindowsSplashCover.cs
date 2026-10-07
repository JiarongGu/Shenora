#if CEF_WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using static Shenora.Chromium.Host.WindowsSplashNative;

namespace Shenora.Chromium.Host;

/// <summary>
/// A frameless main window's strip and resize band as the window will paint them, until it first does: its background
/// and the strip's caption buttons. Until its first frame Chromium shows its own colours there, not the window's
/// (measured: #F3F3F3 under a light Windows theme for up to 0.9 s, in a dark app).
/// <para>
/// A layered popup owned by the main window over its whole client area, under the splash, and click-through, so the
/// window's own strip still takes the drag, the double-click, Snap Layouts and the buttons. It has no fade in or out,
/// which would show the window's colours through it (measured: shown, its pixels 110 ms later). Made, shown and moved
/// on CEF's UI thread, the main window's own, so it shows the moment the window does rather than with the splash's first
/// frame, which can come 110–120 ms after (measured).
/// </para>
/// </summary>
internal sealed unsafe class WindowsSplashCover
{
    private const string ClassName = "ShenoraSplashCover";
    private const uint WM_CLOSE = 0x10, WM_NCDESTROY = 0x82;
    private static readonly Lock ClassGate = new();
    private static nint _class;

    private readonly nint _owner;
    private readonly SplashOverlayLayout _layout;
    private readonly uint _background;
    private nint _hwnd, _dc, _bitmap, _previous;
    private GCHandle _self;
    private uint* _bits;
    private Size _size;
    private (Size Size, float Scale, bool Maximized) _presented;
    private bool _wanted;   // Show was asked: shown as soon as its window is not minimized

    private WindowsSplashCover(nint owner, SplashOverlayLayout layout, uint background)
    {
        _owner = owner;
        _layout = layout;
        _background = background;
    }

    /// <summary>The cover for a frameless window with a background of its own, made hidden; null for any other. The
    /// owner's thread.</summary>
    public static WindowsSplashCover? Make(nint owner, SplashOverlayLayout layout)
    {
        if (!layout.Frameless || layout.WindowBackground is not { A: 255 } background) return null;
        var cover = new WindowsSplashCover(owner, layout, (uint)background.ToArgb());
        var previous = SetThreadDpiAwarenessContext(-4);   // made per-monitor, so its rectangles are physical pixels
        try
        {
            var client = ClientBounds(owner);
            var windowClass = Class();   // first: a class that will not register must not leave the cover rooted
            cover._self = GCHandle.Alloc(cover);
            var hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, windowClass, "", WS_POPUP,
                client.X, client.Y, client.Width, client.Height, owner, 0, GetModuleHandleW(null), GCHandle.ToIntPtr(cover._self));
            if (hwnd == 0)
            {
                // A creation that failed after WM_NCCREATE has been through WM_NCDESTROY, which freed it already.
                if (cover._self.IsAllocated) cover._self.Free();
                return null;
            }
            cover._hwnd = hwnd;
            var on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, &on, sizeof(int));
            cover.Present();
            return cover;
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>The window, for tests.</summary>
    internal nint Window => Volatile.Read(ref _hwnd);

    /// <summary>Show it at the window's client area as it is now. The owner's thread, once CEF has shown the window: shown
    /// before, the window opened under the terminal it was launched from in the one run tried.</summary>
    public void Show()
    {
        if (Window == 0) return;
        _wanted = true;
        if (IsIconic(_owner) != 0) return;   // shown on the restore, by Follow
        Follow();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>The window moved, resized, maximized or changed DPI. The owner's thread.</summary>
    public void Follow()
    {
        if (Window == 0 || IsIconic(_owner) != 0) return;   // the OS hides it with its owner; it follows on the restore
        var previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            var client = ClientBounds(_owner);
            SetWindowPos(_hwnd, 0, client.X, client.Y, client.Width, client.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            // A layered window keeps its bitmap as it moves: drawn again only for a new size, scale or maximize glyph.
            if (_presented != (client.Size, Scale(), IsZoomed(_owner) != 0)) Present();
            // Asked to show while its window was minimized: the OS does not show it with its owner.
            if (_wanted && IsWindowVisible(_hwnd) == 0) ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        }
        finally
        {
            if (previous != 0) SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>Gone. Any thread: the owner's thread destroys it, and frees what it drew with.</summary>
    public void Remove()
    {
        var hwnd = Interlocked.Exchange(ref _hwnd, 0);
        if (hwnd != 0) PostMessageW(hwnd, WM_CLOSE, 0, 0);
    }

    // The window's colour, and each button's glyph mask in its colour, centred in its rect as the window's own buttons
    // centre theirs; opaque but for its corners, cut round by radius pixels as Windows 11 rounds the window's (0: square).
    internal static void Paint(Span<uint> pixels, int width, uint background, IReadOnlyList<(Rectangle Bounds, CaptionGlyphs.Mask Mask, uint Glyph)> glyphs,
        int radius = 0)
    {
        pixels.Fill(background | 0xFF000000);
        var height = pixels.Length / width;
        foreach (var (bounds, mask, glyph) in glyphs)
        {
            int left = bounds.X + ((bounds.Width - mask.Size) / 2), top = bounds.Y + ((bounds.Height - mask.Size) / 2);
            var opacity = glyph >> 24;
            for (var y = 0; y < mask.Size; y++)
                for (var x = 0; x < mask.Size; x++)
                {
                    int px = left + x, py = top + y;
                    var a = opacity * mask.Coverage[(y * mask.Size) + x] / 255;
                    if (a == 0 || px < 0 || py < 0 || px >= width || py >= height) continue;
                    ref var p = ref pixels[(py * width) + px];
                    p = 0xFF000000 | (Over(glyph, p, a, 16) << 16) | (Over(glyph, p, a, 8) << 8) | Over(glyph, p, a, 0);
                }
        }
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        for (var y = 0; y < radius; y++)
            for (var x = 0; x < radius; x++)
            {
                double dx = radius - (x + 0.5), dy = radius - (y + 0.5);
                var coverage = Math.Clamp(radius - Math.Sqrt((dx * dx) + (dy * dy)) + 0.5, 0, 1);
                if (coverage >= 1) continue;
                foreach (var i in (ReadOnlySpan<int>)[(y * width) + x, (y * width) + width - 1 - x, ((height - 1 - y) * width) + x, ((height - 1 - y) * width) + width - 1 - x])
                {
                    var value = pixels[i];
                    uint Scaled(int shift) => (uint)Math.Round(((value >> shift) & 0xFF) * coverage) << shift;
                    pixels[i] = Scaled(24) | Scaled(16) | Scaled(8) | Scaled(0);
                }
            }
    }

    private static uint Over(uint over, uint under, uint alpha, int shift) =>
        ((((over >> shift) & 0xFF) * alpha) + (((under >> shift) & 0xFF) * (255 - alpha))) / 255;

    private float Scale()
    {
        var scale = GetDpiForWindow(_hwnd) / 96f;
        return scale > 0 ? scale : 1;
    }

    // The strip's own palette and button places (ChromiumWindow.PlaceStrip), so nothing moves when the window draws.
    private void Present()
    {
        GetWindowRect(_hwnd, out var r);
        var size = new Size(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        var scale = Scale();
        var maximized = IsZoomed(_owner) != 0;
        if (!EnsureBitmap(size)) return;
        var palette = CaptionButtonPalette.ForStrip(_layout.TitleBar, CaptionButtonPalette.ForBackground(Color.FromArgb((int)_background)));
        var em = Math.Max(1, (int)Math.Round(NativeCaptionButtons.GlyphDips * scale));
        var glyphs = new List<(Rectangle, CaptionGlyphs.Mask, uint)>();
        foreach (var button in SplashGeometry.DefaultCaptionButtons(size.Width, (int)Math.Round(_layout.StripDips * scale), scale))
            if (CaptionGlyphs.Rasterize(NativeCaptionButtons.Glyph(button.Kind, maximized), em) is { } mask)
                glyphs.Add((new Rectangle(button.X, button.Y, button.Width, button.Height), mask,
                    palette.For(button.Kind, hot: false, pressed: false, active: true).Glyph));
        // Windows 11 rounds a normal window's corners, as the splash cuts its own; a maximized one is square.
        var rounded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && !maximized;
        Paint(new Span<uint>(_bits, size.Width * size.Height), size.Width, _background, glyphs, rounded ? (int)Math.Round(8 * scale) : 0);
        GdiFlush();
        var at = new POINT { X = r.Left, Y = r.Top };
        var extent = new SIZE { Cx = size.Width, Cy = size.Height };
        var origin = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, 0, &at, &extent, _dc, &origin, 0, &blend, ULW_ALPHA);
        _presented = (size, scale, maximized);
    }

    private bool EnsureBitmap(Size size)
    {
        if (_bitmap != 0 && size == _size) return true;
        ReleaseBitmap();
        if (_dc == 0) _dc = CreateCompatibleDC(0);
        var info = new BITMAPINFOHEADER { Size = (uint)sizeof(BITMAPINFOHEADER), Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
        void* bits;
        _bitmap = CreateDIBSection(_dc, &info, 0, &bits, 0, 0);
        if (_bitmap == 0) return false;
        _bits = (uint*)bits;
        _previous = SelectObject(_dc, _bitmap);
        _size = size;
        return true;
    }

    private void ReleaseBitmap()
    {
        if (_bitmap == 0) return;
        SelectObject(_dc, _previous);
        DeleteObject(_bitmap);
        _bitmap = 0;
        _bits = null;
    }

    // The owner's thread, as its window goes (removed, or with the main window).
    private void Destroyed()
    {
        Volatile.Write(ref _hwnd, 0);
        ReleaseBitmap();
        if (_dc != 0) DeleteDC(_dc);
        _dc = 0;
        if (_self.IsAllocated) _self.Free();
    }

    private static Rectangle ClientBounds(nint owner)
    {
        GetClientRect(owner, out var client);
        var origin = new POINT();
        ClientToScreen(owner, ref origin);
        return new Rectangle(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private static nint Class()
    {
        lock (ClassGate)
        {
            if (_class != 0) return _class;
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    Size = (uint)sizeof(WNDCLASSEXW),
                    WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                    Instance = GetModuleHandleW(null),
                    ClassName = name,
                };
                _class = RegisterClassExW(&wc);
            }
            if (_class == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "The splash cover's window class could not be registered.");
            return _class;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_NCCREATE) SetWindowLongPtrW(hwnd, GWLP_USERDATA, ((CREATESTRUCTW*)lParam)->CreateParams);
            if (msg == WM_NCDESTROY && GetWindowLongPtrW(hwnd, GWLP_USERDATA) is var data and not 0
                && GCHandle.FromIntPtr(data).Target is WindowsSplashCover self)
            {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                self.Destroyed();
            }
        }
        catch
        {
            // Nothing may unwind into user32.
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    [DllImport("user32")] private static extern int IsZoomed(nint hwnd);
}
#endif
