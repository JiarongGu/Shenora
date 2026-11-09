using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Tests.Linux;

/// <summary>
/// The X display as a person looking at it would have it, on a connection of its own: a top-level window found by its
/// title, where its client area sits on the screen, the colour of a screen pixel, and a close asked as a window manager
/// asks one. What an app under test says of itself is never read here, only what the X server holds.
/// </summary>
internal sealed unsafe class XScreen : IDisposable
{
    private readonly nint _c;
    private readonly uint _root;

    public XScreen()
    {
        int screen;
        _c = xcb_connect(null, &screen);
        if (xcb_connection_has_error(_c) != 0) throw new InvalidOperationException("No X display answered (DISPLAY).");
        var roots = xcb_setup_roots_iterator(xcb_get_setup(_c));
        _root = *(uint*)roots.data;
        Width = *(ushort*)(roots.data + 20);
        Height = *(ushort*)(roots.data + 22);
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The window manager's name (<c>_NET_WM_NAME</c> of its check window; empty if it names none), or null while
    /// none runs.</summary>
    public string? WindowManager()
    {
        var check = Property(_root, "_NET_SUPPORTING_WM_CHECK", 0, 4);
        if (check is not { Length: 4 }) return null;
        var window = BitConverter.ToUInt32(check);
        return Encoding.UTF8.GetString(Property(window, "_NET_WM_NAME", 0, 256) ?? []);
    }

    /// <summary>Whether a window manager has taken the display, which it says by setting
    /// <c>_NET_SUPPORTING_WM_CHECK</c> on the root window.</summary>
    public bool HasWindowManager => Property(_root, "_NET_SUPPORTING_WM_CHECK", 0, 4) is { Length: 4 };

    /// <summary>The first window anywhere in the tree titled <paramref name="title"/> (its <c>_NET_WM_NAME</c>) that is
    /// at least <paramref name="minimum"/> pixels each way, or 0.</summary>
    public uint FindWindow(string title, int minimum = 100) => Find(_root, title, minimum);

    private uint Find(uint window, string title, int minimum)
    {
        if (Title(window) == title)
        {
            var (_, _, w, h) = Geometry(window);
            if (w >= minimum && h >= minimum) return window;
        }
        foreach (var child in Children(window))
            if (Find(child, title, minimum) is var found and not 0) return found;
        return 0;
    }

    // The title a window manager shows: _NET_WM_NAME, else the older WM_NAME.
    private string? Title(uint window) =>
        (Property(window, "_NET_WM_NAME", 0, 256) ?? Property(window, "WM_NAME", 0, 256)) is { } name ? Encoding.UTF8.GetString(name) : null;

    /// <summary>Every window in the tree with its title and size, one to a line: what a failed find says.</summary>
    public string Describe() => Describe(_root, 0);

    private string Describe(uint window, int depth)
    {
        var (_, _, w, h) = Geometry(window);
        var title = Title(window) is { } t ? $"'{t}'" : "";
        var text = $"{new string(' ', depth * 2)}0x{window:X} {w}x{h} {title}{Environment.NewLine}";
        return text + string.Concat(Children(window).Select(child => Describe(child, depth + 1)));
    }

    /// <summary>The window's size, which is its client area's.</summary>
    public (int Width, int Height) Size(uint window)
    {
        var (_, _, w, h) = Geometry(window);
        return (w, h);
    }

    /// <summary>Where the window's client area's top left sits on the screen.</summary>
    public (int X, int Y) Origin(uint window)
    {
        var reply = xcb_translate_coordinates_reply(_c, xcb_translate_coordinates(_c, window, _root, 0, 0), null);
        if (reply == 0) throw new InvalidOperationException($"Window 0x{window:X} is gone.");
        try { return (*(short*)(reply + 12), *(short*)(reply + 14)); }
        finally { free(reply); }
    }

    /// <summary>Whether the window is still on the display.</summary>
    public bool Exists(uint window)
    {
        var reply = xcb_get_geometry_reply(_c, xcb_get_geometry(_c, window), null);
        if (reply == 0) return false;
        free(reply);
        return true;
    }

    /// <summary>The whole screen as it is now.</summary>
    public Snapshot Capture()
    {
        var reply = xcb_get_image_reply(_c, xcb_get_image(_c, 2 /* ZPixmap */, _root, 0, 0, (ushort)Width, (ushort)Height, uint.MaxValue), null);
        if (reply == 0) throw new InvalidOperationException("The X server would not give the screen's image.");
        try
        {
            var length = xcb_get_image_data_length(reply);
            var data = new byte[length];
            Marshal.Copy((nint)xcb_get_image_data(reply), data, 0, length);
            return new Snapshot(data, Width, Height);
        }
        finally { free(reply); }
    }

    /// <summary>Ask the window to close, as a window manager's close button does (<c>WM_DELETE_WINDOW</c>).</summary>
    public void AskToClose(uint window)
    {
        var e = stackalloc byte[32];
        new Span<byte>(e, 32).Clear();
        e[0] = 33;   // ClientMessage
        e[1] = 32;   // format
        *(uint*)(e + 4) = window;
        *(uint*)(e + 8) = Atom("WM_PROTOCOLS");
        *(uint*)(e + 12) = Atom("WM_DELETE_WINDOW");
        xcb_send_event(_c, 0, window, 0, e);
        xcb_flush(_c);
    }

    private (int X, int Y, int Width, int Height) Geometry(uint window)
    {
        var reply = xcb_get_geometry_reply(_c, xcb_get_geometry(_c, window), null);
        if (reply == 0) return (0, 0, 0, 0);
        try { return (*(short*)(reply + 12), *(short*)(reply + 14), *(ushort*)(reply + 16), *(ushort*)(reply + 18)); }
        finally { free(reply); }
    }

    private uint[] Children(uint window)
    {
        var reply = xcb_query_tree_reply(_c, xcb_query_tree(_c, window), null);
        if (reply == 0) return [];
        try
        {
            var count = xcb_query_tree_children_length(reply);
            var children = xcb_query_tree_children(reply);
            var all = new uint[count];
            for (var i = 0; i < count; i++) all[i] = children[i];
            return all;
        }
        finally { free(reply); }
    }

    private byte[]? Property(uint window, string name, uint offset, uint length)
    {
        var reply = xcb_get_property_reply(_c, xcb_get_property(_c, 0, window, Atom(name), 0 /* AnyPropertyType */, offset, length), null);
        if (reply == 0) return null;
        try
        {
            var size = xcb_get_property_value_length(reply);
            if (size == 0) return null;
            var bytes = new byte[size];
            Marshal.Copy((nint)xcb_get_property_value(reply), bytes, 0, size);
            return bytes;
        }
        finally { free(reply); }
    }

    private readonly Dictionary<string, uint> _atoms = new();

    private uint Atom(string name)
    {
        if (_atoms.TryGetValue(name, out var atom)) return atom;
        var bytes = Encoding.ASCII.GetBytes(name);
        fixed (byte* p = bytes)
        {
            var reply = xcb_intern_atom_reply(_c, xcb_intern_atom(_c, 0, (ushort)bytes.Length, p), null);
            try { atom = *(uint*)(reply + 8); }
            finally { free(reply); }
        }
        return _atoms[name] = atom;
    }

    public void Dispose() => xcb_disconnect(_c);

    /// <summary>The screen at one moment: a pixel's colour by its place.</summary>
    internal sealed class Snapshot(byte[] data, int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;

        /// <summary>The pixel's colour as 0xRRGGBB; the server's 24-bit visual has four bytes a pixel, blue first.</summary>
        public int At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) throw new ArgumentOutOfRangeException($"({x},{y}) is off the {Width}x{Height} screen.");
            var i = (y * Width + x) * 4;
            return (data[i + 2] << 16) | (data[i + 1] << 8) | data[i];
        }

