#if CEF_MACOS
using System.Reflection;
using System.Runtime.InteropServices;

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
        // The framework registered these when it loaded; a class conforms to what it is TOLD it conforms to.
        foreach (var name in new[] { "CrAppProtocol", "CrAppControlProtocol", "CefAppProtocol" })
            if (objc_getProtocol(name) is var protocol and not 0) class_addProtocol(cls, protocol);
        objc_registerClassPair(cls);

        var app = objc_msgSend(cls, sel_registerName("sharedApplication"));
        if (object_getClass(app) != cls)
            throw new InvalidOperationException("NSApp already existed before the Chromium shell could create its own.");
        // Regular: a Dock icon and a menu bar, as an app started from a bundle has.
        objc_msgSend_long(app, sel_registerName("setActivationPolicy:"), 0);
    }

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
    [DllImport(ObjC)] private static extern void objc_msgSendSuper(ObjcSuper* super, nint selector, nint argument);
}
#endif
