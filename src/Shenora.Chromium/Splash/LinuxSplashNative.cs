#if CEF_LINUX
using System.Runtime.InteropServices;

namespace Shenora.Chromium.Host;

/// <summary>The Xlib, cairo, pango and libc calls the Linux splash makes.</summary>
internal static unsafe class LinuxSplashNative
{
    private const string X11 = "libX11.so.6", Cairo = "libcairo.so.2", Pango = "libpango-1.0.so.0", PangoCairo = "libpangocairo-1.0.so.0";
    private const string GObject = "libgobject-2.0.so.0", LibC = "libc";

    public const long ExposureMask = 1L << 15, StructureNotifyMask = 1L << 17;
    public const int Expose = 12, DestroyNotify = 17, MapNotify = 19, ConfigureNotify = 22, PropModeReplace = 0, ZPixmap = 2;
    public const int OCloexec = 0x80000, ONonblock = 0x800;

    // Field offsets (LP64) the splash reads from Xlib's structs.
    public const int EventWindowOffset = 40;          // XMapEvent / XConfigureEvent / XDestroyWindowEvent .window
    public const int ConfigureWidthOffset = 56;       // XConfigureEvent .width, then .height
    public const int VisualRedMaskOffset = 24;        // Visual .red_mask, .green_mask, .blue_mask (unsigned long each)
    public const int ImageByteOrderOffset = 24;       // XImage .byte_order
    public const int PangoScale = 1024;
    public const short PollIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct XSizeHints
    {
        public long Flags;
        public int X, Y, Width, Height, MinWidth, MinHeight, MaxWidth, MaxHeight, WidthInc, HeightInc;
        public int MinAspectX, MinAspectY, MaxAspectX, MaxAspectY, BaseWidth, BaseHeight, WinGravity;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWMHints
    {
        public long Flags;
        public int Input, InitialState;
        public nuint IconPixmap, IconWindow;
        public int IconX, IconY;
        public nuint IconMask, WindowGroup;
    }

    [StructLayout(LayoutKind.Sequential)] public struct XClassHint { public nint Name, Class; }
    [StructLayout(LayoutKind.Sequential)] public struct XEvent { public int Type; private fixed long _pad[23]; }
    [StructLayout(LayoutKind.Sequential)] public struct PollFd { public int Fd; public short Events, Revents; }

    [DllImport(X11)] public static extern int XInitThreads();
    [DllImport(X11)] public static extern nint XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);
    [DllImport(X11)] public static extern int XEventsQueued(nint display, int mode);
    [DllImport(X11)] public static extern int XSetWMProtocols(nint display, nuint window, nuint* protocols, int count);
    [DllImport(X11)] public static extern int XSetWindowBackground(nint display, nuint window, nuint pixel);
    [DllImport(X11)] public static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] public static extern int XCloseDisplay(nint display);
    [DllImport(X11)] public static extern int XDefaultScreen(nint display);
    [DllImport(X11)] public static extern nuint XRootWindow(nint display, int screen);
    [DllImport(X11)] public static extern nint XDefaultVisual(nint display, int screen);
    [DllImport(X11)] public static extern int XDefaultDepth(nint display, int screen);
    [DllImport(X11)] public static extern nint XDefaultGC(nint display, int screen);
    [DllImport(X11)] public static extern int XDisplayWidth(nint display, int screen);
    [DllImport(X11)] public static extern int XDisplayHeight(nint display, int screen);
    [DllImport(X11)] public static extern int XConnectionNumber(nint display);
    [DllImport(X11)] public static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport(X11)] public static extern int XDestroyWindow(nint display, nuint window);
    [DllImport(X11)] public static extern nuint XInternAtom(nint display, string name, int onlyIfExists);
    [DllImport(X11)] public static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, void* data, int count);
    [DllImport(X11)] public static extern void XSetWMNormalHints(nint display, nuint window, XSizeHints* hints);
    [DllImport(X11)] public static extern int XSetWMHints(nint display, nuint window, XWMHints* hints);
    [DllImport(X11)] public static extern int XSetClassHint(nint display, nuint window, XClassHint* hint);
    [DllImport(X11)] public static extern int XSetTransientForHint(nint display, nuint window, nuint owner);
    [DllImport(X11)] public static extern int XSelectInput(nint display, nuint window, long mask);
    [DllImport(X11)] public static extern int XMapWindow(nint display, nuint window);
    [DllImport(X11)] public static extern int XRaiseWindow(nint display, nuint window);
    [DllImport(X11)] public static extern int XMoveWindow(nint display, nuint window, int x, int y);
    [DllImport(X11)] public static extern int XMoveResizeWindow(nint display, nuint window, int x, int y, uint width, uint height);
    [DllImport(X11)] public static extern int XFlush(nint display);
    [DllImport(X11)] public static extern int XPending(nint display);
    [DllImport(X11)] public static extern int XNextEvent(nint display, XEvent* evt);
    [DllImport(X11)] public static extern nint XCreateImage(nint display, nint visual, uint depth, int format, int offset, byte* data, uint width, uint height, int pad, int bytesPerLine);
    [DllImport(X11)] public static extern int XPutImage(nint display, nuint drawable, nint gc, nint image, int srcX, int srcY, int dstX, int dstY, uint width, uint height);
    [DllImport(X11)] public static extern int XFree(nint data);
    [DllImport(X11)] public static extern int XGetGeometry(nint display, nuint drawable, out nuint root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
    [DllImport(X11)] public static extern int XTranslateCoordinates(nint display, nuint source, nuint target, int x, int y, out int targetX, out int targetY, out nuint child);
    [DllImport(X11)] public static extern nint XResourceManagerString(nint display);
    [DllImport(X11)] public static extern nuint XGetSelectionOwner(nint display, nuint selection);
    [DllImport(X11)] public static extern int XGetWindowProperty(nint display, nuint window, nuint property, long offset, long length, int delete, nuint type,
        out nuint actualType, out int actualFormat, out nuint items, out nuint bytesAfter, out nint data);

    [DllImport(Cairo)] public static extern nint cairo_image_surface_create(int format, int width, int height);
    [DllImport(Cairo)] public static extern byte* cairo_image_surface_get_data(nint surface);
    [DllImport(Cairo)] public static extern int cairo_image_surface_get_stride(nint surface);
    [DllImport(Cairo)] public static extern int cairo_image_surface_get_width(nint surface);
    [DllImport(Cairo)] public static extern int cairo_image_surface_get_height(nint surface);
    [DllImport(Cairo)] public static extern nint cairo_image_surface_create_from_png(string path);
    [DllImport(Cairo)] public static extern nint cairo_image_surface_create_from_png_stream(delegate* unmanaged<nint, byte*, uint, int> read, nint closure);
    [DllImport(Cairo)] public static extern int cairo_surface_status(nint surface);
    [DllImport(Cairo)] public static extern void cairo_surface_flush(nint surface);
    [DllImport(Cairo)] public static extern void cairo_surface_destroy(nint surface);
    [DllImport(Cairo)] public static extern nint cairo_create(nint surface);
    [DllImport(Cairo)] public static extern void cairo_destroy(nint cr);
    [DllImport(Cairo)] public static extern void cairo_save(nint cr);
    [DllImport(Cairo)] public static extern void cairo_restore(nint cr);
    [DllImport(Cairo)] public static extern void cairo_set_operator(nint cr, int op);
    [DllImport(Cairo)] public static extern void cairo_paint(nint cr);
    [DllImport(Cairo)] public static extern void cairo_set_source_rgba(nint cr, double r, double g, double b, double a);
    [DllImport(Cairo)] public static extern void cairo_rectangle(nint cr, double x, double y, double width, double height);
    [DllImport(Cairo)] public static extern void cairo_fill(nint cr);
    [DllImport(Cairo)] public static extern void cairo_translate(nint cr, double x, double y);
    [DllImport(Cairo)] public static extern void cairo_scale(nint cr, double x, double y);
    [DllImport(Cairo)] public static extern void cairo_move_to(nint cr, double x, double y);
    [DllImport(Cairo)] public static extern void cairo_set_source_surface(nint cr, nint surface, double x, double y);
    [DllImport(Cairo)] public static extern nint cairo_get_source(nint cr);
    [DllImport(Cairo)] public static extern void cairo_pattern_set_filter(nint pattern, int filter);
    [DllImport(Cairo)] public static extern void cairo_pattern_set_extend(nint pattern, int extend);

    [DllImport(PangoCairo)] public static extern nint pango_cairo_create_layout(nint cr);
    [DllImport(PangoCairo)] public static extern void pango_cairo_show_layout(nint cr, nint layout);
    [DllImport(Pango)] public static extern void pango_layout_set_text(nint layout, byte[] text, int length);
    [DllImport(Pango)] public static extern void pango_layout_set_width(nint layout, int width);
    [DllImport(Pango)] public static extern void pango_layout_set_wrap(nint layout, int wrap);
    [DllImport(Pango)] public static extern void pango_layout_set_alignment(nint layout, int alignment);
    [DllImport(Pango)] public static extern void pango_layout_set_font_description(nint layout, nint description);
    [DllImport(Pango)] public static extern void pango_layout_get_pixel_size(nint layout, out int width, out int height);
    [DllImport(Pango)] public static extern nint pango_font_description_from_string(string text);
    [DllImport(Pango)] public static extern void pango_font_description_set_absolute_size(nint description, double size);
    [DllImport(Pango)] public static extern void pango_font_description_set_weight(nint description, int weight);
    [DllImport(Pango)] public static extern void pango_font_description_free(nint description);
    [DllImport(GObject)] public static extern void g_object_unref(nint instance);

    [DllImport(LibC, SetLastError = true)] public static extern int pipe2(int* fds, int flags);
    [DllImport(LibC)] public static extern nint write(int fd, byte* buffer, nint count);
    [DllImport(LibC)] public static extern nint read(int fd, byte* buffer, nint count);
    [DllImport(LibC)] public static extern int close(int fd);
    [DllImport(LibC)] public static extern int poll(PollFd* fds, nuint count, int timeout);
}
#endif
