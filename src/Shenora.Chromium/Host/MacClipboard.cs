#if CEF_MACOS
using System.Runtime.InteropServices;
using System.Text;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The macOS clipboard, <c>NSPasteboard</c>, through the Objective-C runtime.
/// <list type="bullet">
/// <item><see cref="ClipboardContent.Text"/>, <see cref="ClipboardContent.Html"/> and <see cref="ClipboardContent.PngImage"/>
/// are the pasteboard types other apps read (<c>public.utf8-plain-text</c>, <c>public.html</c>, <c>public.png</c>). Any other
/// media type goes under the type the system names for it (<c>UTType typeWithMIMEType:</c>; the pasteboard refuses a raw
/// media type, measured), and reads back as the media type the system names for each type offered, pictures other than
/// PNG excepted, as in the Windows shell.</item>
/// <item>Files are one pasteboard item each, with a <c>public.file-url</c>, as Finder writes them; the other formats ride on
/// the first item, so one <see cref="SetAsync"/> replaces everything at once.</item>
/// </list>
/// </summary>
internal sealed unsafe class MacClipboard : IClipboardService
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string TextType = "public.utf8-plain-text";
    private const string HtmlType = "public.html";
    private const string PngType = "public.png";
    private const string FileUrlType = "public.file-url";

    private readonly string? _name;
    private readonly object _gate = new();

    /// <summary>The system clipboard.</summary>
    public MacClipboard() { }

    /// <summary>A private pasteboard of this name, which no other app reads: how the clipboard is tested without
    /// touching the user's.</summary>
    internal MacClipboard(string pasteboardName) => _name = pasteboardName;

    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SetAsync(new ClipboardContent { Text = text });
    }

    public Task<string?> GetTextAsync() => Task.FromResult(Read().Text);

    public Task ClearAsync()
    {
        Run(() => { Send(Pasteboard(), "clearContents"); return true; });
        return Task.CompletedTask;
    }

    public Task SetAsync(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var files = content.Files.Select(f => { ArgumentException.ThrowIfNullOrWhiteSpace(f, nameof(content)); return Path.GetFullPath(f); }).ToList();
        Run(() =>
        {
            var pasteboard = Pasteboard();
            if (content.IsEmpty) { Send(pasteboard, "clearContents"); return true; }

            // Every item is built BEFORE the pasteboard is cleared, so content refused part-way writes nothing.
            var items = new List<nint> { NewItem() };
            try
            {
                if (content.Text is { } text) SetString(items[0], text, TextType);
                foreach (var (mediaType, bytes) in content.Formats)
                    SetData(items[0], bytes.Span, mediaType switch
                    {
                        ClipboardContent.Html => HtmlType,
                        ClipboardContent.PngImage => PngType,
                        _ => TypeFor(mediaType) ?? throw new NotSupportedException($"macOS names no pasteboard type for {mediaType}."),
                    });
                for (var i = 0; i < files.Count; i++)
                {
                    if (i > 0) items.Add(NewItem());
                    SetString(items[i], new Uri(files[i]).AbsoluteUri, FileUrlType);
                }
                fixed (nint* objects = items.ToArray())
                {
                    var array = objc_msgSend_ptr_count(Class("NSArray"), Sel("arrayWithObjects:count:"), (nint)objects, (nuint)items.Count);
                    Send(pasteboard, "clearContents");
                    if (objc_msgSend_bool(pasteboard, Sel("writeObjects:"), array) == 0)
                        throw new InvalidOperationException("The pasteboard refused the content.");
                }
            }
            finally
            {
                foreach (var item in items) Send(item, "release");
            }
            return true;
        });
        return Task.CompletedTask;
    }

    public Task<ClipboardContent> GetAsync() => Task.FromResult(Read());

    private ClipboardContent Read() => Run(() =>
    {
        string? text = null;
        var files = new List<string>();
        var formats = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var items = Send(Pasteboard(), "pasteboardItems");
        var count = items == 0 ? 0 : objc_msgSend_nuint(items, Sel("count"));
        for (nuint i = 0; i < count; i++)
        {
            var item = objc_msgSend_nuint_arg(items, Sel("objectAtIndex:"), i);
            var types = Send(item, "types");
            var typeCount = objc_msgSend_nuint(types, Sel("count"));
            for (nuint t = 0; t < typeCount; t++)
            {
                var type = ReadString(objc_msgSend_nuint_arg(types, Sel("objectAtIndex:"), t));
                switch (type)
                {
                    case TextType when text is null:
                        text = ReadString(objc_msgSend(item, Sel("stringForType:"), NSString(TextType)));
                        break;
                    case FileUrlType:
                        if (ReadString(objc_msgSend(item, Sel("stringForType:"), NSString(FileUrlType))) is { } url
                            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
                            files.Add(uri.LocalPath);
                        break;
                    case HtmlType:
                        formats.TryAdd(ClipboardContent.Html, ReadData(item, HtmlType));
                        break;
                    case PngType:
                        formats.TryAdd(ClipboardContent.PngImage, ReadData(item, PngType));
                        break;
                    // Anything else the system can name by a media type: an app's own representation, RTF, JSON.
                    case { } other when MediaTypeFor(other) is { } mediaType && Carried(mediaType):
                        formats.TryAdd(mediaType, ReadData(item, other));
                        break;
                }
            }
        }
        return new ClipboardContent { Text = text, Files = files, Formats = formats };
    });

    /// <summary>Let go of a private pasteboard: the pasteboard server keeps a named one until told.</summary>
    internal void ReleasePrivate()
    {
        if (_name is not null) Run(() => { Send(Pasteboard(), "releaseGlobally"); return true; });
    }

    /// <summary>One operation at a time, inside an autorelease pool: the objects AppKit hands back are autoreleased, and
    /// a thread with no pool of its own leaks them.</summary>
    private T Run<T>(Func<T> work)
    {
        lock (_gate)
        {
            var pool = objc_autoreleasePoolPush();
            try { return work(); }
            finally { objc_autoreleasePoolPop(pool); }
        }
    }

    // Pictures come as PNG alone, as in the Windows shell, and text is Text.
    private static bool Carried(string mediaType) =>
        !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

    // UTType is in its own framework, which nothing may have loaded yet.
    private static readonly Lazy<nint> UTType = new(() =>
    {
        NativeLibrary.Load("/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers");
        return objc_getClass("UTType");
    });

    /// <summary>The pasteboard type the system names for a media type: declared (<c>public.json</c>) or dynamic.</summary>
    private static string? TypeFor(string mediaType)
    {
        var type = objc_msgSend(UTType.Value, Sel("typeWithMIMEType:"), NSString(mediaType));
        return type == 0 ? null : ReadString(Send(type, "identifier"));
    }

    /// <summary>The media type the system names for a pasteboard type, or null.</summary>
    private static string? MediaTypeFor(string pasteboardType)
    {
        var type = objc_msgSend(UTType.Value, Sel("typeWithIdentifier:"), NSString(pasteboardType));
        return type == 0 ? null : ReadString(Send(type, "preferredMIMEType"));
    }

    private nint Pasteboard() => _name is null
        ? Send(Class("NSPasteboard"), "generalPasteboard")
        : objc_msgSend(Class("NSPasteboard"), Sel("pasteboardWithName:"), NSString(_name));

    private static nint NewItem() => Send(Send(Class("NSPasteboardItem"), "alloc"), "init");

    private static void SetString(nint item, string value, string type)
    {
        if (objc_msgSend_bool2(item, Sel("setString:forType:"), NSString(value), NSString(type)) == 0)
            throw new InvalidOperationException($"The pasteboard refused {type}.");
    }

    private static void SetData(nint item, ReadOnlySpan<byte> bytes, string type)
    {
        fixed (byte* p = bytes)
        {
            var data = objc_msgSend_ptr_count(Class("NSData"), Sel("dataWithBytes:length:"), (nint)p, (nuint)bytes.Length);
            if (objc_msgSend_bool2(item, Sel("setData:forType:"), data, NSString(type)) == 0)
                throw new InvalidOperationException($"The pasteboard refused {type}.");
        }
    }

    private static ReadOnlyMemory<byte> ReadData(nint item, string type)
    {
        var data = objc_msgSend(item, Sel("dataForType:"), NSString(type));
        if (data == 0) return ReadOnlyMemory<byte>.Empty;
        var length = (int)objc_msgSend_nuint(data, Sel("length"));
        return new ReadOnlySpan<byte>((void*)Send(data, "bytes"), length).ToArray();
    }

    /// <summary>An autoreleased NSString.</summary>
    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return objc_msgSend(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static string? ReadString(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String"));

    private static nint Class(string name) => objc_getClass(name);
    private static nint Sel(string name) => sel_registerName(name);
    private static nint Send(nint receiver, string selector) => objc_msgSend(receiver, Sel(selector));

    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector);
    [DllImport(ObjC)] private static extern nint objc_msgSend(nint receiver, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_ptr_count(nint receiver, nint selector, nint pointer, nuint count);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint objc_msgSend_nuint(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint objc_msgSend_nuint_arg(nint receiver, nint selector, nuint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte objc_msgSend_bool(nint receiver, nint selector, nint argument);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte objc_msgSend_bool2(nint receiver, nint selector, nint first, nint second);
}
#endif
