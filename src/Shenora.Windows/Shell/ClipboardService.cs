using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

// IClipboardService moved to Shenora in P5.5 H4.1 — a clipboard is portable in both concept and
// signature, so app logic using it needs no Windows reference (D20). The STA-thread implementation
// below is what stays Windows-side.

/// <summary>
/// The <see cref="IClipboardService"/> implementation: every operation runs on a dedicated STA
/// thread — the WinForms clipboard is STA-only, and the source app grew ad-hoc thread wrappers
/// around every call site; this centralizes that pattern.
/// <para>
/// ⚠ <b>It TRANSLATES the well-known media types into the formats other Windows applications
/// actually read</b>, rather than storing them under their media-type name. That is the whole job:
/// a PNG filed as <c>"image/png"</c> is invisible to Explorer, Word and every browser, so the paste
/// would appear to work and produce nothing — the silent-success shape this kit keeps paying for.
/// An unrecognised type IS stored verbatim, which is correct: it is a private format that only the
/// app putting it there will ask for.
/// </para>
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Empty means CLEAR, not throw (P5.5 H2). Clipboard.SetText rejects an empty string with
        // ArgumentNullException — a surprise for "set the clipboard to what the user selected" when
        // the selection happens to be empty, which is app data, not a programming error. Clear()
        // is what the caller meant. A null argument is still a caller bug and still throws above.
        if (text.Length == 0) return ClearAsync();
        return SetAsync(new ClipboardContent { Text = text });
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync() =>
        StaThread.RunSharedAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <inheritdoc />
    public Task ClearAsync() => StaThread.RunSharedAsync(() => { Clipboard.Clear(); return true; });

    /// <inheritdoc />
    public Task SetAsync(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsEmpty) return ClearAsync();

        return StaThread.RunSharedAsync(() =>
        {
            // ONE DataObject for every representation. Building it up and setting it once is what makes
            // the operation atomic — each Clipboard.SetX call would replace the previous one's work.
            var data = new DataObject();
            // Anything the DataObject only REFERENCES has to outlive the flush below — see SetPng.
            Bitmap? picture = null;

            if (content.Text is { } text) data.SetText(text);

            if (content.Files.Count > 0)
            {
                var paths = new StringCollection();
                foreach (var file in content.Files)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(file, nameof(content));
                    paths.Add(Path.GetFullPath(file));
                }
                data.SetFileDropList(paths);
            }

            foreach (var (mediaType, bytes) in content.Formats)
            {
                switch (mediaType)
                {
                    case ClipboardContent.PngImage:
                        picture = SetPng(data, bytes);
                        break;
                    case ClipboardContent.Html:
                        data.SetData(DataFormats.Html, HtmlClipboardFormat.Wrap(Encoding.UTF8.GetString(bytes.Span)));
                        break;
                    default:
                        // A private format: stored under the app's own name, for the app to read back.
                        // A MemoryStream rather than the byte[] — WinForms writes streams natively, where
                        // an array would need the BinaryFormatter that .NET no longer ships.
                        data.SetData(mediaType, new MemoryStream(bytes.ToArray()));
                        break;
                }
            }

            // copy: true — the data must outlive this process, which is what a user expects of a copy.
            // ⚠ And RETRY: the Windows clipboard is a single global resource that any process can hold
            // open, so a copy racing another application's paste fails with an ExternalException for no
            // reason the user could act on. The overload exists for exactly this.
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);
            // Now, and not before: the flush has rendered every format into global memory, so the
            // managed objects behind them are no longer needed.
            picture?.Dispose();
            return true;
        });
    }

    /// <inheritdoc />
    public Task<ClipboardContent> GetAsync() => StaThread.RunSharedAsync(() =>
    {
        // ONE snapshot, read six ways. Every `Clipboard.X` helper re-opens the clipboard, so asking six
        // of them in a row can straddle another application's copy and return half of each — which would
        // be an odd way to implement the type whose whole point is that a clipboard item is ATOMIC.
        var data = Clipboard.GetDataObject();
        if (data is null) return new ClipboardContent();

        var formats = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);

        if (ReadPng(data) is { } png) formats[ClipboardContent.PngImage] = png;
        if (ReadText(data, DataFormats.Html) is { } wrapped
            && HtmlClipboardFormat.Unwrap(wrapped) is { } html)
        {
            formats[ClipboardContent.Html] = Encoding.UTF8.GetBytes(html);
        }

        // Private formats are read back by NAME, and only the ones that look like a media type.
        // ⚠ Windows SYNTHESIZES formats — asking for every name GetFormats() reports would drag in
        // conversions of things already returned above and can materialise a whole bitmap twice.
        foreach (var name in data.GetFormats(autoConvert: false))
        {
            if (!name.Contains('/', StringComparison.Ordinal) || formats.ContainsKey(name)) continue;
            if (ReadBytes(data, name) is { } bytes) formats[name] = bytes;
        }

        return new ClipboardContent
        {
            Text = ReadText(data, DataFormats.UnicodeText),
            Files = ReadFiles(data),
            Formats = formats,
        };
    });

    /// <summary>
    /// One format off the snapshot as RAW BYTES, or null when it is not there.
    /// <para>
    /// 🔴 <b>The runtime type is not stable and assuming one loses the format SILENTLY.</b> The same
    /// clipboard format comes back as a <see cref="string"/>, a <see cref="MemoryStream"/> or a
    /// <c>byte[]</c> depending on whether the value is still the original managed object or has been
    /// round-tripped through OLE — and a round trip is exactly what a flushed copy is. <b>Measured:</b> a
    /// version of this that did <c>GetData(format) as T</c> lost <c>text/html</c> or <c>image/png</c> on
    /// roughly one read in six, reporting the format as ABSENT rather than failing, because a failed cast
    /// is indistinguishable from an empty clipboard. Accept every representation instead.
    /// </para>
    /// <para>
    /// Guarded because <see cref="IDataObject"/> is implemented by whatever application did the copy: a
    /// malformed or hostile offering must not turn "read the clipboard" into a throw at the caller.
    /// </para>
    /// </summary>
    private static byte[]? ReadBytes(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format)) return null;
            return data.GetData(format) switch
            {
                MemoryStream stream => stream.ToArray(),
                byte[] bytes => bytes,
                // A string arrives when the value never left this process. UTF-8 rather than Unicode:
                // it is what every byte-oriented clipboard format on this side was written as.
                string text => Encoding.UTF8.GetBytes(text),
                Stream stream => ReadAll(stream),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>One format off the snapshot as TEXT, tolerating the same representation drift.</summary>
    private static string? ReadText(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format)) return null;
            return data.GetData(format) switch
            {
                string text => text,
                MemoryStream stream => Decode(stream.ToArray()),
                byte[] bytes => Decode(bytes),
                Stream stream => Decode(ReadAll(stream)),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Bytes to text, stopping at the first NUL. ⚠ A clipboard string that has been through OLE is
    /// NUL-TERMINATED, and keeping that terminator makes every later comparison fail by one character.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    /// <summary>The dropped-file list, which arrives as <c>string[]</c> or as a <c>StringCollection</c>.</summary>
    private static IReadOnlyList<string> ReadFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return [];
            return data.GetData(DataFormats.FileDrop) switch
            {
                string[] paths => paths,
                StringCollection paths => [.. paths.Cast<string>().Where(p => p is not null)],
                _ => [],
            };
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The picture as a decodable image, whatever shape the offering hands back.</summary>
    private static Image? ReadImage(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Bitmap)) return null;
            return data.GetData(DataFormats.Bitmap) as Image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Offer the picture BOTH ways. <c>"PNG"</c> is what browsers and modern editors look for and it
    /// keeps transparency; <c>CF_BITMAP</c> is what everything older reads, and an app offered only the
    /// first shows an empty Paste. Neither alone is enough.
    /// </summary>
    private static Bitmap? SetPng(DataObject data, ReadOnlyMemory<byte> bytes)
    {
        data.SetData("PNG", new MemoryStream(bytes.ToArray()));
        try
        {
            using var source = new MemoryStream(bytes.ToArray());
            // A copy, because Image.FromStream keeps the stream alive for the image's lifetime.
            using var image = Image.FromStream(source);
            var bitmap = new Bitmap(image);
            data.SetImage(bitmap);

            // 🔴 RETURNED, NOT DISPOSED HERE, and that is load-bearing. The DataObject only holds a
            // REFERENCE until `SetDataObject` flushes it, so disposing the bitmap on the way out of this
            // method hands OLE a disposed image to render — and a format that throws mid-flush can take
            // the OTHER formats down with it. Measured: with the `using` in place, a copy carrying text +
            // HTML + PNG lost `text/html`, `image/png` or even the TEXT on roughly one attempt in six,
            // reported as an absent format rather than as any kind of error.
            return bitmap;
        }
        catch (Exception)
        {
            // Bytes that are not a decodable image still go on the clipboard as PNG — the caller said
            // this is a PNG, and refusing the whole copy over the compatibility half would be worse.
            return null;
        }
    }

    /// <summary>The picture as PNG bytes, or null when the snapshot holds no picture.</summary>
    private static ReadOnlyMemory<byte>? ReadPng(IDataObject data)
    {
        // Prefer what was actually put there: re-encoding a CF_BITMAP loses the alpha channel, so a
        // screenshot with transparency comes back on a black or white background.
        if (ReadBytes(data, "PNG") is { } bytes) return bytes;

        if (ReadImage(data) is not { } image) return null;
        using (image)
        {
            using var buffer = new MemoryStream();
            image.Save(buffer, ImageFormat.Png);
            return buffer.ToArray();
        }
    }
}

