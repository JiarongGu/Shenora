using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Tests.Linux;

/// <summary>
/// A clipboard manager as the freedesktop spec describes one, scripted: it owns <c>CLIPBOARD_MANAGER</c>, and on a
/// <c>SAVE_TARGETS</c> request reads each target the requestor names (and <c>TARGETS</c>) from the <c>CLIPBOARD</c>'s
/// owner, one conversion at a time, then answers the request. A manager that keeps a copy takes the clipboard over
/// and serves it, as a real one does, so a copy outlives the app that made it. Its own connection, its own thread.
/// </summary>
internal sealed unsafe class ScriptedClipboardManager : IDisposable
{
    internal enum Behaviour { Keep, Refuse, Ignore }

    private const byte SelectionRequest = 30, SelectionNotify = 31;
    private const uint AtomType = 4, StringType = 31;
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(3);

    private readonly Behaviour _behaviour;
    private readonly Dictionary<string, uint> _atoms = new();
    private readonly Dictionary<uint, string> _names = new();
    private readonly Dictionary<uint, byte[]> _kept = new();
    private readonly Thread _thread;
    private readonly nint _c;
    private readonly uint _window, _clipboard, _manager, _saveTargets, _targets, _property;
    private volatile bool _stopping;
    private int _requests;

    public ScriptedClipboardManager(Behaviour behaviour)
    {
        _behaviour = behaviour;
        int screen;
        _c = xcb_connect(null, &screen);
        if (xcb_connection_has_error(_c) != 0) throw new InvalidOperationException("No X display answered (DISPLAY).");
        var roots = xcb_setup_roots_iterator(xcb_get_setup(_c));
        var root = *(uint*)roots.data;
        _window = xcb_generate_id(_c);
        xcb_create_window(_c, 0, _window, root, 0, 0, 1, 1, 0, 2 /* InputOnly */, 0, 0, null);
        _clipboard = Atom("CLIPBOARD");
        _manager = Atom("CLIPBOARD_MANAGER");
        _saveTargets = Atom("SAVE_TARGETS");
        _targets = Atom("TARGETS");
        _property = Atom("MANAGER_TRANSFER");
        xcb_set_selection_owner(_c, _window, _manager, 0);
        xcb_flush(_c);
        if (Owner(_manager) != _window) throw new InvalidOperationException("The X server did not give CLIPBOARD_MANAGER to the manager.");
        _thread = new Thread(Loop) { IsBackground = true, Name = "scripted clipboard manager" };
        _thread.Start();
    }

    /// <summary>How many SAVE_TARGETS requests it has had.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Each target it kept, by name, with the bytes the requestor served.</summary>
    public IReadOnlyDictionary<string, byte[]> Kept
    {
        get { lock (_kept) return _kept.ToDictionary(k => _names[k.Key], k => k.Value); }
    }

    /// <summary>The targets the last request asked to have kept, by name; empty when none had a property.</summary>
    public IReadOnlyList<string> Asked { get; private set; } = [];

    public void Dispose()
    {
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        xcb_disconnect(_c);
    }

    private void Loop()
    {
        while (!_stopping)
        {
            nint e;
            while (!_stopping && (e = xcb_poll_for_event(_c)) != 0)
            {
                try { Handle(e); }
                finally { free(e); }
            }
            Thread.Sleep(5);
        }
    }

    private void Handle(nint e)
    {
        if (Type(e) != SelectionRequest) return;
        uint time = U32(e, 4), requestor = U32(e, 12), selection = U32(e, 16), target = U32(e, 20), property = U32(e, 24);
        if (selection == _manager && target == _saveTargets) Save(requestor, time, property);
        else if (selection == _clipboard) Serve(requestor, time, target, property);
    }