        /// <summary>Whether every pixel of <paramref name="area"/> is the same in both.</summary>
        public bool Same(Snapshot other, System.Drawing.Rectangle area)
        {
            for (var y = Math.Max(0, area.Top); y < Math.Min(Height, area.Bottom); y++)
                for (var x = Math.Max(0, area.Left); x < Math.Min(Width, area.Right); x++)
                    if (At(x, y) != other.At(x, y)) return false;
            return true;
        }

        /// <summary>The smallest rectangle holding every pixel within <paramref name="tolerance"/> of <paramref name="colour"/>
        /// inside <paramref name="within"/>, and how many such pixels there are; an empty rectangle when there is none.</summary>
        public (System.Drawing.Rectangle Box, int Count) Find(int colour, System.Drawing.Rectangle within, int tolerance = 12)
        {
            int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
            for (var y = Math.Max(0, within.Top); y < Math.Min(Height, within.Bottom); y++)
                for (var x = Math.Max(0, within.Left); x < Math.Min(Width, within.Right); x++)
                {
                    if (!Near(At(x, y), colour, tolerance)) continue;
                    count++;
                    if (x < left) left = x;
                    if (y < top) top = y;
                    if (x > right) right = x;
                    if (y > bottom) bottom = y;
                }
            return count == 0 ? (System.Drawing.Rectangle.Empty, 0) : (System.Drawing.Rectangle.FromLTRB(left, top, right + 1, bottom + 1), count);
        }

        public static bool Near(int a, int b, int tolerance = 12) =>
            Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) <= tolerance
            && Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) <= tolerance
            && Math.Abs((a & 0xFF) - (b & 0xFF)) <= tolerance;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator { public nint data; public int rem; public int index; }

    private const string Xcb = "libxcb.so.1", LibC = "libc.so.6";

    [DllImport(Xcb)] private static extern nint xcb_connect(byte* display, int* screen);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(nint c);
    [DllImport(Xcb)] private static extern void xcb_disconnect(nint c);
    [DllImport(Xcb)] private static extern nint xcb_get_setup(nint c);
    [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(nint setup);
    [DllImport(Xcb)] private static extern uint xcb_intern_atom(nint c, byte onlyIfExists, ushort length, byte* name);
    [DllImport(Xcb)] private static extern nint xcb_intern_atom_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern uint xcb_get_property(nint c, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [DllImport(Xcb)] private static extern nint xcb_get_property_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern void* xcb_get_property_value(nint reply);
    [DllImport(Xcb)] private static extern int xcb_get_property_value_length(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_query_tree(nint c, uint window);
    [DllImport(Xcb)] private static extern nint xcb_query_tree_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern uint* xcb_query_tree_children(nint reply);
    [DllImport(Xcb)] private static extern int xcb_query_tree_children_length(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_get_geometry(nint c, uint drawable);
    [DllImport(Xcb)] private static extern nint xcb_get_geometry_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern uint xcb_translate_coordinates(nint c, uint source, uint destination, short x, short y);
    [DllImport(Xcb)] private static extern nint xcb_translate_coordinates_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern uint xcb_get_image(nint c, byte format, uint drawable, short x, short y, ushort width, ushort height, uint planeMask);
    [DllImport(Xcb)] private static extern nint xcb_get_image_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern byte* xcb_get_image_data(nint reply);
    [DllImport(Xcb)] private static extern int xcb_get_image_data_length(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_send_event(nint c, byte propagate, uint destination, uint mask, byte* e);
    [DllImport(Xcb)] private static extern int xcb_flush(nint c);
    [DllImport(LibC)] private static extern void free(nint p);
}
