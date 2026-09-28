using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// Windows' clipboard formats as bytes, with no window and no clipboard: the parts of <c>Win32Clipboard</c>
/// that are pure, so they are tested without touching the machine's clipboard.
/// </summary>
internal static class ClipboardFormats
{
    /// <summary>
    /// <c>CF_HDROP</c>: a <c>DROPFILES</c> header (the list's offset, and wide characters) and the paths, each ended
    /// by a NUL, the list by one more.
    /// </summary>
    public static byte[] BuildDropFiles(IReadOnlyList<string> paths)
    {
        const int header = 20;   // DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide
        var list = string.Concat(paths.Select(p => p + '\0')) + '\0';
        var bytes = new byte[header + Encoding.Unicode.GetByteCount(list)];
        BitConverter.GetBytes(header).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 16);   // fWide
        Encoding.Unicode.GetBytes(list).CopyTo(bytes, header);
        return bytes;
    }

    /// <summary>The paths out of a <c>DROPFILES</c> block, wide or ANSI; none when it is malformed.</summary>
    public static IReadOnlyList<string> ParseDropFiles(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20) return [];
        var offset = BitConverter.ToInt32(bytes[..4]);
        var wide = BitConverter.ToInt32(bytes.Slice(16, 4)) != 0;
        if (offset < 20 || offset >= bytes.Length) return [];
        var text = wide ? Encoding.Unicode.GetString(bytes[offset..]) : Encoding.Default.GetString(bytes[offset..]);
        var end = text.IndexOf("\0\0", StringComparison.Ordinal);
        return [.. (end < 0 ? text : text[..end]).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
    }

    // CF_HTML's header, which Windows requires and no other platform has: BYTE offsets, into the final
    // string, to the document and the fragment. The WebView2 shell's HtmlClipboardFormat, which a test pins
    // this copy to.
    private const string Header = "Version:0.9\r\nStartHTML:{0}\r\nEndHTML:{1}\r\nStartFragment:{2}\r\nEndFragment:{3}\r\n";
    private const string Open = "<html><body><!--StartFragment-->";
    private const string Close = "<!--EndFragment--></body></html>";

    public static string WrapHtml(string html)
    {
        // Measured against a ten-digit template first: the header's own length depends on the offsets.
        var template = string.Format(CultureInfo.InvariantCulture, Header, "0000000000", "0000000000", "0000000000", "0000000000");
        var startHtml = Encoding.UTF8.GetByteCount(template);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(Open);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(html);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(Close);
        var header = string.Format(CultureInfo.InvariantCulture, Header,
            startHtml.ToString("D10", CultureInfo.InvariantCulture), endHtml.ToString("D10", CultureInfo.InvariantCulture),
            startFragment.ToString("D10", CultureInfo.InvariantCulture), endFragment.ToString("D10", CultureInfo.InvariantCulture));
        return header + Open + html + Close;
    }

    public static string? UnwrapHtml(string payload)
    {
        var start = payload.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = payload.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0 || end < start) return null;
        start += "<!--StartFragment-->".Length;
        return payload[start..end];
    }

    /// <summary>Text up to its first NUL: a clipboard string is NUL-terminated, and its block often longer.</summary>
    public static string UpToNul(string text)
    {
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }
}

