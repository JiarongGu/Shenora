#if CEF_LINUX
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

/// <summary>
/// A StatusNotifierItem, the tray icon of KDE, GNOME's AppIndicator extension and most Linux panels, with its menu over
/// <c>com.canonical.dbusmenu</c>, both over GIO's D-Bus, which CEF itself needs. The panel (the "host") draws the icon
/// and the menu; the item answers its questions. Objects registered here are called on the thread that registered
/// them, through GLib's default main context, which Chromium's UI thread runs, so the tray lives on the UI thread.
/// <para>
/// ⚠ A desktop with no tray host shows nothing (GNOME without the AppIndicator extension), and <see cref="Shown"/> says
/// so, so closing the main window closes it rather than hiding it where nothing can bring it back. A host that
/// appears later is picked up. The icon is a file on the host's side: its folder as the icon theme path and its
/// name, as Chromium's own status icon gives it.
/// </para>
/// </summary>
internal sealed unsafe class LinuxTray : NativeTray
{
    private const string Gio = "libgio-2.0.so.0", GLib = "libglib-2.0.so.0", GObject = "libgobject-2.0.so.0";
    private const string ItemInterface = "org.kde.StatusNotifierItem", MenuInterface = "com.canonical.dbusmenu";
    private const string Watcher = "org.kde.StatusNotifierWatcher";
    private const string ItemPath = "/StatusNotifierItem", MenuPath = "/MenuBar";
    private const int RequestName = 1, Register = 2, HostCheck = 3;   // which answer an async call's callback carries

    private const string ItemXml = """
        <node><interface name="org.kde.StatusNotifierItem">
          <property name="Category" type="s" access="read"/><property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/><property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/><property name="IconName" type="s" access="read"/>
          <property name="IconThemePath" type="s" access="read"/><property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="OverlayIconName" type="s" access="read"/><property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionIconName" type="s" access="read"/><property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
          <property name="AttentionMovieName" type="s" access="read"/><property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/><property name="Menu" type="o" access="read"/>
          <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
          <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
          <signal name="NewTitle"/><signal name="NewIcon"/><signal name="NewToolTip"/>
          <signal name="NewStatus"><arg name="status" type="s"/></signal>
        </interface></node>
        """;

    private const string MenuXml = """
        <node><interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/><property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/><property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="as" direction="in"/>
            <arg type="u" direction="out"/><arg type="(ia{sv}av)" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" direction="in"/><arg type="as" direction="in"/>
            <arg type="a(ia{sv})" direction="out"/></method>
          <method name="GetProperty"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
          <method name="Event"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/>
            <arg type="u" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" direction="in"/><arg type="ai" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" direction="in"/><arg type="b" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" direction="in"/><arg type="ai" direction="out"/><arg type="ai" direction="out"/></method>
          <signal name="ItemsPropertiesUpdated"><arg type="a(ia{sv})"/><arg type="a(ias)"/></signal>
          <signal name="LayoutUpdated"><arg type="u"/><arg type="i"/></signal>
          <signal name="ItemActivationRequested"><arg type="i"/><arg type="u"/></signal>
        </interface></node>
        """;

    // One tray per process: the callbacks find it here.
    private static LinuxTray? _current;

    private readonly ChromiumTray _tray;
    private readonly ILogger? _log;
    private readonly string _iconName, _iconFolder;
    private nint _connection, _itemNode, _menuNode, _vtable;
    private uint _itemId, _menuId, _ownerSignal, _watcherSignal;
    private string _name = "";
    private int _nameTries, _generation;
    private IReadOnlyList<TrayMenuEntry> _entries = [];
    private uint _revision;
    private bool _ownsName, _registered, _hostPresent, _disposed;

