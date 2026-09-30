using System.Text;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The X11 clipboard's targets as bytes, with no display: what <c>LinuxClipboard</c> offers for a
/// <see cref="ClipboardContent"/>, and how it reads one back from the targets another app offers. Pure, so it is tested
/// without an X server.
/// <list type="bullet">
/// <item>Text is offered under Chromium's own targets for it, so other apps and Chromium's pages read the kit's copies as
/// they read Chromium's.</item>
/// <item>Files are <c>text/uri-list</c> and GNOME's <c>x-special/gnome-copied-files</c>, which a file manager pastes.</item>
/// <item>Every format is offered under its media type, the name X11 apps use for a target; a picture reads back as PNG
/// alone, as in the other shells.</item>
/// </list>
/// </summary>
internal static class X11ClipboardFormats
{
    internal const string Utf8String = "UTF8_STRING", Latin1String = "STRING", Text = "TEXT";
    internal const string PlainUtf8 = "text/plain;charset=utf-8", Plain = "text/plain";
    internal const string UriList = "text/uri-list", GnomeCopiedFiles = "x-special/gnome-copied-files";

    /// <summary>One target an owner serves: its name, the type its data is written as, and the data.</summary>
    public readonly record struct Target(string Name, string Type, byte[] Data);

    /// <summary>
    /// The targets that carry <paramref name="content"/>. Its files must be absolute paths. Throws
    /// <see cref="NotSupportedException"/> for a format that is not keyed by a media type, which X11 would take as one
    /// of its own names.
    /// </summary>
    public static IReadOnlyList<Target> Offer(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var targets = new List<Target>();
        if (content.Text is { } text)
        {
            var utf8 = Encoding.UTF8.GetBytes(text);
            targets.Add(new(Utf8String, Utf8String, utf8));
            targets.Add(new(PlainUtf8, PlainUtf8, utf8));
            targets.Add(new(Plain, Plain, utf8));
            targets.Add(new(Text, Utf8String, utf8));
            targets.Add(new(Latin1String, Latin1String, Encoding.Latin1.GetBytes(text)));
        }
        if (content.Files.Count > 0)
        {
            var uris = content.Files.Select(FileUri).ToList();
            targets.Add(new(UriList, UriList, Encoding.UTF8.GetBytes(string.Concat(uris.Select(u => u + "\r\n")))));
            targets.Add(new(GnomeCopiedFiles, GnomeCopiedFiles, Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", uris))));
        }
        foreach (var (mediaType, bytes) in content.Formats)
        {
            // A target named with parameters, spaces and all (LibreOffice's), reads back and so must write back too.
            if (!mediaType.Contains('/'))
                throw new NotSupportedException($"The X11 clipboard carries a format under its media type, and '{mediaType}' is not one.");
            if (targets.All(t => t.Name != mediaType)) targets.Add(new(mediaType, mediaType, bytes.ToArray()));
        }
        return targets;
    }

    /// <summary>
    /// What <paramref name="targets"/> hold, reading each through <paramref name="fetch"/>, which answers null for a target
    /// it could not get. Absent formats are simply absent.
    /// </summary>
    public static ClipboardContent Read(IReadOnlyCollection<string> targets, Func<string, byte[]?> fetch)
    {
        var offered = new HashSet<string>(targets, StringComparer.Ordinal);
        var text = ReadText(offered, fetch);
        IReadOnlyList<string> files =
            offered.Contains(UriList) && fetch(UriList) is { } list ? ParseUriList(list)
            : offered.Contains(GnomeCopiedFiles) && fetch(GnomeCopiedFiles) is { } copied ? ParseUriList(copied, skipFirstLine: true)
            : [];
        var formats = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var target in targets)
            if (Carried(target) && !formats.ContainsKey(target) && fetch(target) is { } data)
                formats[target] = target == ClipboardContent.Html ? Html(data) : data;
        return new ClipboardContent { Text = text, Files = files, Formats = formats };
    }

    /// <summary>The text among <paramref name="targets"/>, fetching only that: UTF-8 first, then Latin-1's <c>STRING</c>.</summary>
    public static string? ReadText(IReadOnlyCollection<string> targets, Func<string, byte[]?> fetch)
    {
        foreach (var target in new[] { Utf8String, PlainUtf8, Latin1String, Plain })
            if (targets.Contains(target) && fetch(target) is { } bytes)
                return (target == Latin1String ? Encoding.Latin1 : Encoding.UTF8).GetString(bytes).TrimEnd('\0');
        return null;
    }

    /// <summary>A <c>file:</c> URI for an absolute path, each segment percent-encoded.</summary>
    public static string FileUri(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            throw new ArgumentException($"A file on the clipboard must be an absolute path: '{path}'.", nameof(path));
        return "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>
    /// The local paths in a URI list (<c>text/uri-list</c>, or GNOME's list after its first line). Comments, other
    /// schemes and other hosts are skipped.
    /// </summary>
    public static IReadOnlyList<string> ParseUriList(ReadOnlySpan<byte> bytes, bool skipFirstLine = false)
    {
        var paths = new List<string>();
        var lines = Encoding.UTF8.GetString(bytes).TrimEnd('\0').Split('\n');
        foreach (var raw in lines.Skip(skipFirstLine ? 1 : 0))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || !line.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line["file://".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) continue;
            var host = rest[..slash];
            if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
            paths.Add(Uri.UnescapeDataString(rest[slash..]));
        }
        return paths;
    }

    // Media types other than text, files and pictures other than PNG, which the named properties carry.
    private static bool Carried(string target) =>
        target.Contains('/')
        && !target.StartsWith(Plain, StringComparison.Ordinal)
        && target != UriList
        && !target.StartsWith("x-special/", StringComparison.Ordinal)
        && (!target.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || target == ClipboardContent.PngImage);

    // HTML as UTF-8: some apps write it as UTF-16 with a byte-order mark.
    private static byte[] Html(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(data, 2, data.Length - 2).TrimEnd('\0'));
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2).TrimEnd('\0'));
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        var end = data.Length;
        while (end > start && data[end - 1] == 0) end--;
        return start == 0 && end == data.Length ? data : data[start..end];
    }
}
