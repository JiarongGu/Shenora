#if CEF_LINUX
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The Linux clipboard: X11's <c>CLIPBOARD</c> selection, over a connection of the kit's own through libxcb, which CEF
/// itself needs. On a Wayland desktop CEF draws through XWayland, which carries the X11 clipboard to Wayland apps and
/// back. <see cref="X11ClipboardFormats"/> decides what is offered and how another app's copy reads.
/// <para>
/// X11 keeps no copy of the clipboard: the app that copied ANSWERS each paste. So one thread owns the connection and
/// answers other apps' requests while this app holds the clipboard, whatever else the app is doing; Xlib was not used
/// because its error handler is the whole process's, and its default one exits on an error as ordinary as a
/// requestor's window closing mid-paste.
/// </para>
/// <para>
/// What the app copied goes with it when it exits, so <see cref="Dispose"/> first hands the copy to the clipboard
/// manager, if one runs, through the freedesktop <c>SAVE_TARGETS</c> request: the manager reads each target from this app,
/// which keeps answering until the manager says it is done or <see cref="HandoverLimit"/> passes. A manager that is not
/// there costs nothing; one that does not answer costs the limit, once. ⚠ Each target is read on its own: <c>MULTIPLE</c>
/// is not offered, so a manager that asks only for it keeps nothing.
/// </para>
/// <para>
/// A format larger than one X request (about 16 MB on a typical server) is sent in parts (ICCCM <c>INCR</c>): the answer
/// says how much is coming, and each part is written once the requestor has deleted the one before, from the connection's
/// own thread, so a slow requestor holds up nobody. One that stops taking parts is let go after <see cref="IncrIdleLimit"/>.
/// </para>
/// </summary>
internal sealed unsafe class LinuxClipboard : IClipboardService, IDisposable
{
    private const string Xcb = "libxcb.so.1", LibC = "libc.so.6";
    private const byte SelectionClear = 29, SelectionRequest = 30, SelectionNotify = 31, PropertyNotify = 28;
    private const uint AtomType = 4, IntegerType = 19, StringType = 31;
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(2);
    private const int MaxTransfer = 256 << 20;   // what one read takes in parts before giving up on its owner
    private const int IncrPart = 1 << 20;        // what one part of a large copy holds, within one request

    /// <summary>The longest <see cref="Dispose"/> waits for a clipboard manager to finish keeping the copy.</summary>
    internal static readonly TimeSpan HandoverLimit = TimeSpan.FromSeconds(5);

    /// <summary>How long a transfer in parts waits for its requestor to take the part before it is given up on.</summary>
    internal static readonly TimeSpan IncrIdleLimit = TimeSpan.FromSeconds(30);

    private readonly string _selectionName;
    private readonly TimeSpan _handover, _incrIdle;
    private int _pending;
    private readonly ILogger? _log;
    private readonly ConcurrentQueue<(Action Run, Action<Exception> Fail)> _work = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _atoms = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _names = [];
    private Thread? _thread;
    private nint _c;
    private int _fd, _wakeRead = -1, _wakeWrite = -1, _abandoned;
    private uint _window, _selection, _property, _targets, _timestamp, _incr, _stamp, _maxBytes;
    private bool _stopping;

    // While this app holds the selection: each target it serves, by atom, with the type its data is written as.
    // The connection's thread alone touches it.
    private Dictionary<uint, (uint Type, byte[] Data)>? _offered;
    private uint _ownedSince;

    // The large copies being sent in parts, each to the requestor whose deletes ask for the next part. Likewise.
    private readonly List<Incr> _transfers = [];

    private sealed class Incr(uint requestor, uint property, uint type, byte[] data, long touched)
    {
        public uint Requestor { get; } = requestor;
        public uint Property { get; } = property;
        public uint Type { get; } = type;
        public byte[] Data { get; } = data;
        public int Sent;
        public long Touched = touched;
    }

