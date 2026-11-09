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
        // It went in parts: a server whose request limit passed the image would have taken it whole, and proved nothing here.
        Assert.Equal(1, app.TransfersBegun);
        Assert.Equal(0, app.PendingTransfers);
    }

    [Fact]
    public async Task A_requestor_that_stops_reading_is_let_go_and_the_next_paste_is_served()
    {
        var png = Large();
        using var app = new LinuxClipboard(Selection, null, incrIdle: TimeSpan.FromSeconds(1));
        await app.SetAsync(Copy(png));

        using (var stalled = new Requestor(Selection))
        {
            Assert.True(stalled.Begin(ClipboardContent.PngImage, "STALLED_TRANSFER", start: true), "the owner did not begin an INCR transfer");
            Assert.True(stalled.FirstPart("STALLED_TRANSFER"), "the owner sent no first part");
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

    // One window asking for two large formats at once, each into a property of its own: the first one finishing must not
    // take the owner's watch on the window from the second.
    [Fact]
    public async Task A_transfer_that_ends_leaves_the_requestors_other_transfer_going()
    {
        const string Other = "image/x-shenora-test";
        using var app = new LinuxClipboard(Selection, null);
        await app.SetAsync(new ClipboardContent
        {
            Formats = new Dictionary<string, ReadOnlyMemory<byte>> { [ClipboardContent.PngImage] = Large(), [Other] = Large() },
        });

        using var requestor = new Requestor(Selection);
        Assert.True(requestor.Begin(ClipboardContent.PngImage, "TRANSFER_ONE", start: true), "the first transfer did not begin");
        Assert.True(requestor.Begin(Other, "TRANSFER_TWO", start: false), "the second transfer did not begin");
        Assert.Equal(2, app.PendingTransfers);

        Assert.True(requestor.Drain("TRANSFER_ONE"), "the first transfer did not finish");
        var watch = Stopwatch.StartNew();   // the owner counts it done just after writing the part that ends it
        while (app.PendingTransfers != 1 && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
        Assert.Equal(1, app.PendingTransfers);

        // Only now does the requestor ask for the second one's first part, by deleting its announcement.
        requestor.Start("TRANSFER_TWO");
        Assert.True(requestor.FirstPart("TRANSFER_TWO"), "the second transfer stopped when the first one ended");
    }

    /// <summary>A bare X client that asks the owner for targets into properties of its own window, and takes the parts of an
    /// INCR answer as it is told to.</summary>
    private sealed unsafe class Requestor : IDisposable
    {
        private readonly nint _c;
        private readonly uint _window, _selection, _incr;
        private readonly List<nint> _held = [];   // events seen while waiting for another, for a later wait

        public Requestor(string selection)
        {
            int screen;
            _c = xcb_connect(null, &screen);
            if (xcb_connection_has_error(_c) != 0) throw new InvalidOperationException("No X display answered (DISPLAY).");
            var roots = xcb_setup_roots_iterator(xcb_get_setup(_c));
            _window = xcb_generate_id(_c);
            var events = 0x400000u;   // PropertyChange
            xcb_create_window(_c, 0, _window, *(uint*)roots.data, 0, 0, 1, 1, 0, 2 /* InputOnly */, 0, 0x800, &events);
            _selection = Atom(selection);
            _incr = Atom("INCR");
        }

        /// <summary>Ask for <paramref name="target"/> into <paramref name="property"/> and read the owner's answer: true when it
        /// is an INCR announcement. With <paramref name="start"/> the announcement is deleted at once, which asks for the first
        /// part; without, it is left where it is.</summary>
        public bool Begin(string target, string property, bool start)
        {
            uint t = Atom(target), p = Atom(property);
            xcb_convert_selection(_c, _window, _selection, t, p, 0);
            xcb_flush(_c);
            if (!Await(e => Type(e) == 31 && U32(e, 8) == _window && U32(e, 16) == t && U32(e, 20) == p)) return false;   // SelectionNotify, answered
            if (Read(p, delete: false).Type != _incr) return false;
            // The announcement's own write raised a PropertyNotify, ahead of the answer: not a part.
            foreach (var e in _held.Where(Written(p)).ToList())
            {
                free(e);
                _held.Remove(e);
            }
            if (start) Start(property);
            return true;
        }

        /// <summary>Delete the property, which is how the owner is asked for the next part (or the first).</summary>
        public void Start(string property)
        {
            xcb_delete_property(_c, _window, Atom(property));
            xcb_flush(_c);
        }

        /// <summary>The owner has written a part into the property, which stays there unread.</summary>
        public bool FirstPart(string property) => Await(Written(Atom(property)));

        /// <summary>Take every part, one at a time, up to the empty one that ends the transfer; false if one never comes.</summary>
        public bool Drain(string property)
        {
            var p = Atom(property);
            while (true)
            {
                if (!Await(Written(p))) return false;
                if (Read(p, delete: true).Length == 0) return true;
            }
        }

        private Func<nint, bool> Written(uint property) =>
            e => Type(e) == 28 && U32(e, 4) == _window && U32(e, 8) == property && ((byte*)e)[16] == 0;   // PropertyNotify, a new value

        // The property's type and how many bytes it holds (up to a part's worth); with delete, it is taken.
        private (uint Type, int Length) Read(uint property, bool delete)
        {
            var reply = xcb_get_property_reply(_c, xcb_get_property(_c, (byte)(delete ? 1 : 0), _window, property, 0, 0, 1 << 19), null);
            if (reply == 0) return (0, 0);
            try { return (U32(reply, 8), xcb_get_property_value_length(reply)); }
            finally { free(reply); }
        }

        private bool Await(Func<nint, bool> match)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                for (var i = 0; i < _held.Count; i++)
                {
                    if (!match(_held[i])) continue;
                    free(_held[i]);
                    _held.RemoveAt(i);
                    return true;
                }
                nint e;
                while ((e = xcb_poll_for_event(_c)) != 0)
                {
                    if (match(e))
                    {
                        free(e);
                        return true;
                    }
                    _held.Add(e);
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

        public void Dispose()
        {
            foreach (var e in _held) free(e);
            xcb_disconnect(_c);
        }

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
        [DllImport(Xcb)] private static extern uint xcb_delete_property(nint c, uint window, uint property);
        [DllImport(Xcb)] private static extern uint xcb_get_property(nint c, byte delete, uint window, uint property, uint type, uint offset, uint length);
        [DllImport(Xcb)] private static extern nint xcb_get_property_reply(nint c, uint cookie, nint* error);
        [DllImport(Xcb)] private static extern int xcb_get_property_value_length(nint reply);
        [DllImport(Xcb)] private static extern int xcb_flush(nint c);
        [DllImport(Xcb)] private static extern nint xcb_poll_for_event(nint c);
        [DllImport(LibC)] private static extern void free(nint p);
    }
}