/// <summary>
/// CF_HTML's header, which Windows requires and no other platform has.
/// <para>
/// 🔴 <b>Bare HTML on the clipboard is not readable as HTML by anything.</b> Word, Outlook and every
/// browser look for <c>CF_HTML</c>, whose payload must begin with a header giving BYTE offsets — not
/// character offsets — to the document and to the pasted fragment within it. Get it wrong and the paste
/// silently arrives as plain text or empty.
/// </para>
/// </summary>
internal static class HtmlClipboardFormat
{
    private const string Header =
        "Version:0.9\r\nStartHTML:{0}\r\nEndHTML:{1}\r\nStartFragment:{2}\r\nEndFragment:{3}\r\n";
    private const string Open = "<html><body><!--StartFragment-->";
    private const string Close = "<!--EndFragment--></body></html>";

    /// <summary>Wrap <paramref name="html"/> in the header, with the offsets filled in.</summary>
    internal static string Wrap(string html)
    {
        // The offsets are into the FINAL string, and the header's own length depends on them — so they
        // are measured against a template of the right width (ten digits, as the format specifies) and
        // only then formatted. Computing them against the un-padded header is the classic way to be off
        // by a few bytes, which shifts the fragment and truncates the paste.
        var template = string.Format(CultureInfo.InvariantCulture, Header, "0000000000", "0000000000",
                                     "0000000000", "0000000000");
        var headerBytes = Encoding.UTF8.GetByteCount(template);
        var openBytes = Encoding.UTF8.GetByteCount(Open);
        var htmlBytes = Encoding.UTF8.GetByteCount(html);
        var closeBytes = Encoding.UTF8.GetByteCount(Close);

        var startHtml = headerBytes;
        var startFragment = headerBytes + openBytes;
        var endFragment = startFragment + htmlBytes;
        var endHtml = endFragment + closeBytes;

        var header = string.Format(CultureInfo.InvariantCulture, Header,
            startHtml.ToString("D10", CultureInfo.InvariantCulture),
            endHtml.ToString("D10", CultureInfo.InvariantCulture),
            startFragment.ToString("D10", CultureInfo.InvariantCulture),
            endFragment.ToString("D10", CultureInfo.InvariantCulture));

        return header + Open + html + Close;
    }

    /// <summary>The fragment out of a CF_HTML payload, or null when it carries no fragment markers.</summary>
    internal static string? Unwrap(string payload)
    {
        var start = payload.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = payload.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0 || end < start) return null;
        start += "<!--StartFragment-->".Length;
        return payload[start..end];
    }
}