    // SAVE_TARGETS: the property names what to keep. Reads each of them from the clipboard's owner, then answers.
    private void Save(uint requestor, uint time, uint property)
    {
        Interlocked.Increment(ref _requests);
        if (_behaviour == Behaviour.Ignore) return;
        if (_behaviour == Behaviour.Refuse) { Notify(requestor, time, _manager, _saveTargets, 0); return; }

        var wanted = new List<uint>();
        if (property != 0 && ReadProperty(requestor, property, delete: false) is { } list)
            for (var i = 0; i + 4 <= list.Length; i += 4) wanted.Add(BitConverter.ToUInt32(list, i));
        Asked = wanted.Select(Name).ToList();

        var kept = new Dictionary<uint, byte[]>();
        foreach (var target in wanted.Prepend(_targets).Distinct())
            if (Convert(target) is { } data) kept[target] = data;
        lock (_kept) foreach (var (target, data) in kept) _kept[target] = data;

        // As a real manager does: the clipboard is the manager's now, and answers for the app that is going.
        xcb_set_selection_owner(_c, _window, _clipboard, 0);
        Notify(requestor, time, _manager, _saveTargets, property);
    }

    // The clipboard once the manager holds it: TARGETS, and each target it kept.
    private void Serve(uint requestor, uint time, uint target, uint property)
    {
        var answered = false;
        lock (_kept)
        {
            if (target == _targets)
            {
                var atoms = _kept.Keys.Where(k => k != _targets).ToArray();
                fixed (uint* list = atoms) xcb_change_property(_c, 0, requestor, property, AtomType, 32, (uint)atoms.Length, list);
                answered = true;
            }
            else if (_kept.TryGetValue(target, out var data))
            {
                fixed (byte* bytes = data) xcb_change_property(_c, 0, requestor, property, StringType, 8, (uint)data.Length, bytes);
                answered = true;
            }
        }
        Notify(requestor, time, _clipboard, target, answered ? property : 0);
    }

    // The clipboard's owner's copy as `target`, or null when it does not answer in time or has none.
    private byte[]? Convert(uint target)
    {
        xcb_delete_property(_c, _window, _property);
        xcb_convert_selection(_c, _window, _clipboard, target, _property, 0);
        xcb_flush(_c);
        var until = Environment.TickCount64 + (long)Answer.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            var e = xcb_poll_for_event(_c);
            if (e == 0) { Thread.Sleep(2); continue; }
            try
            {
                if (Type(e) == SelectionNotify && U32(e, 8) == _window && U32(e, 16) == target)
                    return U32(e, 20) == 0 ? null : ReadProperty(_window, _property, delete: true);
                Handle(e);
            }
            finally { free(e); }
        }
        return null;
    }

    private byte[]? ReadProperty(uint window, uint property, bool delete)
    {
        var reply = xcb_get_property_reply(_c, xcb_get_property(_c, (byte)(delete ? 1 : 0), window, property, 0, 0, 1 << 20), null);
        if (reply == 0) return null;
        try { return new ReadOnlySpan<byte>(xcb_get_property_value(reply), xcb_get_property_value_length(reply)).ToArray(); }
        finally { free(reply); }
    }

    private void Notify(uint requestor, uint time, uint selection, uint target, uint property)
    {
        var notify = stackalloc byte[32];
        new Span<byte>(notify, 32).Clear();
        notify[0] = SelectionNotify;
        *(uint*)(notify + 4) = time;
        *(uint*)(notify + 8) = requestor;
        *(uint*)(notify + 12) = selection;
        *(uint*)(notify + 16) = target;
        *(uint*)(notify + 20) = property;
        xcb_send_event(_c, 0, requestor, 0, notify);
        xcb_flush(_c);
    }

    private uint Owner(uint selection)
    {
        var reply = xcb_get_selection_owner_reply(_c, xcb_get_selection_owner(_c, selection), null);
        if (reply == 0) return 0;
        try { return U32(reply, 8); }
        finally { free(reply); }
    }

    private uint Atom(string name)
    {
        if (_atoms.TryGetValue(name, out var atom)) return atom;
        var bytes = Encoding.UTF8.GetBytes(name);
        nint reply;
        fixed (byte* b = bytes) reply = xcb_intern_atom_reply(_c, xcb_intern_atom(_c, 0, (ushort)bytes.Length, b), null);
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
        try { name = Encoding.UTF8.GetString(xcb_get_atom_name_name(reply), xcb_get_atom_name_name_length(reply)); }
        finally { free(reply); }
        _names[atom] = name;
        _atoms[name] = atom;
        return name;
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
    [DllImport(LibC)] private static extern void free(nint p);
}
