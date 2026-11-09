using System.Diagnostics;
using System.Text;
using Shenora.Chromium.Host;
using Shenora.Core.Shell;

namespace Shenora.Tests.Linux;

/// <summary>
/// What the Linux clipboard does as the app goes (<c>LinuxClipboard.Dispose</c>): hand its copy to the clipboard manager
/// through <c>SAVE_TARGETS</c>, against a scripted manager (<see cref="ScriptedClipboardManager"/>) on the display these
/// run on, which is a private Xvfb one (<c>dev.mjs linux-tests</c>). The clipboard is the X server's CLIPBOARD, so the
/// cases run one at a time and each starts with the display's clipboard unowned.
/// </summary>
public class LinuxClipboardHandoverTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private const string Text = "kept past exit — 日本語";

    private static ClipboardContent Copy() => new()
    {
        Text = Text,
        Formats = new Dictionary<string, ReadOnlyMemory<byte>> { [ClipboardContent.PngImage] = Png },
    };

    [Fact]
    public async Task A_copy_is_kept_by_the_manager_and_outlives_the_app()
    {
        using var manager = new ScriptedClipboardManager(ScriptedClipboardManager.Behaviour.Keep);
        var app = new LinuxClipboard();
        await app.SetAsync(Copy());

        var watch = Stopwatch.StartNew();
        app.Dispose();
        watch.Stop();

        Assert.Equal(1, manager.Requests);
        Assert.Contains("UTF8_STRING", manager.Asked);
        Assert.Contains(ClipboardContent.PngImage, manager.Asked);
        Assert.Equal(Encoding.UTF8.GetBytes(Text), manager.Kept["UTF8_STRING"]);
        Assert.Equal(Png, manager.Kept[ClipboardContent.PngImage]);
        Assert.True(watch.Elapsed < LinuxClipboard.HandoverLimit, $"the exit waited {watch.Elapsed}");

        // The app is gone and the clipboard still holds the copy: another app pastes it from the manager.
        using var other = new LinuxClipboard();
        var pasted = await other.GetAsync();
        Assert.Equal(Text, pasted.Text);
        Assert.Equal(Png, pasted.Formats[ClipboardContent.PngImage].ToArray());
    }

    [Fact]
    public async Task With_no_manager_the_exit_does_not_wait()
    {
        var app = new LinuxClipboard();
        await app.SetAsync(Copy());

        var watch = Stopwatch.StartNew();
        app.Dispose();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"the exit waited {watch.Elapsed}");
        using var other = new LinuxClipboard();
        Assert.True((await other.GetAsync()).IsEmpty, "the copy went with the app, as it did before the handover");
    }

    [Fact]
    public async Task A_manager_that_never_answers_holds_the_exit_for_the_limit_and_no_longer()
    {
        using var manager = new ScriptedClipboardManager(ScriptedClipboardManager.Behaviour.Ignore);
        var limit = TimeSpan.FromSeconds(1);
        var app = new LinuxClipboard("CLIPBOARD", null, limit);
        await app.SetAsync(Copy());

        var watch = Stopwatch.StartNew();
        app.Dispose();
        watch.Stop();

        Assert.Equal(1, manager.Requests);
        Assert.InRange(watch.Elapsed, limit - TimeSpan.FromMilliseconds(100), limit + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_manager_that_refuses_does_not_hold_the_exit()
    {
        using var manager = new ScriptedClipboardManager(ScriptedClipboardManager.Behaviour.Refuse);
        var app = new LinuxClipboard();
        await app.SetAsync(Copy());

        var watch = Stopwatch.StartNew();
        app.Dispose();

        Assert.Equal(1, manager.Requests);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"the exit waited {watch.Elapsed}");
        Assert.Empty(manager.Kept);
    }

    [Fact]
    public async Task A_clipboard_the_app_cleared_or_never_used_hands_over_nothing()
    {
        using var manager = new ScriptedClipboardManager(ScriptedClipboardManager.Behaviour.Keep);

        new LinuxClipboard().Dispose();   // never connected

        var cleared = new LinuxClipboard();
        await cleared.SetAsync(Copy());
        await cleared.ClearAsync();
        cleared.Dispose();

        Assert.Equal(0, manager.Requests);
    }

    [Fact]
    public async Task A_selection_of_another_name_is_not_the_clipboard_and_is_not_handed_over()
    {
        using var manager = new ScriptedClipboardManager(ScriptedClipboardManager.Behaviour.Keep);
        var app = new LinuxClipboard("SHENORA_TEST_SELECTION", null);
        await app.SetAsync(Copy());

        app.Dispose();

        Assert.Equal(0, manager.Requests);
    }
}
