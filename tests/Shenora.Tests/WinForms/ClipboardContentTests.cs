using System.Globalization;
using System.Text;
using Shenora.Core.Shell;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The clipboard's two testable halves that need no clipboard: the content record, and CF_HTML's
/// header arithmetic.
/// <para>
/// 🔴 <b>The header is where this silently breaks.</b> Its offsets are BYTE offsets into the UTF-8
/// payload, and computing them in CHARACTERS is both the obvious mistake and an invisible one — every
/// ASCII test passes, and the paste truncates only once someone copies an em-dash or a CJK character.
/// So the tests below slice the real bytes at the offsets the header declares and compare.
/// </para>
/// </summary>
public class ClipboardContentTests
{
    private static (int StartHtml, int EndHtml, int StartFragment, int EndFragment) OffsetsOf(string payload)
    {
        int Read(string key)
        {
            var at = payload.IndexOf(key + ':', StringComparison.Ordinal) + key.Length + 1;
            return int.Parse(payload.Substring(at, 10), CultureInfo.InvariantCulture);
        }
        return (Read("StartHTML"), Read("EndHTML"), Read("StartFragment"), Read("EndFragment"));
    }

    [Theory]
    [InlineData("<b>plain ascii</b>")]
    [InlineData("<p>an em dash — and a CJK run 神阙 with astral 😀</p>")]
    public void The_CF_HTML_header_declares_BYTE_offsets_that_actually_land_on_the_fragment(string html)
    {
        var payload = HtmlClipboardFormat.Wrap(html);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var (startHtml, endHtml, startFragment, endFragment) = OffsetsOf(payload);

        // The fragment the offsets point at must BE the html — this is what a receiving app slices.
        Assert.Equal(html, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));

        // And the document offsets must bracket it and end at the payload's end.
        Assert.True(startHtml < startFragment);
        Assert.Equal(bytes.Length, endHtml);
        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
    }

    [Fact]
    public void A_multibyte_fragment_would_be_TRUNCATED_by_character_offsets()
    {
        // Pins the distinction rather than trusting it. ⚠ It shows at the END offsets, not the start:
        // everything before the fragment is ASCII, so StartFragment is the same number either way and a
        // test asserting on it proves nothing. The multi-byte content is INSIDE the fragment, so a
        // character count ends short — which is why the symptom is a paste with its tail cut off.
        const string html = "<p>神阙</p>";
        var payload = HtmlClipboardFormat.Wrap(html);
        var (_, endHtml, startFragment, endFragment) = OffsetsOf(payload);

        var asCharacters = payload.IndexOf(html, StringComparison.Ordinal) + html.Length;
        Assert.True(endFragment > asCharacters,
            $"EndFragment {endFragment} must exceed the character-count answer {asCharacters}.");
        Assert.Equal(Encoding.UTF8.GetByteCount(html), endFragment - startFragment);
        Assert.Equal(Encoding.UTF8.GetByteCount(payload), endHtml);
    }

    [Fact]
    public void Wrap_and_Unwrap_round_trip()
    {
        const string html = "<i>round</i> trip — 神阙";
        Assert.Equal(html, HtmlClipboardFormat.Unwrap(HtmlClipboardFormat.Wrap(html)));
    }

    [Fact]
    public void Unwrap_answers_null_for_a_payload_with_no_fragment_markers()
    {
        // A receiver must be able to tell "no HTML here" from "empty HTML here".
        Assert.Null(HtmlClipboardFormat.Unwrap("Version:0.9\r\n<html><body>no markers</body></html>"));
        Assert.Null(HtmlClipboardFormat.Unwrap(""));
        Assert.Equal("", HtmlClipboardFormat.Unwrap(HtmlClipboardFormat.Wrap("")));
    }

    [Fact]
    public void An_empty_content_is_the_same_thing_Clear_leaves()
    {
        Assert.True(new ClipboardContent().IsEmpty);
        Assert.False(new ClipboardContent { Text = "" }.IsEmpty);   // an empty text item is still an item
        Assert.False(new ClipboardContent { Files = ["a"] }.IsEmpty);
        Assert.False(new ClipboardContent
        {
            Formats = new Dictionary<string, ReadOnlyMemory<byte>> { [ClipboardContent.PngImage] = new byte[1] },
        }.IsEmpty);
    }
}
