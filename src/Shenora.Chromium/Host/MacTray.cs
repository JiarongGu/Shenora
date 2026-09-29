#if CEF_MACOS
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Shenora.Chromium.Host;

/// <summary>
/// A status item in the menu bar: an <c>NSStatusItem</c> whose <c>NSMenu</c> is rebuilt from the tray's model each time
/// it opens (<c>menuNeedsUpdate:</c>), answered by a target object of a class registered at runtime. On the main
/// thread, which is CEF's UI thread on macOS.
/// </summary>
internal sealed unsafe class MacTray : NativeTray
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // One tray per process: the target's methods find it here.
    private static MacTray? _current;
    private static nint _targetClass;

    private readonly ChromiumTray _tray;
    private readonly ILogger? _log;
    private readonly nint _statusItem;
    private readonly nint _menu;
    private readonly nint _target;
    private IReadOnlyList<TrayMenuEntry> _entries = [];

    public MacTray(ChromiumTray tray, ILogger? log)
    {
        _tray = tray;
        _log = log;
        _current = this;
        var pool = objc_autoreleasePoolPush();
        try
        {
            _target = Send(Send(TargetClass(), "alloc"), "init");
            _statusItem = objc_msgSend_double(Send(Class("NSStatusBar"), "systemStatusBar"), Sel("statusItemWithLength:"), -1.0);   // NSVariableStatusItemLength
            Send(_statusItem, "retain");
            var button = Send(_statusItem, "button");
            if (Image(tray.IconPath) is var image and not 0)
            {
                objc_msgSend_size(image, Sel("setSize:"), 18, 18);   // the menu bar's own size, in points
                objc_msgSend(button, Sel("setImage:"), image);
                Send(image, "release");
            }
            else
            {
                objc_msgSend(button, Sel("setTitle:"), NSString(tray.Text));
            }
            objc_msgSend(button, Sel("setToolTip:"), NSString(tray.Text));
            _menu = objc_msgSend(Send(Class("NSMenu"), "alloc"), Sel("initWithTitle:"), NSString(""));
            objc_msgSend_bool(_menu, Sel("setAutoenablesItems:"), 0);
            objc_msgSend(_menu, Sel("setDelegate:"), _target);
            objc_msgSend(_statusItem, Sel("setMenu:"), _menu);
            Rebuild(_menu);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public override void Dispose()
    {
        if (_current != this) return;
        _current = null;
        objc_msgSend(Send(Class("NSStatusBar"), "systemStatusBar"), Sel("removeStatusItem:"), _statusItem);
        Send(_statusItem, "release");
        Send(_menu, "release");
        Send(_target, "release");
    }

    /// <summary>The menu, from the model, with each item's index as its tag.</summary>
    private void Rebuild(nint menu)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            Send(menu, "removeAllItems");
            _entries = _tray.Menu();
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.IsSeparator)
                {
                    objc_msgSend(menu, Sel("addItem:"), Send(Class("NSMenuItem"), "separatorItem"));
                    continue;
                }
                var item = objc_msgSend(Send(Class("NSMenuItem"), "alloc"), Sel("initWithTitle:action:keyEquivalent:"),
                    NSString(entry.Text), Sel("shenoraTrayItem:"), NSString(""));
                objc_msgSend(item, Sel("setTarget:"), _target);
                objc_msgSend_long(item, Sel("setTag:"), i);
                objc_msgSend_bool(item, Sel("setEnabled:"), entry.Enabled ? (byte)1 : (byte)0);
                objc_msgSend_long(item, Sel("setState:"), entry.Checked ? 1 : 0);
                objc_msgSend(menu, Sel("addItem:"), item);
                Send(item, "release");
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The file's image, else a copy of the app's own icon (never the shared one, which the Dock draws), each
    /// with a reference the caller releases; 0 when there is neither.</summary>
    private nint Image(string? path)
    {
        if (path is not null)
        {
            var loaded = objc_msgSend(Send(Class("NSImage"), "alloc"), Sel("initWithContentsOfFile:"), NSString(path));
            if (loaded != 0) return loaded;
            AppCallback.Log(_log, () => $"[Shenora.Chromium] The tray icon {path} could not be loaded; using the app's own", LogLevel.Warning);
        }
        var app = Send(Send(Class("NSApplication"), "sharedApplication"), "applicationIconImage");
        return app == 0 ? 0 : Send(app, "copy");
    }

    private static nint TargetClass()
    {
        if (_targetClass != 0) return _targetClass;
        var cls = objc_allocateClassPair(Class("NSObject"), "ShenoraTrayTarget", 0);
        if (cls == 0) throw new InvalidOperationException("Could not register the tray's menu target class.");
        class_addMethod(cls, Sel("shenoraTrayItem:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&ItemChosen, "v@:@");
        class_addMethod(cls, Sel("menuNeedsUpdate:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&MenuNeedsUpdate, "v@:@");
        if (objc_getProtocol("NSMenuDelegate") is var protocol and not 0) class_addProtocol(cls, protocol);
        objc_registerClassPair(cls);
        return _targetClass = cls;
    }

    [UnmanagedCallersOnly]
    private static void ItemChosen(nint self, nint selector, nint sender)
    {
        if (_current is not { } tray) return;
        var index = (int)objc_msgSend_long_ret(sender, Sel("tag"));
        AppCallback.Run(() =>
        {
            if (index >= 0 && index < tray._entries.Count) tray._tray.Choose(tray._entries[index]);
        });
    }

    [UnmanagedCallersOnly]
    private static void MenuNeedsUpdate(nint self, nint selector, nint menu)
    {
        if (_current is not { } tray) return;
        AppCallback.Run(() => tray.Rebuild(menu), ex => AppCallback.Log(tray._log, () => "[Shenora.Chromium] The tray's menu could not be built", LogLevel.Error, ex));
    }

    /// <summary>An autoreleased NSString.</summary>
    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return objc_msgSend(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static nint Send(nint receiver, string selector) => objc_msgSend(receiver, Sel(selector));
    private static nint Class(string name) => objc_getClass(name);
    private static nint Sel(string name) => sel_registerName(name);

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(string name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint selector, nint implementation, string types);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector, nint argument);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector, nint first, nint second, nint third);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_double(nint receiver, nint selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_size(nint receiver, nint selector, double width, double height);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_bool(nint receiver, nint selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_long(nint receiver, nint selector, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long objc_msgSend_long_ret(nint receiver, nint selector);
}
#endif
