#if CEF_MACOS
using System.Runtime.InteropServices;
using System.Text;

namespace Shenora.Chromium.Host;

/// <summary>The Objective-C runtime, CoreFoundation, CoreGraphics, CoreText and ImageIO calls the macOS splash makes.</summary>
internal static unsafe class MacSplashNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CT = "/System/Library/Frameworks/CoreText.framework/CoreText";
    private const string IIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const uint Utf8 = 0x08000100;

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CGSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    public struct CGRect
    {
        public double X, Y, Width, Height;
        public CGRect(double x, double y, double width, double height) { X = x; Y = y; Width = width; Height = height; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct CFRange { public nint Location, Length; }
    [StructLayout(LayoutKind.Sequential)] public struct CTParagraphStyleSetting { public uint Spec; public nuint ValueSize; public void* Value; }

    private static readonly nint CoreText = NativeLibrary.Load(CT);
    private static readonly nint CoreFoundation = NativeLibrary.Load(CF);

    /// <summary>AppKit and QuartzCore loaded, so their classes resolve.</summary>
    public static void LoadFrameworks()
    {
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        NativeLibrary.Load("/System/Library/Frameworks/QuartzCore.framework/QuartzCore");
    }

    public static nint Class(string name) => objc_getClass(name);
    public static nint Sel(string name) => sel_registerName(name);
    public static nint Send(nint receiver, string selector) => objc_msgSend(receiver, Sel(selector));
    public static nint Send(nint receiver, string selector, nint argument) => objc_msgSend(receiver, Sel(selector), argument);
    public static void SendBool(nint receiver, string selector, bool value) => objc_msgSend_bool(receiver, Sel(selector), value ? (byte)1 : (byte)0);
    public static bool GetBool(nint receiver, string selector) => objc_msgSend_bool_ret(receiver, Sel(selector)) != 0;
    public static void SendDouble(nint receiver, string selector, double value) => objc_msgSend_double(receiver, Sel(selector), value);
    public static double GetDouble(nint receiver, string selector) => objc_msgSend_double_ret(receiver, Sel(selector));
    /// <summary>A method taking a 4-byte <c>float</c> (CALayer's <c>opacity</c>), which a double would not reach.</summary>
    public static void SendFloat(nint receiver, string selector, float value) => objc_msgSend_float(receiver, Sel(selector), value);
    public static bool GetBool(nint receiver, string selector, nint argument) => objc_msgSend_bool_arg(receiver, Sel(selector), argument) != 0;
    public static nint At(nint array, long index) => objc_msgSend_index(array, Sel("objectAtIndex:"), index);
    public static void SendLong(nint receiver, string selector, long value) => objc_msgSend_long(receiver, Sel(selector), value);
    public static long GetLong(nint receiver, string selector) => objc_msgSend_long_ret(receiver, Sel(selector));

    /// <summary>A method returning an NSRect: through <c>objc_msgSend_stret</c> on x86-64, where a 32-byte struct comes back
    /// through a hidden pointer, and plain <c>objc_msgSend</c> on arm64.</summary>
    public static CGRect GetRect(nint receiver, string selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret(out var rect, receiver, Sel(selector));
            return rect;
        }
        return objc_msgSend_rect_ret(receiver, Sel(selector));
    }

    /// <summary>A method taking one NSRect and returning one (<c>contentRectForFrameRect:</c>), with the same x86-64 rule
    /// as <see cref="GetRect(nint, string)"/>.</summary>
    public static CGRect GetRect(nint receiver, string selector, CGRect argument)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret_rect(out var rect, receiver, Sel(selector), argument);
            return rect;
        }
        return objc_msgSend_rect_rect(receiver, Sel(selector), argument);
    }

    public static void SetFrame(nint window, CGRect frame) => objc_msgSend_frame(window, Sel("setFrame:display:"), frame, 1);

    /// <summary>A method taking one CGRect by value (CALayer's <c>setFrame:</c>).</summary>
    public static void SendRect(nint receiver, string selector, CGRect rect) => objc_msgSend_rect_arg(receiver, Sel(selector), rect);

    public static nint InitWindow(nint window, CGRect frame) =>
        objc_msgSend_initWindow(window, Sel("initWithContentRect:styleMask:backing:defer:"), frame, 0 /* borderless */, 2 /* buffered */, 0);

    public static void AddChild(nint parent, nint child) => objc_msgSend_long2(parent, Sel("addChildWindow:ordered:"), child, 1 /* above */);

    public static void PerformOnMain(nint receiver, string selector) =>
        objc_msgSend_perform(receiver, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"), Sel(selector), 0, 0);

    public static bool IsMainThread => objc_msgSend_bool_ret(Class("NSThread"), Sel("isMainThread")) != 0;

    /// <summary>A CFString (create rule: the caller releases it).</summary>
    public static nint CFString(string value)
    {
        fixed (char* chars = value) return CFStringCreateWithCharacters(0, chars, value.Length);
    }

    public static nint Constant(nint library, string name) => *(nint*)NativeLibrary.GetExport(library, name);

    public static nint CoreTextConstant(string name) => Constant(CoreText, name);

    public static nint KeyCallbacks => NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");

    public static nint ValueCallbacks => NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");

    public static nint FileUrl(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        fixed (byte* p = bytes) return CFURLCreateFromFileSystemRepresentation(0, p, bytes.Length, 0);
    }

    public static nint Data(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes) return CFDataCreate(0, p, bytes.Length);
    }

    [DllImport(ObjC)] public static extern nint objc_getClass(string name);
    [DllImport(ObjC)] public static extern nint sel_registerName(string name);
    [DllImport(ObjC)] public static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] public static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_bool(nint receiver, nint selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte objc_msgSend_bool_ret(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_double(nint receiver, nint selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double objc_msgSend_double_ret(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_float(nint receiver, nint selector, float value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte objc_msgSend_bool_arg(nint receiver, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_index(nint receiver, nint selector, long index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_long(nint receiver, nint selector, long value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long objc_msgSend_long_ret(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_long2(nint receiver, nint selector, nint first, long second);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect objc_msgSend_rect_ret(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void objc_msgSend_stret(out CGRect result, nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void objc_msgSend_stret_rect(out CGRect result, nint receiver, nint selector, CGRect argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect objc_msgSend_rect_rect(nint receiver, nint selector, CGRect argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_frame(nint receiver, nint selector, CGRect frame, byte display);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_rect_arg(nint receiver, nint selector, CGRect rect);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_initWindow(nint receiver, nint selector, CGRect frame, ulong style, ulong backing, byte defer);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void objc_msgSend_perform(nint receiver, nint selector, nint action, nint argument, byte wait);

    [DllImport(CF)] public static extern void CFRelease(nint value);
    [DllImport(CF)] private static extern nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);
    [DllImport(CF)] public static extern nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallbacks, nint valueCallbacks);
    [DllImport(CF)] public static extern nint CFAttributedStringCreate(nint allocator, nint text, nint attributes);
    [DllImport(CF)] private static extern nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [DllImport(CF)] private static extern nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte* path, nint length, byte isDirectory);

    [DllImport(CG)] public static extern nint CGColorSpaceCreateDeviceRGB();
    [DllImport(CG)] public static extern void CGColorSpaceRelease(nint space);
    [DllImport(CG)] public static extern nint CGBitmapContextCreate(nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint info);
    [DllImport(CG)] public static extern nint CGBitmapContextCreateImage(nint context);
    [DllImport(CG)] public static extern void CGContextRelease(nint context);
    [DllImport(CG)] public static extern void CGImageRelease(nint image);
    [DllImport(CG)] public static extern void CGContextClearRect(nint context, CGRect rect);
    [DllImport(CG)] public static extern void CGContextSetRGBFillColor(nint context, double r, double g, double b, double a);
    [DllImport(CG)] public static extern void CGContextFillRect(nint context, CGRect rect);
    [DllImport(CG)] public static extern void CGContextSetInterpolationQuality(nint context, int quality);
    [DllImport(CG)] public static extern void CGContextDrawImage(nint context, CGRect rect, nint image);
    [DllImport(CG)] public static extern nint CGColorCreateSRGB(double r, double g, double b, double a);
    [DllImport(CG)] public static extern void CGColorRelease(nint color);
    [DllImport(CG)] public static extern nint CGPathCreateWithRect(CGRect rect, nint transform);
    [DllImport(CG)] public static extern void CGPathRelease(nint path);
    [DllImport(CG)] public static extern void CGContextSetTextMatrix(nint context, CGAffineTransform matrix);

    [StructLayout(LayoutKind.Sequential)] public struct CGAffineTransform { public double A, B, C, D, Tx, Ty; }

    [DllImport(CT)] public static extern nint CTFontCreateUIFontForLanguage(uint type, double size, nint language);
    [DllImport(CT)] public static extern nint CTFontCreateWithName(nint name, double size, nint matrix);
    [DllImport(CT)] public static extern nint CTFontCreateCopyWithSymbolicTraits(nint font, double size, nint matrix, uint value, uint mask);
    [DllImport(CT)] public static extern nint CTParagraphStyleCreate(CTParagraphStyleSetting* settings, nuint count);
    [DllImport(CT)] public static extern nint CTFramesetterCreateWithAttributedString(nint text);
    [DllImport(CT)] public static extern CGSize CTFramesetterSuggestFrameSizeWithConstraints(nint framesetter, CFRange range, nint attributes, CGSize constraints, CFRange* fit);
    [DllImport(CT)] public static extern nint CTFramesetterCreateFrame(nint framesetter, CFRange range, nint path, nint attributes);
    [DllImport(CT)] public static extern void CTFrameDraw(nint frame, nint context);

    [DllImport(IIO)] public static extern nint CGImageSourceCreateWithURL(nint url, nint options);
    [DllImport(IIO)] public static extern nint CGImageSourceCreateWithData(nint data, nint options);
    [DllImport(IIO)] public static extern nint CGImageSourceCreateImageAtIndex(nint source, nuint index, nint options);
}
#endif
