using System.Collections.Specialized;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// The <see cref="IClipboardService"/> implementation. 🔴 Every operation runs on the shared STA
/// apartment — the WinForms clipboard is STA-only, and OLE must service the flush on a PUMPED thread
/// (see <see cref="StaThread.RunSharedAsync"/>).
/// <para>
/// ⚠ <b>It TRANSLATES the well-known media types into the formats other Windows applications actually
/// read</b>, rather than storing them under their media-type name: a PNG filed as <c>"image/png"</c> is
/// invisible to Explorer, Word and every browser, so the paste appears to work and produces nothing. An
/// unrecognised type IS stored verbatim — it is a private format only its own app will ask for.
/// </para>
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // ⚠ Empty means CLEAR, not throw: Clipboard.SetText rejects an empty string, but an empty
        // selection is app data rather than a programming error. A null argument still throws above.
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
            // ONE DataObject for every representation, set once — that is what makes a copy atomic; each
            // Clipboard.SetX call would replace the previous one's work.
            var data = new DataObject();
            // 🔴 Anything the DataObject only REFERENCES has to outlive the flush below — see SetPng.
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
                        // A private format, under the app's own name. A MemoryStream rather than byte[]:
                        // WinForms writes streams natively, an array would need the BinaryFormatter that
                        // .NET no longer ships.
                        data.SetData(mediaType, new MemoryStream(bytes.ToArray()));
                        break;
                }
            }

            // copy: true — the data must outlive this process. ⚠ And RETRY: the clipboard is a single
            // global resource any process can hold open, so a copy racing another app's paste fails with
            // an ExternalException for no reason the user could act on.
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);
            // 🔴 Now, and not before: the flush has rendered every format into global memory.
            picture?.Dispose();
            return true;
        });
    }

    /// <inheritdoc />
    public Task<ClipboardContent> GetAsync() => StaThread.RunSharedAsync(() =>
    {
        // ONE snapshot, read six ways. Every `Clipboard.X` helper re-opens the clipboard, so six in a row
        // can straddle another application's copy and return half of each.
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
        // ⚠ Windows SYNTHESIZES formats, so asking for every name GetFormats() reports drags in
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
    /// One format off the snapshot as RAW BYTES, or null when it is not there. Guarded, because
    /// <see cref="IDataObject"/> is implemented by whatever application did the copy.
    /// <para>
    /// 🔴 <b>The runtime type is not stable, and assuming one loses the format SILENTLY.</b> The same
    /// clipboard format comes back as a <see cref="string"/>, a <see cref="MemoryStream"/> or a
    /// <c>byte[]</c> depending on whether the value has been round-tripped through OLE — and a flushed
    /// copy is exactly that. <b>Measured:</b> a version doing <c>GetData(format) as T</c> lost
    /// <c>text/html</c> or <c>image/png</c> on roughly one read in six, reporting the format as ABSENT
    /// because a failed cast is indistinguishable from an empty clipboard. Accept every representation.
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
                // A string arrives when the value never left this process. UTF-8, which is what every
                // byte-oriented clipboard format on this side was written as.
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
    /// Offer the picture BOTH ways: <c>"PNG"</c> is what browsers and modern editors look for and it keeps
    /// transparency, <c>CF_BITMAP</c> is what everything older reads. Neither alone is enough.
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

            // 🔴 RETURNED, NOT DISPOSED HERE. The DataObject only holds a REFERENCE until
            // `SetDataObject` flushes it, so disposing on the way out hands OLE a disposed image to
            // render — and a format that throws mid-flush takes the OTHER formats down with it.
            // Measured: with a `using` here, a copy of text + HTML + PNG lost `text/html`, `image/png`
            // or even the TEXT on roughly one attempt in six, reported as an absent format.
            return bitmap;
        }
        catch (Exception)
        {
            // Bytes that are not a decodable image still go on the clipboard as PNG; only the
            // CF_BITMAP compatibility half is lost.
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
/// 🔴 <b>Bare HTML on the clipboard is not readable as HTML by anything.</b> Word, Outlook and every
/// browser look for <c>CF_HTML</c>, whose payload must begin with a header giving BYTE offsets — not
/// character offsets — to the document and the fragment. Get it wrong and the paste silently arrives as
/// plain text or empty.
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
        // ⚠ The offsets are into the FINAL string and the header's own length depends on them, so they
        // are measured against a ten-digit-wide template and only then formatted. Computing them against
        // the un-padded header shifts the fragment and truncates the paste.
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
