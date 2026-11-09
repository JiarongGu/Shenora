using Shenora.Chromium.Host;

namespace Shenora.Tests.Linux;

/// <summary>
/// A <c>LinuxClipboard</c> whose connection fails part-way (the server stops answering between the connect and the first
/// atom), on the display these run on (<c>dev.mjs linux-tests</c>): it must hand back what it opened, since the next call
/// connects afresh and would otherwise leak again. The server's dying at that moment is made by the seam
/// <c>beforeAtom</c>, which throws as the failed reply does.
/// </summary>
public class LinuxClipboardStartTests
{
    [Fact]
    public async Task A_start_that_fails_after_the_connect_closes_what_it_opened_and_the_next_call_starts_afresh()
    {
        var failing = true;
        var asked = 0;
        using var clipboard = new LinuxClipboard("CLIPBOARD", null, beforeAtom: name =>
        {
            if (name == "INCR") asked++;
            if (failing && name == "INCR") throw new InvalidOperationException("The X server would not name 'INCR'.");
        });
        // The first call's own connection is what is measured, so the runtime's lazily opened descriptors are already open.
        Assert.Throws<InvalidOperationException>(() => { _ = clipboard.GetTextAsync(); });
        var before = Descriptors();

        for (var i = 0; i < 3; i++)
            Assert.Throws<InvalidOperationException>(() => { _ = clipboard.GetTextAsync(); });

        Assert.Equal(4, asked);
        var leaked = Descriptors().Except(before).ToList();
        Assert.True(leaked.Count == 0, $"{leaked.Count} descriptors stayed open across three failed starts: {string.Join(", ", leaked)}");

        failing = false;
        await clipboard.ClearAsync();   // the next call connects, and works
    }

    // The descriptors this process holds, by number.
    private static HashSet<string> Descriptors() => Directory.GetFileSystemEntries("/proc/self/fd").Select(Path.GetFileName).OfType<string>().ToHashSet();
}