    public LinuxTray(ChromiumTray tray, ILogger? log)
    {
        _tray = tray;
        _log = log;
        (_iconFolder, _iconName) = tray.IconPath is { } path
            ? (Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", Path.GetFileNameWithoutExtension(path))
            : ("", "application-x-executable");

        // A connection of its own: the process's shared one ends the app if the bus closes, and closing this one
        // drops the item's name, which takes the icon off the panel.
        nint error = 0;
        var address = g_dbus_address_get_for_bus_sync(2 /* SESSION */, 0, &error);
        if (address != 0)
        {
            _connection = g_dbus_connection_new_for_address_sync(address, 1 | 8 /* AUTHENTICATION_CLIENT | MESSAGE_BUS_CONNECTION */, 0, 0, &error);
            g_free(address);
        }
        if (_connection == 0)
        {
            var reason = TakeError(error);
            AppCallback.Log(_log, () => $"[Shenora.Chromium] No D-Bus session, so no tray icon: {reason}", LogLevel.Warning);
            return;
        }
        try
        {
            _vtable = (nint)NativeMemory.AllocZeroed(11, (nuint)sizeof(nint));   // method_call, get_property, set_property, padding[8]
            ((nint*)_vtable)[0] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*, byte*, nint, nint, nint, void>)&MethodCall;
            ((nint*)_vtable)[1] = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*, byte*, nint*, nint, nint>)&GetProperty;
            _itemNode = Node(ItemXml);
            _menuNode = Node(MenuXml);
            _itemId = RegisterObject(ItemPath, _itemNode, ItemInterface);
            _menuId = RegisterObject(MenuPath, _menuNode, MenuInterface);
            // The watcher arriving (a panel starting after the app) and leaving, and its hosts coming and going.
            _ownerSignal = Subscribe("org.freedesktop.DBus", "org.freedesktop.DBus", "NameOwnerChanged", "/org/freedesktop/DBus", Watcher);
            _watcherSignal = Subscribe(Watcher, Watcher, null, null, null);
        }
        catch
        {
            Release();
            throw;
        }
        _current = this;
        RequestOwnName();
    }

    /// <summary>Registered with a watcher that has a host to show it: only then can the icon bring the window back.</summary>
    public override bool Shown => _registered && _hostPresent;

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registered = _hostPresent = false;
        Release();
    }

    // Whatever of the connection was set up, taken down: on Dispose, or when the constructor failed part-way.
    private void Release()
    {
        if (_current == this) _current = null;
        if (_connection != 0)
        {
            if (_ownerSignal != 0) g_dbus_connection_signal_unsubscribe(_connection, _ownerSignal);
            if (_watcherSignal != 0) g_dbus_connection_signal_unsubscribe(_connection, _watcherSignal);
            if (_itemId != 0) g_dbus_connection_unregister_object(_connection, _itemId);
            if (_menuId != 0) g_dbus_connection_unregister_object(_connection, _menuId);
            g_dbus_connection_close_sync(_connection, 0, null);   // synchronous: the process may be ending
            g_object_unref(_connection);
            _connection = 0;
        }
        if (_itemNode != 0) g_dbus_node_info_unref(_itemNode);
        if (_menuNode != 0) g_dbus_node_info_unref(_menuNode);
        if (_vtable != 0) NativeMemory.Free((void*)_vtable);
        _itemNode = _menuNode = _vtable = 0;
    }

    // Whether the icon can bring the window back changed: a hidden main window is shown if it no longer can.
    private void SetState(bool registered, bool hostPresent)
    {
        var was = Shown;
        _registered = registered;
        _hostPresent = hostPresent;
        if (was && !Shown) _tray.IconGone();
    }

    // ── what the host asks ─────────────────────────────────────────────────────────────────────────────────────────

    private nint ItemProperty(string name) => name switch
    {
        "Category" => Str("ApplicationStatus"),
        "Id" => Str(LinuxPlatform.AppName),
        "Title" => Str(_tray.Text),
        "Status" => Str("Active"),
        "WindowId" => g_variant_new_int32(0),
        "IconName" => Str(_iconName),
        "IconThemePath" => Str(_iconFolder),
        "IconPixmap" or "OverlayIconPixmap" or "AttentionIconPixmap" => Array("(iiay)", []),
        "OverlayIconName" or "AttentionIconName" or "AttentionMovieName" => Str(""),
        "ToolTip" => Tuple(Str(""), Array("(iiay)", []), Str(_tray.Text), Str("")),
        "ItemIsMenu" => g_variant_new_boolean(0),
        "Menu" => ObjectPath(MenuPath),
        _ => 0,
    };

    private static nint MenuProperty(string name) => name switch
    {
        "Version" => g_variant_new_uint32(3),
        "TextDirection" => Str("ltr"),
        "Status" => Str("normal"),
        "IconThemePath" => Array("s", []),
        _ => 0,
    };

    private void ItemMethod(string method, nint invocation)
    {
        // A left click shows the window, as Open does; the host shows the menu itself on a right click.
        if (method == "Activate") _tray.Choose(new TrayMenuEntry("", _tray.ShowWindow));
        g_dbus_method_invocation_return_value(invocation, 0);
    }

    private void MenuMethod(string method, nint parameters, nint invocation)
    {
        switch (method)
        {
            case "GetLayout":
                // The model is asked for afresh each time the host lays the menu out, which is as it opens.
                if (IntAt(parameters, 0) == DbusMenu.Root) Rebuild();
                Return(invocation, Tuple(g_variant_new_uint32(_revision), Layout()));
                break;
            case "GetGroupProperties":
                var rows = new List<nint>();
                foreach (var id in IntsAt(parameters, 0))
                    if (id == DbusMenu.Root || DbusMenu.Entry(_entries, id) is not null) rows.Add(Tuple(g_variant_new_int32(id), Properties(id)));
                Return(invocation, Tuple(Array("(ia{sv})", rows)));
                break;
            case "GetProperty":
                var value = Properties(IntAt(parameters, 0), StringAt(parameters, 1));
                Return(invocation, Tuple(g_variant_new_variant(value != 0 ? value : Str(""))));
                break;
            case "Event":
                Event(IntAt(parameters, 0), StringAt(parameters, 1));
                Return(invocation, 0);
                break;
            case "EventGroup":
                var events = g_variant_get_child_value(parameters, 0);
                try
                {
                    for (nuint i = 0, n = g_variant_n_children(events); i < n; i++)
                    {
                        var one = g_variant_get_child_value(events, i);
                        try { Event(IntAt(one, 0), StringAt(one, 1)); }
                        finally { g_variant_unref(one); }
                    }
                }
                finally { g_variant_unref(events); }
                Return(invocation, Tuple(Array("i", [])));
                break;
            case "AboutToShow":
                Return(invocation, Tuple(g_variant_new_boolean(1)));   // lay it out again: the app's items may have changed
                break;
            case "AboutToShowGroup":
                Return(invocation, Tuple(Array("i", [g_variant_new_int32(DbusMenu.Root)]), Array("i", [])));
                break;
            default:
                Return(invocation, 0);
                break;
        }
    }

    private void Rebuild()
    {
        _entries = _tray.Menu();
        _revision++;
    }

    private void Event(int id, string eventId)
    {
        if (eventId == "clicked" && DbusMenu.Entry(_entries, id) is { } entry) _tray.Choose(entry);
    }

    // (ia{sv}av): the menu, its properties, and each entry as a variant of its own layout.
    private nint Layout()
    {
        var children = new List<nint>();
        for (var id = 1; id <= _entries.Count; id++)
            children.Add(g_variant_new_variant(Tuple(g_variant_new_int32(id), Properties(id), Array("v", []))));
        return Tuple(g_variant_new_int32(DbusMenu.Root), Properties(DbusMenu.Root), Array("v", children));
    }

    private nint Properties(int id) =>
        Dictionary(id == DbusMenu.Root ? DbusMenu.RootProperties : DbusMenu.Entry(_entries, id) is { } entry ? DbusMenu.Properties(entry) : []);

    private nint Properties(int id, string name)
    {
        var source = id == DbusMenu.Root ? DbusMenu.RootProperties : DbusMenu.Entry(_entries, id) is { } entry ? DbusMenu.Properties(entry) : [];
        foreach (var (key, value) in source)
            if (key == name) return Value(value);
        return 0;
    }

    // ── the watcher ────────────────────────────────────────────────────────────────────────────────────────────────

    private void Answered(int operation, int generation, nint reply, string? error)
    {
        if (_disposed) return;
        if (operation == RequestName)
        {
            // Only a name this process OWNS is its item: with another process's (a PID repeated across namespaces,
            // as in a Flatpak) a panel would show that app's icon, and send it the clicks.
            var code = error is null && reply != 0 ? UIntAt(reply, 0) : 0;
            if (code == 1 /* PRIMARY_OWNER */)
            {
                _ownsName = true;
                RegisterWithWatcher();
            }
            else if (code == 3 /* EXISTS */ && _nameTries < 8) RequestOwnName();
            else AppCallback.Log(_log, () => $"[Shenora.Chromium] The tray icon has no name of its own on the bus: {error ?? $"reply {code}"}", LogLevel.Warning);
            return;
        }
        // An answer from a watcher that has since gone says nothing about the one there now.
        if (generation != _generation) return;
        if (operation == Register)
        {
            if (error is null)
            {
                SetState(registered: true, _hostPresent);
                CheckHost();
            }
            else
            {
                SetState(registered: false, hostPresent: false);
                AppCallback.Log(_log, () => "[Shenora.Chromium] No tray on this desktop (no StatusNotifierWatcher); the icon appears if one starts", LogLevel.Information);
            }
        }
        else if (operation == HostCheck)
        {
            SetState(_registered, hostPresent: error is null && reply != 0 && BooleanInVariantAt(reply, 0));
            if (!_hostPresent) AppCallback.Log(_log, () => "[Shenora.Chromium] The desktop's tray has no host to show the icon yet", LogLevel.Information);
        }
    }

    private void RequestOwnName()
    {
        _name = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-{++_nameTries}";
        Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName",
            Tuple(Str(_name), g_variant_new_uint32(4 /* DO_NOT_QUEUE */)), RequestName);
    }

    private void RegisterWithWatcher() =>
        Call(Watcher, "/StatusNotifierWatcher", Watcher, "RegisterStatusNotifierItem", Tuple(Str(_name)), Register);

    private void CheckHost() =>
        Call(Watcher, "/StatusNotifierWatcher", "org.freedesktop.DBus.Properties", "Get",
            Tuple(Str(Watcher), Str("IsStatusNotifierHostRegistered")), HostCheck);

    private void Signal(string sender, string name, nint parameters)
    {
        if (_disposed) return;
        if (name == "NameOwnerChanged")
        {
            // (sss): the name, its old owner, its new one. Whichever it is, the item is registered with no one now; a
            // new watcher is registered with once this process owns its name.
            _generation++;
            SetState(registered: false, hostPresent: false);
            if (StringAt(parameters, 2).Length > 0 && _ownsName) RegisterWithWatcher();
        }
        else if (_registered && name is "StatusNotifierHostRegistered" or "StatusNotifierHostUnregistered")
        {
            CheckHost();
        }
    }

    // ── GLib callbacks, on the UI thread ───────────────────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MethodCall(nint connection, byte* sender, byte* path, byte* iface, byte* method, nint parameters, nint invocation, nint user)
    {
        var tray = _current;
        var name = Utf8(method);
        var inMenu = Utf8(iface) == MenuInterface;
        var answered = false;
        AppCallback.Run(() =>
        {
            if (tray is null) return;
            if (inMenu) tray.MenuMethod(name, parameters, invocation);
            else tray.ItemMethod(name, invocation);
            answered = true;
        }, ex => AppCallback.Log(tray?._log, () => $"[Shenora.Chromium] The tray's '{name}' failed", LogLevel.Error, ex));
        if (!answered) g_dbus_method_invocation_return_dbus_error(invocation, U8("org.freedesktop.DBus.Error.Failed"), U8("The tray could not answer."));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint GetProperty(nint connection, byte* sender, byte* path, byte* iface, byte* property, nint* error, nint user)
    {
        var tray = _current;
        var name = Utf8(property);
        nint value = 0;
        AppCallback.Run(() => value = Utf8(iface) == MenuInterface ? MenuProperty(name) : tray?.ItemProperty(name) ?? 0);
        // No value without an error aborts the process: GLib asserts that a getter answering nothing said why.
        if (value == 0) g_set_error_literal(error, g_dbus_error_quark(), 0 /* G_DBUS_ERROR_FAILED */, U8($"The tray could not answer {name}."));
        return value;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SignalArrived(nint connection, byte* sender, byte* path, byte* iface, byte* signal, nint parameters, nint user)
    {
        var tray = _current;
        var from = Utf8(sender);
        var name = Utf8(signal);
        AppCallback.Run(() => tray?.Signal(from, name, parameters));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CallFinished(nint source, nint result, nint tag)
    {
        var tray = _current;
        nint error = 0;
        var reply = g_dbus_connection_call_finish(source, result, &error);
        var failure = reply == 0 ? TakeError(error) : null;
        try { AppCallback.Run(() => tray?.Answered((int)(tag & 0xFF), (int)(tag >> 8), reply, failure)); }
        finally { if (reply != 0) g_variant_unref(reply); }
    }

    // ── GIO and GVariant ───────────────────────────────────────────────────────────────────────────────────────────

    private nint Node(string xml)
    {
        nint error = 0;
        var node = g_dbus_node_info_new_for_xml(U8(xml), &error);
        if (node == 0) throw new InvalidOperationException($"The tray's D-Bus interface did not parse: {TakeError(error)}");
        return node;
    }

    private uint RegisterObject(string path, nint node, string iface)
    {
        nint error = 0;
        var id = g_dbus_connection_register_object(_connection, U8(path), g_dbus_node_info_lookup_interface(node, U8(iface)), _vtable, 0, 0, &error);
        if (id == 0) throw new InvalidOperationException($"The tray could not export {iface}: {TakeError(error)}");
        return id;
    }

    private uint Subscribe(string? sender, string iface, string? member, string? path, string? arg0)
    {
        byte[]? s = sender is null ? null : U8(sender), m = member is null ? null : U8(member), p = path is null ? null : U8(path), a = arg0 is null ? null : U8(arg0);
        fixed (byte* sp = s) fixed (byte* mp = m) fixed (byte* pp = p) fixed (byte* ap = a)
            return g_dbus_connection_signal_subscribe(_connection, sp, U8(iface), mp, pp, ap, 0,
                &SignalArrived, 0, 0);
    }

    // The answer carries which call it was and which watcher it was made to.
    private void Call(string bus, string path, string iface, string method, nint parameters, int operation) =>
        g_dbus_connection_call(_connection, U8(bus), U8(path), U8(iface), U8(method), parameters, 0, 0, 2000, 0, &CallFinished,
            operation | ((nint)_generation << 8));

    private static void Return(nint invocation, nint value) => g_dbus_method_invocation_return_value(invocation, value);

    private static nint Str(string value) => g_variant_new_string(U8(value));

    private static nint ObjectPath(string value) => g_variant_new_object_path(U8(value));

    private static nint Tuple(params nint[] items)
    {
        fixed (nint* p = items) return g_variant_new_tuple(p, (nuint)items.Length);
    }

    private static nint Array(string elementType, List<nint> items)
    {
        var buffer = items.ToArray();
        fixed (nint* p = buffer) return g_variant_new_array(U8(elementType), p, (nuint)buffer.Length);
    }

    private static nint Dictionary(IReadOnlyList<KeyValuePair<string, object>> entries) =>
        Array("{sv}", [.. entries.Select(e => g_variant_new_dict_entry(Str(e.Key), g_variant_new_variant(Value(e.Value))))]);

    private static nint Value(object value) => value switch
    {
        string s => Str(s),
        bool b => g_variant_new_boolean(b ? 1 : 0),
        int i => g_variant_new_int32(i),
        _ => throw new ArgumentException($"A dbusmenu property cannot be a {value.GetType().Name}."),
    };

    // A child of a value that came over the bus is a new reference of its own: read it, then let it go.
    private static int IntAt(nint parent, nuint index)
    {
        var child = g_variant_get_child_value(parent, index);
        try { return g_variant_get_int32(child); }
        finally { g_variant_unref(child); }
    }

    private static string StringAt(nint parent, nuint index)
    {
        var child = g_variant_get_child_value(parent, index);
        try { return Utf8(g_variant_get_string(child, null)); }
        finally { g_variant_unref(child); }
    }

    private static List<int> IntsAt(nint parent, nuint index)
    {
        var array = g_variant_get_child_value(parent, index);
        try
        {
            var values = new List<int>();
            for (nuint i = 0, n = g_variant_n_children(array); i < n; i++) values.Add(IntAt(array, i));
            return values;
        }
        finally { g_variant_unref(array); }
    }

    private static uint UIntAt(nint parent, nuint index)
    {
        var child = g_variant_get_child_value(parent, index);
        try { return g_variant_get_uint32(child); }
        finally { g_variant_unref(child); }
    }

    // (v) holding a boolean, as a property's Get answers.
    private static bool BooleanInVariantAt(nint parent, nuint index)
    {
        var boxed = g_variant_get_child_value(parent, index);
        var inner = g_variant_get_variant(boxed);
        try { return g_variant_get_boolean(inner) != 0; }
        finally { g_variant_unref(inner); g_variant_unref(boxed); }
    }

    private static string? TakeError(nint error)
    {
        if (error == 0) return null;
        var message = Utf8(*(byte**)(error + 8));   // GError: domain, code, message
        g_error_free(error);
        return message;
    }

    private static byte[] U8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static string Utf8(byte* text) => text == null ? "" : Marshal.PtrToStringUTF8((nint)text) ?? "";

    [DllImport(Gio)] private static extern nint g_dbus_address_get_for_bus_sync(int type, nint cancellable, nint* error);
    [DllImport(Gio)] private static extern nint g_dbus_connection_new_for_address_sync(nint address, int flags, nint observer, nint cancellable, nint* error);
    [DllImport(Gio)] private static extern int g_dbus_connection_close_sync(nint connection, nint cancellable, nint* error);
    [DllImport(Gio)] private static extern uint g_dbus_error_quark();
    [DllImport(Gio)] private static extern nint g_dbus_node_info_new_for_xml(byte[] xml, nint* error);
    [DllImport(Gio)] private static extern nint g_dbus_node_info_lookup_interface(nint node, byte[] name);
    [DllImport(Gio)] private static extern void g_dbus_node_info_unref(nint node);
    [DllImport(Gio)] private static extern uint g_dbus_connection_register_object(nint connection, byte[] path, nint iface, nint vtable, nint user, nint free, nint* error);
    [DllImport(Gio)] private static extern int g_dbus_connection_unregister_object(nint connection, uint id);
    [DllImport(Gio)] private static extern uint g_dbus_connection_signal_subscribe(nint connection, byte* sender, byte[] iface, byte* member, byte* path, byte* arg0, int flags,
        delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte*, byte*, nint, nint, void> callback, nint user, nint free);
    [DllImport(Gio)] private static extern void g_dbus_connection_signal_unsubscribe(nint connection, uint id);
    [DllImport(Gio)] private static extern void g_dbus_connection_call(nint connection, byte[] bus, byte[] path, byte[] iface, byte[] method, nint parameters, nint replyType, int flags, int timeout, nint cancellable,
        delegate* unmanaged[Cdecl]<nint, nint, nint, void> callback, nint user);
    [DllImport(Gio)] private static extern nint g_dbus_connection_call_finish(nint connection, nint result, nint* error);
    [DllImport(Gio)] private static extern void g_dbus_method_invocation_return_value(nint invocation, nint value);
    [DllImport(Gio)] private static extern void g_dbus_method_invocation_return_dbus_error(nint invocation, byte[] name, byte[] message);
    [DllImport(GObject)] private static extern void g_object_unref(nint instance);
    [DllImport(GLib)] private static extern void g_error_free(nint error);
    [DllImport(GLib)] private static extern void g_set_error_literal(nint* error, uint domain, int code, byte[] message);
    [DllImport(GLib)] private static extern void g_free(nint memory);
    [DllImport(GLib)] private static extern uint g_variant_get_uint32(nint value);
    [DllImport(GLib)] private static extern nint g_variant_new_string(byte[] value);
    [DllImport(GLib)] private static extern nint g_variant_new_object_path(byte[] value);
    [DllImport(GLib)] private static extern nint g_variant_new_int32(int value);
    [DllImport(GLib)] private static extern nint g_variant_new_uint32(uint value);
    [DllImport(GLib)] private static extern nint g_variant_new_boolean(int value);
    [DllImport(GLib)] private static extern nint g_variant_new_variant(nint value);
    [DllImport(GLib)] private static extern nint g_variant_new_tuple(nint* children, nuint count);
    [DllImport(GLib)] private static extern nint g_variant_new_array(byte[] childType, nint* children, nuint count);
    [DllImport(GLib)] private static extern nint g_variant_new_dict_entry(nint key, nint value);
    [DllImport(GLib)] private static extern nint g_variant_get_child_value(nint value, nuint index);
    [DllImport(GLib)] private static extern nuint g_variant_n_children(nint value);
    [DllImport(GLib)] private static extern int g_variant_get_int32(nint value);
    [DllImport(GLib)] private static extern int g_variant_get_boolean(nint value);
    [DllImport(GLib)] private static extern byte* g_variant_get_string(nint value, nuint* length);
    [DllImport(GLib)] private static extern nint g_variant_get_variant(nint value);
    [DllImport(GLib)] private static extern void g_variant_unref(nint value);
}
#endif
