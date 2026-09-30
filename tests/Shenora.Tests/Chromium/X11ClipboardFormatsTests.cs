using System.Text;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Linux clipboard's targets, decided without an X server: what is offered for a copy, and what another app's
/// targets read back as. The connection itself was driven on X11 against xclip and Chromium's own clipboard.
/// </summary>
public class X11ClipboardFormatsTests
{
    private static Dictionary<string, X11ClipboardFormats.Target> Offer(ClipboardContent content) =>
        X11ClipboardFormats.Offer(content).ToDictionary(t => t.Name);

    [Fact]
    public void Text_is_offered_under_Chromiums_targets_for_it()
    {
        var offer = Offer(new ClipboardContent { Text = "héllo 日本" });

        Assert.Equal(["UTF8_STRING", "text/plain;charset=utf-8", "text/plain", "TEXT", "STRING"], offer.Keys);
        Assert.Equal("héllo 日本", Encoding.UTF8.GetString(offer["UTF8_STRING"].Data));
        Assert.Equal("UTF8_STRING", offer["TEXT"].Type);
        // STRING is Latin-1: what it cannot hold becomes '?'.
        Assert.Equal("héllo ??", Encoding.Latin1.GetString(offer["STRING"].Data));
    }

    [Fact]
    public void Files_are_a_uri_list_and_GNOMEs_copied_files_and_read_back_as_paths()
    {
        string[] paths = ["/tmp/a b.txt", "/home/u/日本/c#1.png"];

        var offer = Offer(new ClipboardContent { Files = paths });

        Assert.Equal("file:///tmp/a%20b.txt\r\nfile:///home/u/%E6%97%A5%E6%9C%AC/c%231.png\r\n", Encoding.UTF8.GetString(offer["text/uri-list"].Data));
        Assert.Equal("copy\nfile:///tmp/a%20b.txt\nfile:///home/u/%E6%97%A5%E6%9C%AC/c%231.png",
            Encoding.UTF8.GetString(offer["x-special/gnome-copied-files"].Data));
        Assert.Equal(paths, X11ClipboardFormats.ParseUriList(offer["text/uri-list"].Data));
        Assert.Equal(paths, X11ClipboardFormats.ParseUriList(offer["x-special/gnome-copied-files"].Data, skipFirstLine: true));
    }

    [Fact]
    public void A_relative_file_is_refused() =>
        Assert.Throws<ArgumentException>(() => X11ClipboardFormats.Offer(new ClipboardContent { Files = ["a.txt"] }));

    [Fact]
    public void A_uri_list_skips_comments_other_schemes_and_other_hosts()
    {
        var list = "# a comment\r\nfile:///ok\r\nhttps://example.com/x\r\nfile://localhost/also%20ok\r\nfile://elsewhere/no\r\n\0";

        Assert.Equal(["/ok", "/also ok"], X11ClipboardFormats.ParseUriList(Encoding.UTF8.GetBytes(list)));
    }

    [Fact]
    public void Formats_are_offered_under_their_media_type_and_a_non_media_type_is_refused()
    {
        var offer = Offer(new ClipboardContent
        {
            Formats = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [ClipboardContent.Html] = "<b>x</b>"u8.ToArray(),
                [ClipboardContent.PngImage] = new byte[] { 0x89, 0x50 },
                ["application/x-app"] = new byte[] { 1, 2, 3 },
            },
        });

        Assert.Equal(["text/html", "image/png", "application/x-app"], offer.Keys);
        Assert.Equal("application/x-app", offer["application/x-app"].Type);
        Assert.Throws<NotSupportedException>(() => X11ClipboardFormats.Offer(new ClipboardContent
        {
            Formats = new Dictionary<string, ReadOnlyMemory<byte>> { ["TARGETS"] = new byte[1] },
        }));
    }

    [Fact]
    public void A_target_with_parameters_reads_back_and_writes_back()
    {
        const string target = "application/x-openoffice-embed-source-xml;windows_formatname=\"Star Embed Source (XML)\"";

        var read = X11ClipboardFormats.Read([target], _ => [1, 2]);
        var offer = Offer(read);

        Assert.Equal([target], offer.Keys);
    }

    [Fact]
    public void A_copy_reads_back_as_itself()
    {
        var content = new ClipboardContent
        {
            Text = "both",
            Files = ["/tmp/f"],
            Formats = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [ClipboardContent.Html] = "<i>h</i>"u8.ToArray(),
                ["application/x-app"] = new byte[] { 7 },
            },
        };
        var offer = Offer(content);

        var read = X11ClipboardFormats.Read(offer.Keys, name => offer.TryGetValue(name, out var t) ? t.Data : null);

        Assert.Equal("both", read.Text);
        Assert.Equal(["/tmp/f"], read.Files);
        Assert.Equal(["application/x-app", "text/html"], read.Formats.Keys.Order());
        Assert.Equal("<i>h</i>", Encoding.UTF8.GetString(read.Formats[ClipboardContent.Html].Span));
    }

    [Fact]
    public void Text_prefers_UTF8_reads_STRING_as_Latin1_and_ends_at_a_NUL()
    {
        var targets = new Dictionary<string, byte[]> { ["STRING"] = [0x63, 0x61, 0x66, 0xE9, 0] };
        Assert.Equal("café", X11ClipboardFormats.Read(targets.Keys, n => targets.GetValueOrDefault(n)).Text);

        targets["UTF8_STRING"] = "utf8 ✓"u8.ToArray();
        Assert.Equal("utf8 ✓", X11ClipboardFormats.Read(targets.Keys, n => targets.GetValueOrDefault(n)).Text);
    }

    [Fact]
    public void Empty_text_is_an_empty_item_and_a_target_that_does_not_answer_is_absent()
    {
        Assert.Equal("", X11ClipboardFormats.Read(["UTF8_STRING"], _ => []).Text);

        var read = X11ClipboardFormats.Read(["UTF8_STRING", "text/html"], _ => null);
        Assert.Null(read.Text);
        Assert.True(read.IsEmpty);
    }

    [Fact]
    public void Pictures_other_than_PNG_and_the_text_and_file_targets_are_not_formats()
    {
        string[] targets = ["TARGETS", "image/png", "image/jpeg", "image/bmp", "text/plain", "text/plain;charset=utf-8",
            "text/uri-list", "x-special/gnome-copied-files", "chromium/x-web-custom-data"];

        var read = X11ClipboardFormats.Read(targets, _ => [1]);

        Assert.Equal(["chromium/x-web-custom-data", "image/png"], read.Formats.Keys.Order());
    }

    [Fact]
    public void HTML_in_UTF16_reads_as_UTF8()
    {
        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("<p>日本</p>\0")).ToArray();

        var read = X11ClipboardFormats.Read(["text/html"], _ => utf16);

        Assert.Equal("<p>日本</p>", Encoding.UTF8.GetString(read.Formats[ClipboardContent.Html].Span));
    }
}
