using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's clipboard formats, decided without the machine's clipboard: the file list's layout, and
/// the CF_HTML header, which must stay byte-identical to the WebView2 shell's so both shells' copies paste alike.
/// </summary>
public class ChromiumClipboardFormatTests
{
    [Fact]
    public void A_file_list_round_trips_through_DROPFILES()
    {
        string[] paths = [@"C:\a.txt", @"D:\folder with space\b c.png", @"C:\日本語\ファイル.txt"];

        var bytes = ClipboardFormats.BuildDropFiles(paths);

        Assert.Equal(20, BitConverter.ToInt32(bytes, 0));   // the list follows the header
        Assert.Equal(1, BitConverter.ToInt32(bytes, 16));   // wide characters
        Assert.Equal(paths, ClipboardFormats.ParseDropFiles(bytes));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void A_malformed_DROPFILES_reads_as_no_files(byte[] bytes) =>
        Assert.Empty(ClipboardFormats.ParseDropFiles(bytes));

    [Theory]
    [InlineData("<b>bold</b>")]
    [InlineData("<p>héllo — 日本語</p>")]
    [InlineData("")]
    public void CF_HTML_is_the_WebView2_shells_byte_for_byte(string html)
    {
        Assert.Equal(Shenora.Windows.HtmlClipboardFormat.Wrap(html), ClipboardFormats.WrapHtml(html));
        Assert.Equal(html, ClipboardFormats.UnwrapHtml(ClipboardFormats.WrapHtml(html)));
    }

    [Fact]
    public void Text_ends_at_its_NUL() =>
        Assert.Equal("copied", ClipboardFormats.UpToNul("copied\0\0garbage"));

    [Fact]
    public void The_shell_registers_its_clipboard_on_Windows()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Clipboard test" });
        builder.UseChromium(new ChromiumHostOptions());
        using var app = builder.Build();
        Assert.IsType<Win32Clipboard>(app.Services.GetRequiredService<IClipboardService>());
    }
}

/// <summary>
/// The Chromium shell's clipboard against the REAL system clipboard, like the WebView2 shell's
/// <c>ClipboardServiceTests</c>, and held out of the gate for the same reason: one OS resource every process
/// shares. ⚠ These clobber the machine's clipboard. Run with <c>dev.mjs test clipboard</c>.
/// </summary>
[Trait("Category", "RealClipboard")]
public class ChromiumClipboardTests
{
    [Fact]
    public async Task Every_representation_survives_ONE_copy_and_comes_back_intact()
    {
        var clipboard = new Win32Clipboard();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var own = Encoding.UTF8.GetBytes("{\"app\":\"payload\"}");
        var files = new[] { Path.GetFullPath("clip-a.txt"), Path.GetFullPath("clip b.txt") };

        await clipboard.SetAsync(new ClipboardContent
        {
            Text = "copied text — 日本語",
            Files = files,
            Formats = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [ClipboardContent.PngImage] = png,
                [ClipboardContent.Html] = Encoding.UTF8.GetBytes("<b>bold</b>"),
                ["application/x-shenora-test"] = own,
            },
        });
        await Task.Delay(150);   // a copy is a user action; see ClipboardServiceTests on pacing
        var read = await clipboard.GetAsync();

        Assert.Equal("copied text — 日本語", read.Text);
        Assert.Equal(files, read.Files);
        Assert.Equal("<b>bold</b>", Encoding.UTF8.GetString(read.Formats[ClipboardContent.Html].Span));
        Assert.Equal(png, read.Formats[ClipboardContent.PngImage].ToArray());
        Assert.Equal(own, read.Formats["application/x-shenora-test"].ToArray());
    }

    [Fact]
    public async Task Setting_empty_text_CLEARS_rather_than_throwing()
    {
        var clipboard = new Win32Clipboard();
        await clipboard.SetTextAsync("something");
        await Task.Delay(150);

        await clipboard.SetTextAsync("");
        await Task.Delay(150);

        Assert.True((await clipboard.GetAsync()).IsEmpty);
    }
}
