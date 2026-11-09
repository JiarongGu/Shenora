using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Linux;

/// <summary>
/// A copy larger than one X request, which the owner serves in parts (ICCCM <c>INCR</c>): the target says how much is coming,
/// and each part is written once the requestor has deleted the one before. On a selection of the tests' own name, against
/// another <see cref="LinuxClipboard"/> as the requestor, and a bare one that stops reading.
/// </summary>
public class LinuxClipboardLargeTests
{
    private const string Selection = "SHENORA_TEST_LARGE";

    // Over a typical server's 16 MB request, and not a multiple of the part size.
    private static byte[] Large()
    {
        var bytes = new byte[20 * 1024 * 1024 + 123];
        new Random(7).NextBytes(bytes);
        bytes[0] = 0x89;
        return bytes;
    }

    private static ClipboardContent Copy(byte[] png) => new()
    {
        Formats = new Dictionary<string, ReadOnlyMemory<byte>> { [ClipboardContent.PngImage] = png },
    };

    [Fact]
    public async Task A_format_larger_than_one_request_reaches_another_app_whole()
    {
        var png = Large();
        using var app = new LinuxClipboard(Selection, null);
        await app.SetAsync(Copy(png));

        using var other = new LinuxClipboard(Selection, null);
        var pasted = await other.GetAsync();

        Assert.True(pasted.Formats.TryGetValue(ClipboardContent.PngImage, out var got), "the large image did not arrive");
        Assert.True(png.AsSpan().SequenceEqual(got.Span), "the image arrived changed");
        Assert.Equal(0, app.PendingTransfers);
    }

    [Fact]
    public async Task A_requestor_that_stops_reading_is_let_go_and_the_next_paste_is_served()
    {
        var png = Large();
        using var app = new LinuxClipboard(Selection, null, incrIdle: TimeSpan.FromSeconds(1));
        await app.SetAsync(Copy(png));

        using (var stalled = new StallingRequestor(Selection, ClipboardContent.PngImage))
        {
            Assert.True(stalled.ReadFirstPart(), "the owner did not begin an INCR transfer");
            Assert.Equal(1, app.PendingTransfers);

            // Another app pastes while the first is stuck mid-transfer.
            using var other = new LinuxClipboard(Selection, null);
            var pasted = await other.GetAsync();
            Assert.True(png.AsSpan().SequenceEqual(pasted.Formats[ClipboardContent.PngImage].Span), "a paste beside a stalled one failed");

            var watch = Stopwatch.StartNew();
            while (app.PendingTransfers != 0 && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
            Assert.Equal(0, app.PendingTransfers);
        }
    }

    /// <summary>A requestor that asks for a target, takes the first part of an INCR answer, and goes no further.</summary>
    private sealed unsafe class StallingRequestor : IDisposable
    {
        private readonly nint _c;
        private readonly uint _window, _selection, _property, _target, _incr;

        public StallingRequestor(string selection, string target)
        {
            int screen;
            _c = xcb_connect(null, &screen);
            if (xcb_connection_has_error(_c) != 0) throw new InvalidOperationException("No X display answered (DISPLAY).");
            var roots = xcb_setup_roots_iterator(xcb_get_setup(_c));
            _window = xcb_generate_id(_c);
            var events = 0x400000u;   // PropertyChange
            xcb_create_window(_c, 0, _window, *(uint*)roots.data, 0, 0, 1, 1, 0, 2 /* InputOnly */, 0, 0x800, &events);
            _selection = Atom(selection);
            _target = Atom(target);
            _property = Atom("STALLED_TRANSFER");
            _incr = Atom("INCR");
        }

        /// <summary>Ask, and read the INCR announcement and then the first part; false if either never comes.</summary>
        public bool ReadFirstPart()
        {
            xcb_convert_selection(_c, _window, _selection, _target, _property, 0);
            xcb_flush(_c);
            if (!Await(e => Type(e) == 31 && U32(e, 8) == _window)) return false;   // SelectionNotify
            if (Read(delete: true) != _incr) return false;
            return Await(e => Type(e) == 28 && U32(e, 4) == _window && U32(e, 8) == _property && ((byte*)e)[16] == 0)   // the first part
                && Read(delete: false) != _incr;
        }

        // The property's type; deleting it is what asks the owner for its next part.
        private uint Read(bool delete)
        {
            var reply = xcb_get_property_reply(_c, xcb_get_property(_c, (byte)(delete ? 1 : 0), _window, _property, 0, 0, 4), null);
            if (reply == 0) return 0;
            try { return U32(reply, 8); }
            finally { free(reply); }
        }

        private bool Await(Func<nint, bool> match)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                nint e;
                while ((e = xcb_poll_for_event(_c)) != 0)
                {
                    try { if (match(e)) return true; }
                    finally { free(e); }
                }
                Thread.Sleep(20);
            }
            return false;
        }

        private uint Atom(string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            fixed (byte* p = bytes)
            {
                var reply = xcb_intern_atom_reply(_c, xcb_intern_atom(_c, 0, (ushort)bytes.Length, p), null);
                try { return U32(reply, 8); }
                finally { free(reply); }
            }
        }

        public void Dispose() => xcb_disconnect(_c);

        private static byte Type(nint e) => (byte)(*(byte*)e & 0x7F);
        private static uint U32(nint p, int offset) => *(uint*)(p + offset);

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenIterator { public nint data; public int rem; public int index; }

        private const string Xcb = "libxcb.so.1", LibC = "libc.so.6";

        [DllImport(Xcb)] private static extern nint xcb_connect(byte* display, int* screen);
        [DllImport(Xcb)] private static extern int xcb_connection_has_error(nint c);
        [DllImport(Xcb)] private static extern void xcb_disconnect(nint c);
        [DllImport(Xcb)] private static extern nint xcb_get_setup(nint c);
        [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(nint setup);
        [DllImport(Xcb)] private static extern uint xcb_generate_id(nint c);
        [DllImport(Xcb)] private static extern uint xcb_create_window(nint c, byte depth, uint window, uint parent, short x, short y, ushort width, ushort height, ushort border, ushort @class, uint visual, uint mask, uint* values);
        [DllImport(Xcb)] private static extern uint xcb_intern_atom(nint c, byte onlyIfExists, ushort length, byte* name);
        [DllImport(Xcb)] private static extern nint xcb_intern_atom_reply(nint c, uint cookie, nint* error);
        [DllImport(Xcb)] private static extern uint xcb_convert_selection(nint c, uint requestor, uint selection, uint target, uint property, uint time);
        [DllImport(Xcb)] private static extern uint xcb_get_property(nint c, byte delete, uint window, uint property, uint type, uint offset, uint length);
        [DllImport(Xcb)] private static extern nint xcb_get_property_reply(nint c, uint cookie, nint* error);
        [DllImport(Xcb)] private static extern int xcb_flush(nint c);
        [DllImport(Xcb)] private static extern nint xcb_poll_for_event(nint c);
        [DllImport(LibC)] private static extern void free(nint p);
    }
}
