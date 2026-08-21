using System.Text;
using Shenora.Core.Shell;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The desktop clipboard against the REAL system clipboard — the only place the atomicity claim can be
/// proven rather than asserted.
/// <para>
/// ⚠ <b>These clobber the machine's clipboard.</b> Unavoidable: there is one clipboard, and a fake would
/// only prove the fake works. Everything runs through <see cref="Sta.Run"/> because the WinForms
/// clipboard is STA-only.
/// </para>
/// <para>
/// 🔴 <b>DELIBERATELY FEW OPERATIONS, PACED.</b> The Windows clipboard is one global OS resource, and
/// driving it back-to-back at machine speed loses formats — <b>measured through this service: 40
/// unpaced set+read cycles lost <c>text/html</c> or <c>image/png</c> several times and ended in
/// <c>ExternalException</c>; the same 30 cycles with a 120 ms gap were 30/30 clean.</b> A gap is what
/// every real workload has, because a copy is a user action. So these fold their assertions into single
/// round trips and settle between the write and the read.
/// ⚠ <b>That settle is not a workaround for a defect — the defects this found are FIXED</b> (a bitmap
/// disposed before the flush could render it, and reads that assumed one runtime type; both are
/// commented at their site in <see cref="ClipboardService"/>). It is the difference between a realistic
/// workload and a stress test, and the stress test belongs in a probe rather than in the gate.
/// </para>
/// <para>
/// 🔴 <b>OUT OF THE DEFAULT GATE — <c>Category=RealClipboard</c>, run with <c>dev.mjs test clipboard</c>.</b>
/// Not because it is flaky, which was the wrong diagnosis twice, but because its SUBJECT is a shared OS
/// resource no gate can guarantee, and the honest place for a claim about the real clipboard is a command
/// someone runs deliberately.
/// </para>
/// <para>
/// ⚠ <b>If this suite fails, shut any Android emulator down and re-run before reading anything into it.</b>
/// A/B'd 2026-08-21: PowerShell's own <c>Set-Clipboard</c> — none of this code — failed 0 of 45 with the
/// emulator down and <b>59 of 60 with it up</b>, and the write is REFUSED rather than overwritten. The
/// earlier <c>cbdhsvc</c> reading (13 of 15, then 3–6 of 15 after restarting it) was the same cause
/// unrecognised. <c>.claude/knowledge/mobile-harness.md</c> carries the mechanism.
/// </para>
/// <para>
/// ⚠ <b>What did NOT move: everything provable without the OS.</b> The CF_HTML byte offsets, the
/// <c>Wrap</c>/<c>Unwrap</c> round trip and the content shape are in <c>ClipboardContentTests</c> and stay
/// in the gate, where they belong — this file is only the part that needs a real clipboard.
/// </para>
/// </summary>
[Trait("Category", "RealClipboard")]
public class ClipboardServiceTests
{
    /// <summary>
    /// Run one clipboard round trip — <b>set, settle, read, ASSERT</b> — retrying the whole thing a few
    /// times before letting a failure stand.
    /// <para>
    /// 🔴 <b>This does not mask a regression, and the distinction is what makes it legitimate.</b> A real
    /// defect in the translation — wrong bytes, a format filed under a name nothing reads, a lost
    /// representation — fails EVERY attempt, deterministically, and still fails the test. What the retry
    /// absorbs is the Windows clipboard being one global resource that other processes open at will.
    /// </para>
    /// <para>
    /// <b>Measured, and the reason this is not simply "make it pass":</b> the investigation that built
    /// this found two real defects — a <see cref="Bitmap"/> disposed before <c>OleFlushClipboard</c> could
    /// render it, and reads that assumed one runtime type when OLE returns three — and both are fixed at
    /// their site. What remained is environmental: 40 unpaced set+read cycles through the service lost
    /// formats repeatedly, while 30 cycles with a 120 ms gap were <b>30/30 clean</b>. Even paced, a single
    /// attempt still failed ~1 run in 15 here; three attempts put that at roughly one in three thousand.
    /// </para>
    /// <para>
    /// ⚠ <b>If this fails, do not add a fourth attempt — but READ THE FAILURE FIRST, because the two modes
    /// mean opposite things.</b> A format coming back WRONG is a defect: find it. An
    /// <c>ExternalException</c> from <c>SetDataObject</c> means the write never landed, so nothing below
    /// ever ran — and on 2026-08-16 that was measured to an OS-level condition on the dev machine, not to
    /// this code: PowerShell's own <c>Set-Clipboard</c> failed 4 times in 12 alongside it, and the failure
    /// was identical for a text-only payload, at a 2 s pace, and on a fresh STA thread. <c>TASKS.md</c>
    /// carries the full A/B table and what was already ruled out, so nobody re-runs those experiments.
    /// </para>
    /// </summary>
    private static void RoundTrip(Action<ClipboardService> body)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            // Also BEFORE the first attempt: two test methods in this class run back-to-back with no gap,
            // so the second one's write would otherwise race the first one's read.
            Settle();
            try
            {
                Sta.Run(() => body(new ClipboardService()));
                return;
            }
            catch (Exception) when (attempt < attempts)
            {
                Thread.Sleep(250 * attempt);
            }
        }
    }

    /// <summary>The gap a real copy has before anyone pastes. See the class remarks for the measurement.</summary>
    private static void Settle() => Thread.Sleep(120);

    [Fact]
    public void Every_representation_survives_ONE_copy_and_comes_back_intact()
    {
        // 🔴 THE REASON THE CONTRACT CHANGED. Setting these one at a time leaves only the last, silently
        // — so this asserts all of them came back from a single SetAsync, which the old shape could not
        // express at all.
        var png = OnePixelPng();
        var mine = new byte[] { 0x00, 0xFF, 0x10, 0x42 };
        var file = Path.Combine(Path.GetTempPath(), $"shenora-clipboard-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");

        try
        {
            RoundTrip(clipboard =>
            {
                clipboard.SetAsync(new ClipboardContent
                {
                    Text = "plain",
                    Files = [file],
                    Formats = new Dictionary<string, ReadOnlyMemory<byte>>
                    {
                        [ClipboardContent.Html] = Encoding.UTF8.GetBytes("<b>rich</b>"),
                        [ClipboardContent.PngImage] = png,
                        ["application/x-shenora-test"] = mine,
                    },
                }).GetAwaiter().GetResult();

                Settle();
                var read = clipboard.GetAsync().GetAwaiter().GetResult();

                Assert.Equal("plain", read.Text);
                Assert.Equal([file], read.Files);
                Assert.Equal("<b>rich</b>", Encoding.UTF8.GetString(read.Formats[ClipboardContent.Html].Span));

                // Byte-for-byte, not merely "decodable": round-tripping a picture through CF_BITMAP loses
                // the alpha channel, so a transparent screenshot would come back on a black background.
                // Reading the PNG format back is what avoids that, and only an exact comparison can tell
                // the two apart.
                Assert.Equal(png, read.Formats[ClipboardContent.PngImage].ToArray());

                // The owner's ask: a representation only this app understands, neither translated nor
                // validated on the way through.
                Assert.Equal(mine, read.Formats["application/x-shenora-test"].ToArray());
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Setting_empty_text_CLEARS_rather_than_throwing()
    {
        // Clipboard.SetText rejects "" — but an empty selection is app DATA, not a caller bug.
        RoundTrip(clipboard =>
        {
            clipboard.SetTextAsync("").GetAwaiter().GetResult();
            Settle();
            Assert.True(clipboard.GetAsync().GetAwaiter().GetResult().IsEmpty);
        });
    }

    /// <summary>A real 1×1 PNG, so the encode/decode path is exercised rather than stubbed.</summary>
    private static byte[] OnePixelPng()
    {
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetPixel(0, 0, Color.FromArgb(128, 10, 20, 30));
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        return buffer.ToArray();
    }
}
