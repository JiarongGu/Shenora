#if CEF_MACOS
using System.Reflection;
using System.Runtime.InteropServices;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// What CEF needs of a macOS process before its first call: where its framework is, and an <c>NSApplication</c> that
/// speaks Chromium's event protocol.
/// <list type="bullet">
/// <item>The binding imports <c>libcef</c>; on macOS that is the framework's binary, in the app bundle the build laid
/// out (<c>Contents/MacOS/&lt;App&gt;</c> beside <c>Contents/Frameworks</c>).</item>
/// <item>🔴 CEF requires <c>NSApp</c> to implement <c>CefAppProtocol</c> (<c>isHandlingSendEvent</c> and
/// <c>setHandlingSendEvent:</c>), and <c>NSApp</c> is whichever class calls <c>sharedApplication</c> first. So the
/// subclass is registered and made <c>NSApp</c> here, after the framework has registered the protocols and before
/// <c>cef_initialize</c> would create a plain one.</item>
/// </list>
/// </summary>
internal static unsafe class MacPlatform
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string FrameworkName = "Chromium Embedded Framework";

    /// <summary>The app bundle's <c>Contents</c>: the running executable is <c>Contents/MacOS/&lt;App&gt;</c>.</summary>
    public static string Contents => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    /// <summary>The CEF framework the build laid out in the bundle.</summary>
    public static string Framework => Path.Combine(Contents, "Frameworks", FrameworkName + ".framework");

    /// <summary>The helper every CEF subprocess runs: <c>&lt;App&gt; Helper.app</c>, whose four variants CEF finds beside it.</summary>
    public static string Helper
    {
        get
        {
            var app = Path.GetFileName(Environment.ProcessPath) ?? "App";
            return Path.Combine(Contents, "Frameworks", $"{app} Helper.app", "Contents", "MacOS", $"{app} Helper");
        }
    }

    private static int _prepared;
    private static nint _superclass;
    private static byte _handlingSendEvent;

    /// <summary>Once per process, before CEF's first call and on the main thread.</summary>
    public static void Prepare()
    {
        if (Interlocked.Exchange(ref _prepared, 1) == 1) return;
        var binary = Path.Combine(Framework, FrameworkName);
        if (!File.Exists(binary))
            throw new DllNotFoundException($"CEF's framework is not in the app bundle at {Framework}.");
        var framework = NativeLibrary.Load(binary);
        NativeLibrary.SetDllImportResolver(typeof(MacPlatform).Assembly,
            (name, _, _) => name == "libcef" ? framework : 0);
        CreateApplication();
    }

    private static void CreateApplication()
    {
        _superclass = objc_getClass("NSApplication");
        var cls = objc_allocateClassPair(_superclass, "ShenoraCefApplication", 0);
        if (cls == 0) throw new InvalidOperationException("Could not register the Chromium shell's NSApplication subclass.");

        // BOOL is a signed char on x86_64 and a C bool on arm64, and the type encoding says which.
        var boolean = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
        class_addMethod(cls, sel_registerName("isHandlingSendEvent"), (nint)(delegate* unmanaged<nint, nint, byte>)&IsHandlingSendEvent, $"{boolean}@:");
        class_addMethod(cls, sel_registerName("setHandlingSendEvent:"), (nint)(delegate* unmanaged<nint, nint, byte, void>)&SetHandlingSendEvent, $"v@:{boolean}");
        class_addMethod(cls, sel_registerName("sendEvent:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&SendEvent, "v@:@");
        class_addMethod(cls, sel_registerName(WillFinishLaunchingName), (nint)(delegate* unmanaged<nint, nint, nint, void>)&WillFinishLaunching, "v@:@");
        class_addMethod(cls, sel_registerName(OpenDocumentsName), (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&OpenDocuments, "v@:@@");
        class_addMethod(cls, sel_registerName(GetUrlName), (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&GetUrl, "v@:@@");
        // The framework registered these when it loaded; a class conforms to what it is TOLD it conforms to.
        foreach (var name in new[] { "CrAppProtocol", "CrAppControlProtocol", "CefAppProtocol" })
            if (objc_getProtocol(name) is var protocol and not 0) class_addProtocol(cls, protocol);
        objc_registerClassPair(cls);

        var app = objc_msgSend(cls, sel_registerName("sharedApplication"));
        if (object_getClass(app) != cls)
            throw new InvalidOperationException("NSApp already existed before the Chromium shell could create its own.");
        // Regular: a Dock icon and a menu bar, as an app started from a bundle has.
        objc_msgSend_long(app, sel_registerName("setActivationPolicy:"), 0);
        // The Apple Events that open files and links are taken as NSApp finishes launching, the place Apple gives for
        // it: the first launch's own document is dispatched after that, and NSApplication's handlers are in by then.
        var center = objc_msgSend(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        objc_msgSend_observe(center, sel_registerName("addObserver:selector:name:object:"), app, sel_registerName(WillFinishLaunchingName),
            NSString("NSApplicationWillFinishLaunchingNotification"), 0);
    }

    // Apple Event codes (FourCharCode): the core suite's open-documents, and the internet suite's get-URL.
    private const uint CoreEventClass = 0x61657674;   // 'aevt'
    private const uint OpenDocumentsId = 0x6F646F63;  // 'odoc'
    private const uint InternetEventClass = 0x4755524C, GetUrlId = 0x4755524C;   // 'GURL', 'GURL'
    private const uint DirectObject = 0x2D2D2D2D;     // '----'
    private const uint FileUrl = 0x6675726C;          // 'furl'
    private const string WillFinishLaunchingName = "shenoraWillFinishLaunching:";
    private const string OpenDocumentsName = "shenoraOpenDocuments:withReply:";
    private const string GetUrlName = "shenoraGetURL:withReply:";

    /// <summary>
    /// A file opened with the app (Finder's "open with", a file dropped on its Dock icon) and a link to a URL scheme it
    /// registered reach a running app on macOS as Apple Events, never as a launch with arguments; the first launch's
    /// document too. Each arrives as a launch would, with the paths or the URL as its arguments.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void WillFinishLaunching(nint self, nint selector, nint notification) => AppCallback.Run(() =>
    {
        var events = objc_msgSend(objc_getClass("NSAppleEventManager"), sel_registerName("sharedAppleEventManager"));
        var install = sel_registerName("setEventHandler:andSelector:forEventClass:andEventID:");
        objc_msgSend_handler(events, install, self, sel_registerName(OpenDocumentsName), CoreEventClass, OpenDocumentsId);
        objc_msgSend_handler(events, install, self, sel_registerName(GetUrlName), InternetEventClass, GetUrlId);
    });

    [UnmanagedCallersOnly]
    private static void OpenDocuments(nint self, nint selector, nint appleEvent, nint reply) => AppCallback.Run(() =>
    {
        var list = objc_msgSend_code(appleEvent, sel_registerName("paramDescriptorForKeyword:"), DirectObject);
        if (list == 0) return;
        // A list of the files, each coerced to a file URL; a single file may come as itself rather than a list of one.
        var count = objc_msgSend_count(list, sel_registerName("numberOfItems"));
        var paths = new List<string>();
        for (long i = 1; i <= Math.Max(count, 1); i++)
        {
            var item = count == 0 ? list : objc_msgSend_index(list, sel_registerName("descriptorAtIndex:"), i);
            var url = item == 0 ? 0 : objc_msgSend_code(item, sel_registerName("coerceToDescriptorType:"), FileUrl);
            if (url != 0 && Utf8(objc_msgSend(url, sel_registerName("data"))) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsFile)
                paths.Add(uri.LocalPath);
        }
        if (paths.Count > 0) ChromiumSingleInstance.Process.Relaunched(new SingleInstanceLaunch(paths, Environment.CurrentDirectory));
    });

    [UnmanagedCallersOnly]
    private static void GetUrl(nint self, nint selector, nint appleEvent, nint reply) => AppCallback.Run(() =>
    {
        var direct = objc_msgSend_code(appleEvent, sel_registerName("paramDescriptorForKeyword:"), DirectObject);
        var text = direct == 0 ? 0 : objc_msgSend(direct, sel_registerName("stringValue"));
        if (text != 0 && Marshal.PtrToStringUTF8(objc_msgSend(text, sel_registerName("UTF8String"))) is { Length: > 0 } url)
            ChromiumSingleInstance.Process.Relaunched(new SingleInstanceLaunch([url], Environment.CurrentDirectory));
    });

    // An NSData's bytes as UTF-8, or null.
    private static string? Utf8(nint data)
    {
        if (data == 0) return null;
        var length = objc_msgSend_count(data, sel_registerName("length"));
        return length <= 0 ? null : Marshal.PtrToStringUTF8(objc_msgSend(data, sel_registerName("bytes")), (int)length);
    }

    private static nint NSString(string value) =>
        objc_msgSend_utf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), value);

    [UnmanagedCallersOnly]
    private static byte IsHandlingSendEvent(nint self, nint selector) => _handlingSendEvent;

    [UnmanagedCallersOnly]
    private static void SetHandlingSendEvent(nint self, nint selector, byte value) => _handlingSendEvent = value;

    /// <summary>CefScopedSendingEvent's job: the flag is set while NSApplication dispatches the event.</summary>
    [UnmanagedCallersOnly]
    private static void SendEvent(nint self, nint selector, nint e)
    {
        var was = _handlingSendEvent;
        _handlingSendEvent = 1;
        var super = new ObjcSuper { Receiver = self, SuperClass = _superclass };
        objc_msgSendSuper(&super, selector, e);
        _handlingSendEvent = was;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjcSuper
    {
        public nint Receiver;
        public nint SuperClass;
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint object_getClass(nint obj);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(string name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint selector, nint implementation, string types);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_long(nint receiver, nint selector, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_code(nint receiver, nint selector, uint code);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_index(nint receiver, nint selector, long index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long objc_msgSend_count(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_utf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_observe(nint receiver, nint selector, nint observer, nint action, nint name, nint obj);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_handler(nint receiver, nint selector, nint handler, nint action, uint eventClass, uint eventId);
    [DllImport(ObjC)] private static extern void objc_msgSendSuper(ObjcSuper* super, nint selector, nint argument);
}
#endif