#if CEF_WINDOWS
/// <summary>
/// The Chromium shell's <see cref="IClipboardService"/> on Windows: the Win32 clipboard directly, with no WinForms
/// in the shell. The formats are the WebView2 shell's <c>ClipboardService</c>'s, so other applications read them:
/// <c>CF_UNICODETEXT</c>, <c>CF_HDROP</c> for files, <c>HTML Format</c> with its byte-offset header, <c>PNG</c>, and
/// an app's own media type under its own name.
/// <para>
/// ⚠ A picture is <c>PNG</c> only, both ways. The WebView2 shell also offers and reads a bitmap, which needs an
/// image codec this shell does not carry, so a picture another app copied as a bitmap alone (a Print Screen)
/// reads as none here. A page's own <c>navigator.clipboard</c> handles images in full.
/// </para>
/// <para>
/// Every operation opens the clipboard, does ALL of its work, and closes it, on one thread-pool thread with a
/// message-only window as the owner (a copy with no owner is refused), so a write is atomic and a read is one
/// snapshot. The clipboard is one resource every process shares, so opening it is retried.
/// </para>
/// </summary>
internal sealed class Win32Clipboard : IClipboardService
{
    private const uint CF_UNICODETEXT = 13, CF_HDROP = 15, GMEM_MOVEABLE = 0x0002;
    private static readonly nint HWND_MESSAGE = -3;
    private readonly SemaphoreSlim _one = new(1, 1);

    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Empty means CLEAR, as on the WebView2 shell: an empty selection is data, not a programming error.
        return text.Length == 0 ? ClearAsync() : SetAsync(new ClipboardContent { Text = text });
    }

    public async Task<string?> GetTextAsync() => (await GetAsync().ConfigureAwait(false)).Text;

    public Task ClearAsync() => RunAsync(() => { EmptyClipboard(); return true; });

    public Task SetAsync(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsEmpty) return ClearAsync();
        var files = content.Files.Select(f => { ArgumentException.ThrowIfNullOrWhiteSpace(f, nameof(content)); return Path.GetFullPath(f); }).ToList();
        return RunAsync(() =>
        {
            if (EmptyClipboard() == 0) throw new InvalidOperationException("The clipboard could not be emptied.");
            if (content.Text is { } text) Put(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + '\0'));
            if (files.Count > 0) Put(CF_HDROP, ClipboardFormats.BuildDropFiles(files));
            foreach (var (mediaType, bytes) in content.Formats)
            {
                switch (mediaType)
                {
                    case ClipboardContent.PngImage: Put(Register("PNG"), bytes.ToArray()); break;
                    case ClipboardContent.Html:
                        Put(Register("HTML Format"), Encoding.UTF8.GetBytes(ClipboardFormats.WrapHtml(Encoding.UTF8.GetString(bytes.Span)) + '\0'));
                        break;
                    default: Put(Register(mediaType), bytes.ToArray()); break;   // private, under the app's own name
                }
            }
            return true;
        });
    }

    public Task<ClipboardContent> GetAsync() => RunAsync(() =>
    {
        var formats = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        if (Take(Register("PNG")) is { } png) formats[ClipboardContent.PngImage] = png;
        if (Take(Register("HTML Format")) is { } wrapped && ClipboardFormats.UnwrapHtml(ClipboardFormats.UpToNul(Encoding.UTF8.GetString(wrapped))) is { } html)
            formats[ClipboardContent.Html] = Encoding.UTF8.GetBytes(html);

        // Private formats are read back by NAME, and only names that look like a media type.
        for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
        {
            if (format < 0xC000 || FormatName(format) is not { } name || !name.Contains('/', StringComparison.Ordinal) || formats.ContainsKey(name)) continue;
            if (Take(format) is { } bytes) formats[name] = bytes;
        }

        return new ClipboardContent
        {
            Text = Take(CF_UNICODETEXT) is { } text ? ClipboardFormats.UpToNul(Encoding.Unicode.GetString(text)) : null,
            Files = Take(CF_HDROP) is { } drop ? ClipboardFormats.ParseDropFiles(drop) : [],
            Formats = formats,
        };
    });

    private async Task<T> RunAsync<T>(Func<T> work)
    {
        await _one.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => WithClipboard(work)).ConfigureAwait(false); }
        finally { _one.Release(); }
    }

    private static T WithClipboard<T>(Func<T> work)
    {
        var owner = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
        try
        {
            var opened = false;
            for (var attempt = 0; attempt < 10 && !opened; attempt++)
            {
                opened = OpenClipboard(owner) != 0;
                if (!opened) Thread.Sleep(100);
            }
            if (!opened) throw new InvalidOperationException("Another application is holding the clipboard open.");
            try { return work(); }
            finally { CloseClipboard(); }
        }
        finally { if (owner != 0) DestroyWindow(owner); }
    }

    private static void Put(uint format, byte[] bytes)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (memory == 0) throw new OutOfMemoryException("The clipboard could not be given memory.");
        var target = GlobalLock(memory);
        Marshal.Copy(bytes, 0, target, bytes.Length);
        GlobalUnlock(memory);
        // On success the clipboard owns the block; only a refusal leaves it ours to free.
        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory);
            throw new InvalidOperationException($"The clipboard refused format {format}.");
        }
    }

    private static byte[]? Take(uint format)
    {
        if (IsClipboardFormatAvailable(format) == 0) return null;
        var memory = GetClipboardData(format);
        if (memory == 0) return null;
        var source = GlobalLock(memory);
        if (source == 0) return null;
        try
        {
            var bytes = new byte[(int)GlobalSize(memory)];
            Marshal.Copy(source, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(memory); }
    }

    private static uint Register(string name) => RegisterClipboardFormatW(name);

    private static string? FormatName(uint format)
    {
        var buffer = new char[256];
        var length = GetClipboardFormatNameW(format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32")] private static extern int DestroyWindow(nint hwnd);
    [DllImport("user32")] private static extern int OpenClipboard(nint owner);
    [DllImport("user32")] private static extern int CloseClipboard();
    [DllImport("user32")] private static extern int EmptyClipboard();
    [DllImport("user32")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32")] private static extern int IsClipboardFormatAvailable(uint format);
    [DllImport("user32")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClipboardFormatNameW(uint format, [Out] char[] name, int max);
    [DllImport("kernel32")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32")] private static extern nint GlobalFree(nint memory);
    [DllImport("kernel32")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32")] private static extern int GlobalUnlock(nint memory);
    [DllImport("kernel32")] private static extern nuint GlobalSize(nint memory);
}
#endif