    /// <summary>The system clipboard.</summary>
    public LinuxClipboard(ILogger<LinuxClipboard>? log = null) : this("CLIPBOARD", log) { }

    /// <summary>A selection of this name, which no other app reads: how the connection is exercised without the user's
    /// clipboard.</summary>
    internal LinuxClipboard(string selectionName, ILogger? log, TimeSpan? handover = null, TimeSpan? incrIdle = null)
    {
        _selectionName = selectionName;
        _log = log;
        _handover = handover ?? HandoverLimit;
        _incrIdle = incrIdle ?? IncrIdleLimit;
    }

    /// <summary>How many large copies are being sent in parts right now.</summary>
    internal int PendingTransfers => Volatile.Read(ref _pending);

    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SetAsync(new ClipboardContent { Text = text });
    }

    public Task<string?> GetTextAsync() => Run(() => Read((targets, fetch) => X11ClipboardFormats.ReadText(targets, fetch), (string?)null));

    public Task ClearAsync() => Run(() => { Relinquish(); return true; });

    public Task SetAsync(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var files = content.Files.Select(f => { ArgumentException.ThrowIfNullOrWhiteSpace(f, nameof(content)); return Path.GetFullPath(f); }).ToList();
        // Decided before anything is written, so content refused part-way leaves the clipboard as it was.
        var offer = X11ClipboardFormats.Offer(content with { Files = files });
        return Run(() =>
        {
            if (content.IsEmpty) { Relinquish(); return true; }
            var offered = new Dictionary<uint, (uint, byte[])>();
            foreach (var target in offer) offered[Atom(target.Name)] = (Atom(target.Type), target.Data);
            var time = ServerTime();
            xcb_set_selection_owner(_c, _window, _selection, time);
            if (Owner() != _window) throw new InvalidOperationException("The X server did not give the clipboard to this app.");
            _offered = offered;
            _ownedSince = time;
            return true;
        });
    }

    public Task<ClipboardContent> GetAsync() => Run(() => Read(X11ClipboardFormats.Read, new ClipboardContent()));

    // The selection's targets and a way to fetch each: from what this app offers when it holds the clipboard, else
    // from the app that does. None when no app holds it, or the holder does not say what it offers.
    private T Read<T>(Func<IReadOnlyCollection<string>, Func<string, byte[]?>, T> read, T none)
    {
        var owner = Owner();
        if (owner == 0) return none;
        if (owner == _window && _offered is { } own)
            return read(own.Keys.Select(Name).ToList(), name => own.TryGetValue(Atom(name), out var t) ? t.Data : null);
        if (Convert(_targets) is not { } list) return none;
        var targets = new List<string>();
        for (var i = 0; i + 4 <= list.Length; i += 4)
            if (BitConverter.ToUInt32(list, i) is var atom and not 0) targets.Add(Name(atom));
        return read(targets, name => Convert(Atom(name)));
    }

    public void Dispose()
    {
        // The thread that answers other apps goes last, so the manager can read the copy before it does.
        Task<bool>? handover = null;
        lock (_gate)
        {
            if (_stopping) return;
            if (_thread is not null && _selectionName == "CLIPBOARD") handover = Run(HandOver);
        }
        try { handover?.Wait(_handover + Answer + Answer); }
        catch (Exception ex) { AppCallback.Log(_log, () => $"[Shenora.Chromium] The clipboard was not handed to a manager: {ex.GetBaseException().Message}", LogLevel.Debug); }

        Thread? thread;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            thread = _thread;
            Wake();
        }
        thread?.Join(TimeSpan.FromSeconds(2));
    }

    // ── the connection's thread ────────────────────────────────────────────────────────────────────────────────────

    // Queued, woken and (on the thread's way out) closed under one gate: nothing is queued after the thread has taken
    // its last look, and no wake-up reaches a pipe that is closed, or a descriptor that number now names.
    private Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            Start();
            _work.Enqueue((
                () =>
                {
                    try { done.TrySetResult(work()); }
                    catch (Exception ex) { done.TrySetException(ex); }
                },
                ex => done.TrySetException(ex)));
            Wake();
        }
        return done.Task;
    }

    // Connects when no thread is running: the first call, or the first since the connection closed. Under the gate.
    private void Start()
    {
        if (_thread is not null) return;
        int screen;
        var c = xcb_connect(null, &screen);
        if (xcb_connection_has_error(c) != 0)
        {
            xcb_disconnect(c);
            throw new InvalidOperationException("The clipboard needs an X display (DISPLAY), and none answered.");
        }
        var fds = stackalloc int[2];
        if (pipe2(fds, 0x80000 /* O_CLOEXEC */) != 0)
        {
            xcb_disconnect(c);
            throw new InvalidOperationException("The clipboard could not make its wake-up pipe.");
        }
        var roots = xcb_setup_roots_iterator(xcb_get_setup(c));
        for (var i = 0; i < screen && roots.rem > 1; i++) xcb_screen_next(&roots);
        var root = *(uint*)roots.data;

        // A window of the connection's own, never shown: the owner other apps ask, and where answers land.
        _c = c;
        _window = xcb_generate_id(c);
        var events = 0x400000u;   // PropertyChange
        xcb_create_window(c, 0, _window, root, 0, 0, 1, 1, 0, 2 /* InputOnly */, 0, 0x800 /* EventMask */, &events);
        _selection = Atom(_selectionName);
        _property = Atom("SHENORA_CLIPBOARD");
        _targets = Atom("TARGETS");
        _timestamp = Atom("TIMESTAMP");
        _incr = Atom("INCR");
        _stamp = Atom("SHENORA_STAMP");
        // In 4-byte units, BIG-REQUESTS included; less the ChangeProperty request's own header.
        _maxBytes = xcb_get_maximum_request_length(c) * 4 - 64;
        _fd = xcb_get_file_descriptor(c);
        xcb_flush(c);
        _wakeRead = fds[0];
        _wakeWrite = fds[1];
        _thread = new Thread(Loop) { IsBackground = true, Name = "Shenora clipboard (X11)" };
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            while (!Volatile.Read(ref _stopping))
            {
                while (_work.TryDequeue(out var job)) job.Run();
                Pump();
                ExpireIncr();
                if (xcb_connection_has_error(_c) != 0)
                {
                    AppCallback.Log(_log, () => "[Shenora.Chromium] The clipboard's X connection broke", LogLevel.Warning);
                    break;
                }
                Wait(_transfers.Count > 0 ? 250 : -1, wake: true);   // a transfer's idle limit is read again soon
            }
        }
        finally
        {
            xcb_destroy_window(_c, _window);
            xcb_flush(_c);
            xcb_disconnect(_c);
            lock (_gate)
            {
                // The next call connects afresh, unless this one was disposed; what was queued behind it ends here.
                _thread = null;
                close(_wakeRead);
                close(_wakeWrite);
                _wakeRead = _wakeWrite = -1;
                _offered = null;
                _transfers.Clear();
                Volatile.Write(ref _pending, 0);
                _atoms.Clear();
                _names.Clear();
                Exception closed = _stopping ? new ObjectDisposedException(nameof(LinuxClipboard)) : new InvalidOperationException("The clipboard's X connection closed.");
                while (_work.TryDequeue(out var job)) job.Fail(closed);
            }
        }
    }

    // Under the gate.
    private void Wake()
    {
        if (_wakeWrite < 0) return;
        byte one = 1;
        write(_wakeWrite, &one, 1);
    }

    /// <summary>Block until the connection has something to read (or the wake-up pipe, when <paramref name="wake"/>).</summary>
    private void Wait(int milliseconds, bool wake)
    {
        var fds = stackalloc PollFd[2];
        fds[0] = new PollFd { fd = _fd, events = 1 };
        fds[1] = new PollFd { fd = _wakeRead, events = 1 };
        if (poll(fds, wake ? 2u : 1u, milliseconds) > 0 && wake && (fds[1].revents & 1) != 0)
        {
            var drain = stackalloc byte[64];
            read(_wakeRead, drain, 64);
        }
    }

    private void Pump()
    {
        nint e;
        while ((e = xcb_poll_for_event(_c)) != 0)
        {
            try { Handle(e); }
            finally { free(e); }
        }
    }

    /// <summary>The first event <paramref name="match"/> accepts within <paramref name="timeout"/>, answering other apps
    /// meanwhile, or 0. The caller frees it.</summary>
    private nint WaitFor(Func<nint, bool> match, TimeSpan timeout)
    {
        var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            nint e;
            while ((e = xcb_poll_for_event(_c)) != 0)
            {
                if (match(e)) return e;
                try { Handle(e); }
                finally { free(e); }
            }
            var left = until - Environment.TickCount64;
            if (left <= 0 || xcb_connection_has_error(_c) != 0) return 0;
            Wait((int)left, wake: false);
        }
    }

    private void Handle(nint e)
    {
        switch (Type(e))
        {
            case SelectionRequest:
                Serve(e);
                break;
            // Another app copied, unless the clear is older than this app's own copy: one taken while this app was
            // taking it, which the copy has already replaced.
            case SelectionClear when U32(e, 12) == _selection && !(_ownedSince != 0 && (int)(U32(e, 4) - _ownedSince) < 0):
                _offered = null;
                break;
            // A requestor took the part of a large copy that was there: the next one goes (state 1 is a deletion).
            case PropertyNotify when ((byte*)e)[16] == 1 && _transfers.Find(t => t.Requestor == U32(e, 4) && t.Property == U32(e, 8)) is { } transfer:
                Continue(transfer);
                break;
        }
    }

    // Another app pastes: write the target it asked for onto its window, then tell it where.
    private void Serve(nint request)
    {
        uint time = U32(request, 4), requestor = U32(request, 12), selection = U32(request, 16), target = U32(request, 20);
        var property = U32(request, 24) is var p and not 0 ? p : target;   // an obsolete client names none
        var answered = false;
        if (selection == _selection && _offered is { } offered)
        {
            if (target == _targets)
            {
                var atoms = new[] { _targets, _timestamp }.Concat(offered.Keys).ToArray();
                fixed (uint* list = atoms) xcb_change_property(_c, 0, requestor, property, AtomType, 32, (uint)atoms.Length, list);
                answered = true;
            }
            else if (target == _timestamp)
            {
                var since = _ownedSince;
                xcb_change_property(_c, 0, requestor, property, IntegerType, 32, 1, &since);
                answered = true;
            }
            else if (offered.TryGetValue(target, out var data))
            {
                if (data.Data.Length > _maxBytes) BeginIncr(requestor, property, data.Type, data.Data);
                else fixed (byte* bytes = data.Data) xcb_change_property(_c, 0, requestor, property, data.Type, 8, (uint)data.Data.Length, bytes);
                answered = true;
            }
        }
        var notify = stackalloc byte[32];
        new Span<byte>(notify, 32).Clear();
        notify[0] = SelectionNotify;
        *(uint*)(notify + 4) = time;
        *(uint*)(notify + 8) = requestor;
        *(uint*)(notify + 12) = selection;
        *(uint*)(notify + 16) = target;
        *(uint*)(notify + 20) = answered ? property : 0;
        xcb_send_event(_c, 0, requestor, 0, notify);
        xcb_flush(_c);
    }

    // A copy too large for one request, begun: the requestor's property holds its size, typed INCR, and we watch the
    // requestor's window for the property's deletion, which asks for the first part (ICCCM 2.7.2). The watch comes first so
    // a quick delete is not missed.
    private void BeginIncr(uint requestor, uint property, uint type, byte[] data)
    {
        var events = 0x400000u;   // PropertyChange
        xcb_change_window_attributes(_c, requestor, 0x800 /* EventMask */, &events);
        var size = (uint)Math.Min(data.Length, int.MaxValue);   // a lower bound, which is all ICCCM asks of it
        xcb_change_property(_c, 0, requestor, property, _incr, 32, 1, &size);
        _transfers.RemoveAll(t => t.Requestor == requestor && t.Property == property);   // a new ask replaces its old one
        _transfers.Add(new Incr(requestor, property, type, data, Environment.TickCount64));
        Volatile.Write(ref _pending, _transfers.Count);
    }

    // The requestor deleted the property: write the next part, or the empty one that ends the transfer.
    private void Continue(Incr transfer)
    {
        var left = transfer.Data.Length - transfer.Sent;
        var part = Math.Min(left, (int)Math.Min(IncrPart, _maxBytes));
        fixed (byte* bytes = transfer.Data)
            xcb_change_property(_c, 0, transfer.Requestor, transfer.Property, transfer.Type, 8, (uint)part, bytes + transfer.Sent);
        transfer.Sent += part;
        transfer.Touched = Environment.TickCount64;
        xcb_flush(_c);
        if (part == 0) EndIncr(transfer);
    }

    private void EndIncr(Incr transfer)
    {
        var none = 0u;
        xcb_change_window_attributes(_c, transfer.Requestor, 0x800, &none);   // the window is the requestor's again
        _transfers.Remove(transfer);
        Volatile.Write(ref _pending, _transfers.Count);
    }

    // A requestor that has not taken a part in the idle limit is given up on, so its transfer does not outlive it.
    private void ExpireIncr()
    {
        if (_transfers.Count == 0) return;
        var now = Environment.TickCount64;
        foreach (var t in _transfers.Where(t => now - t.Touched > _incrIdle.TotalMilliseconds).ToList()) EndIncr(t);
        xcb_flush(_c);
    }

    // ── on the connection's thread, inside a Run ───────────────────────────────────────────────────────────────────

    private uint Owner() => Owner(_selection);

    private uint Owner(uint selection)
    {
        var reply = xcb_get_selection_owner_reply(_c, xcb_get_selection_owner(_c, selection), null);
        if (reply == 0) return 0;
        try { return U32(reply, 8); }
        finally { free(reply); }
    }

    // The freedesktop clipboard-manager handover: SAVE_TARGETS, with the targets to keep, to the selection a manager owns.
    // The manager then reads each from this app (Serve, from WaitFor) and answers with a SelectionNotify, whose property
    // is None when it will not. True when it kept the copy.
    private bool HandOver()
    {
        if (_offered is not { Count: > 0 } offered || Owner(_selection) != _window) return false;
        var manager = Atom("CLIPBOARD_MANAGER");
        if (Owner(manager) == 0) return false;
        var save = Atom("SAVE_TARGETS");
        var property = Atom("SHENORA_CLIPBOARD_SAVE");
        var targets = offered.Keys.ToArray();
        fixed (uint* list = targets) xcb_change_property(_c, 0, _window, property, AtomType, 32, (uint)targets.Length, list);
        xcb_convert_selection(_c, _window, manager, save, property, 0);
        xcb_flush(_c);
        var e = WaitFor(x => Type(x) == SelectionNotify && U32(x, 8) == _window && U32(x, 12) == manager && U32(x, 16) == save, _handover);
        if (e == 0) return false;
        try { return U32(e, 20) != 0; }
        finally { free(e); }
    }

    // Leaving the selection with no owner is what an empty clipboard is; any app may.
    private void Relinquish()
    {
        _offered = null;
        xcb_set_selection_owner(_c, 0, _selection, ServerTime());
        xcb_flush(_c);
    }

    // A time the server itself issued, which ICCCM asks for when taking a selection: a zero-length change to a
    // property of our own window reports one.
    private uint ServerTime()
    {
        xcb_change_property(_c, 2 /* Append */, _window, _stamp, StringType, 8, 0, null);
        xcb_flush(_c);
        var e = WaitFor(x => Type(x) == PropertyNotify && U32(x, 4) == _window && U32(x, 8) == _stamp, Answer);
        if (e == 0) return 0;
        try { return U32(e, 12); }
        finally { free(e); }
    }

    /// <summary>Another app's copy as <paramref name="target"/>, or null when it has none or does not answer in time.</summary>
    private byte[]? Convert(uint target)
    {
        xcb_delete_property(_c, _window, _property);
        xcb_convert_selection(_c, _window, _selection, target, _property, 0);
        xcb_flush(_c);
        var e = WaitFor(x => Type(x) == SelectionNotify && U32(x, 8) == _window && U32(x, 12) == _selection && U32(x, 16) == target, Answer);
        if (e == 0) return Abandon();
        uint property;
        try { property = U32(e, 20); }
        finally { free(e); }
        if (property == 0) return null;

        var (type, data) = Property(delete: true);
        if (type != _incr) return data;

        // INCR: the owner sends the data in parts, each written once we delete the one before; an empty part ends it.
        var all = new MemoryStream();
        while (true)
        {
            var part = WaitFor(x => Type(x) == PropertyNotify && U32(x, 4) == _window && U32(x, 8) == _property && ((byte*)x)[16] == 0, Answer);
            if (part == 0) return Abandon();
            free(part);
            var (_, chunk) = Property(delete: true);
            if (chunk is null) return Abandon();
            if (chunk.Length == 0) return all.ToArray();
            all.Write(chunk);
            if (all.Length > MaxTransfer) return Abandon();
        }
    }

    // A transfer given up on: its owner may still write to the property, and the next transfer's delete is the very
    // signal that asks it for more, so later transfers use another.
    private byte[]? Abandon()
    {
        _property = Atom($"SHENORA_CLIPBOARD_{++_abandoned % 4}");
        return null;
    }

    /// <summary>Our window's transfer property, whole, and its type; deleted once read when <paramref name="delete"/>.</summary>
    private (uint Type, byte[]? Data) Property(bool delete)
    {
        var data = new MemoryStream();
        uint type = 0, offset = 0;
        while (true)
        {
            var reply = xcb_get_property_reply(_c, xcb_get_property(_c, (byte)(delete ? 1 : 0), _window, _property, 0, offset, 1 << 20), null);
            if (reply == 0) return (0, null);
            try
            {
                type = U32(reply, 8);
                var after = U32(reply, 12);
                var length = xcb_get_property_value_length(reply);
                data.Write(new ReadOnlySpan<byte>(xcb_get_property_value(reply), length));
                offset += (uint)length / 4;
                if (after == 0) return (type, data.ToArray());
            }
            finally { free(reply); }
        }
    }

    private uint Atom(string name)
    {
        if (_atoms.TryGetValue(name, out var atom)) return atom;
        var bytes = Encoding.UTF8.GetBytes(name);
        nint reply;
        fixed (byte* b = bytes) reply = xcb_intern_atom_reply(_c, xcb_intern_atom(_c, 0, (ushort)bytes.Length, b), null);
        if (reply == 0) throw new InvalidOperationException($"The X server would not name '{name}'.");
        try { atom = U32(reply, 8); }
        finally { free(reply); }
        _atoms[name] = atom;
        _names[atom] = name;
        return atom;
    }

    private string Name(uint atom)
    {
        if (_names.TryGetValue(atom, out var name)) return name;
        var reply = xcb_get_atom_name_reply(_c, xcb_get_atom_name(_c, atom), null);
        if (reply == 0) return "";
        try { name = Encoding.UTF8.GetString(xcb_get_atom_name_name(reply), xcb_get_atom_name_name_length(reply)); }
        finally { free(reply); }
        _names[atom] = name;
        _atoms[name] = atom;
        return name;
    }

    // Events and replies are X's wire format: fixed 32-bit fields, whatever the machine's long.
    private static byte Type(nint e) => (byte)(*(byte*)e & 0x7F);
    private static uint U32(nint p, int offset) => *(uint*)(p + offset);

    // ── libxcb and libc ────────────────────────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator { public nint data; public int rem; public int index; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int fd; public short events; public short revents; }

    [DllImport(Xcb)] private static extern nint xcb_connect(byte* display, int* screen);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(nint c);
    [DllImport(Xcb)] private static extern void xcb_disconnect(nint c);
    [DllImport(Xcb)] private static extern nint xcb_get_setup(nint c);
    [DllImport(Xcb)] private static extern ScreenIterator xcb_setup_roots_iterator(nint setup);
    [DllImport(Xcb)] private static extern void xcb_screen_next(ScreenIterator* iterator);
    [DllImport(Xcb)] private static extern uint xcb_generate_id(nint c);
    [DllImport(Xcb)] private static extern uint xcb_create_window(nint c, byte depth, uint window, uint parent, short x, short y, ushort width, ushort height, ushort border, ushort @class, uint visual, uint mask, uint* values);
    [DllImport(Xcb)] private static extern uint xcb_destroy_window(nint c, uint window);
    [DllImport(Xcb)] private static extern uint xcb_intern_atom(nint c, byte onlyIfExists, ushort length, byte* name);
    [DllImport(Xcb)] private static extern nint xcb_intern_atom_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern uint xcb_get_atom_name(nint c, uint atom);
    [DllImport(Xcb)] private static extern nint xcb_get_atom_name_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern byte* xcb_get_atom_name_name(nint reply);
    [DllImport(Xcb)] private static extern int xcb_get_atom_name_name_length(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_set_selection_owner(nint c, uint owner, uint selection, uint time);
    [DllImport(Xcb)] private static extern uint xcb_get_selection_owner(nint c, uint selection);
    [DllImport(Xcb)] private static extern nint xcb_get_selection_owner_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern uint xcb_convert_selection(nint c, uint requestor, uint selection, uint target, uint property, uint time);
    [DllImport(Xcb)] private static extern uint xcb_change_property(nint c, byte mode, uint window, uint property, uint type, byte format, uint length, void* data);
    [DllImport(Xcb)] private static extern uint xcb_delete_property(nint c, uint window, uint property);
    [DllImport(Xcb)] private static extern uint xcb_get_property(nint c, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [DllImport(Xcb)] private static extern nint xcb_get_property_reply(nint c, uint cookie, nint* error);
    [DllImport(Xcb)] private static extern void* xcb_get_property_value(nint reply);
    [DllImport(Xcb)] private static extern int xcb_get_property_value_length(nint reply);
    [DllImport(Xcb)] private static extern uint xcb_send_event(nint c, byte propagate, uint destination, uint mask, byte* e);
    [DllImport(Xcb)] private static extern int xcb_flush(nint c);
    [DllImport(Xcb)] private static extern nint xcb_poll_for_event(nint c);
    [DllImport(Xcb)] private static extern int xcb_get_file_descriptor(nint c);
    [DllImport(Xcb)] private static extern uint xcb_get_maximum_request_length(nint c);
    [DllImport(Xcb)] private static extern uint xcb_change_window_attributes(nint c, uint window, uint valueMask, void* valueList);

    [DllImport(LibC)] private static extern void free(nint p);
    [DllImport(LibC)] private static extern int pipe2(int* fds, int flags);
    [DllImport(LibC)] private static extern nint read(int fd, byte* buffer, nint count);
    [DllImport(LibC)] private static extern nint write(int fd, byte* buffer, nint count);
    [DllImport(LibC)] private static extern int close(int fd);
    [DllImport(LibC)] private static extern int poll(PollFd* fds, nuint count, int timeout);
}
#endif
