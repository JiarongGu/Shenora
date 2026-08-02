using Shenora.Core;
using Shenora.Windows;

namespace Shenora.Tests.Io;

/// <summary>
/// Cross-process leases and the "who holds it" inspector — the two halves of the locking story, which
/// answer different questions and must not be confused for each other.
///
/// <para>
/// These touch the real filesystem on purpose. A lease IS an OS file handle: a fake would be testing
/// the fake. The inspector likewise — the only way to know Restart Manager works is to hold a real
/// handle and ask.
/// </para>
/// </summary>
public class PathLockTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shenora-locks", Path.GetRandomFileName());

    public PathLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* held; the OS will */ }
        GC.SuppressFinalize(this);
    }

    private FilePathLocker NewLocker() => new(new FilePathLockerOptions
    {
        LockDirectory = Path.Combine(_root, "locks"),
        PollInterval = TimeSpan.FromMilliseconds(5),
    });

    [Fact]
    public async Task A_lease_excludes_a_second_acquirer_until_it_is_released()
    {
        var locker = NewLocker();
        var target = Path.Combine(_root, "asset.dds");

        var first = await locker.TryAcquireAsync(target, TimeSpan.Zero);
        Assert.NotNull(first);

        // Zero timeout = try once. Null is the normal "someone else has it" answer, not an error.
        Assert.Null(await locker.TryAcquireAsync(target, TimeSpan.Zero));

        await first!.DisposeAsync();
        var second = await locker.TryAcquireAsync(target, TimeSpan.Zero);
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task Two_spellings_of_one_path_are_one_lease()
    {
        // The bug this prevents: `data\mods\..\mods\x` and `data/mods/x` both "locked", independently.
        var locker = NewLocker();
        var direct = Path.Combine(_root, "mods", "x.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(direct)!);
        var roundabout = Path.Combine(_root, "mods", "..", "mods", "x.dds");

        var held = await locker.TryAcquireAsync(direct, TimeSpan.Zero);
        Assert.NotNull(held);
        Assert.Null(await locker.TryAcquireAsync(roundabout, TimeSpan.Zero));
        await held!.DisposeAsync();
    }

    [Fact]
    public async Task Disjoint_paths_do_not_block_each_other()
    {
        var locker = NewLocker();
        var one = await locker.TryAcquireAsync(Path.Combine(_root, "a.dds"), TimeSpan.Zero);
        var two = await locker.TryAcquireAsync(Path.Combine(_root, "b.dds"), TimeSpan.Zero);

        Assert.NotNull(one);
        Assert.NotNull(two);
        await one!.DisposeAsync();
        await two!.DisposeAsync();
    }

    [Fact]
    public async Task A_waiting_acquirer_gets_the_lease_when_the_holder_releases()
    {
        var locker = NewLocker();
        var target = Path.Combine(_root, "contended.dds");
        var held = await locker.TryAcquireAsync(target, TimeSpan.Zero);

        var waiting = locker.TryAcquireAsync(target, TimeSpan.FromSeconds(5));
        Assert.False(waiting.IsCompleted, "it must actually wait, not fail fast");

        await held!.DisposeAsync();
        var acquired = await waiting;
        Assert.NotNull(acquired);
        await acquired!.DisposeAsync();
    }

    [Fact]
    public async Task Lock_files_never_land_in_the_tree_being_locked()
    {
        // The app does not necessarily OWN the folder it manages — sidecar locks would be litter in
        // someone else's tree, and would get synced, committed, and outlive the process.
        var locker = NewLocker();
        var managed = Path.Combine(_root, "managed");
        Directory.CreateDirectory(managed);
        var target = Path.Combine(managed, "asset.dds");

        var lease = await locker.TryAcquireAsync(target, TimeSpan.Zero);
        Assert.Empty(Directory.GetFileSystemEntries(managed));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "locks")));
        await lease!.DisposeAsync();
    }

    [Fact]
    public async Task The_queue_defers_an_update_whose_path_another_holder_has()
    {
        var locker = NewLocker();
        var target = Path.Combine(_root, "target.dds");
        var temp = Path.Combine(_root, "target.tmp");
        await File.WriteAllTextAsync(temp, "new");

        var queue = new FileUpdateQueue(new FileUpdateQueueOptions
        {
            Locker = locker,
            LeaseTimeout = TimeSpan.FromMilliseconds(100),   // do not hang the suite
        });

        var blocker = await locker.TryAcquireAsync(target, TimeSpan.Zero);
        var result = await queue.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace(temp, target)],
        });

        Assert.False(result.Succeeded);
        Assert.IsType<IOException>(result.Error);
        Assert.False(File.Exists(target));   // nothing was touched
        Assert.True(File.Exists(temp));      // including the staged file

        await blocker!.DisposeAsync();
        Assert.True((await queue.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace(temp, target)],
        })).Succeeded);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void The_inspector_names_a_process_that_really_holds_the_file()
    {
        // The whole point of the inspector: turn "the process cannot access the file" into a NAME.
        // Proven against a real handle, because the only way to test Restart Manager is to use it.
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(_root, "held.dds");
        File.WriteAllText(target, "content");
        var inspector = new RestartManagerLockInspector();

        Assert.Empty(inspector.WhoHolds(target));   // nothing holds it yet

        using (var _ = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var holders = inspector.WhoHolds(target);
            Assert.Contains(holders, holder => holder.ProcessId == Environment.ProcessId);
        }

        Assert.Empty(inspector.WhoHolds(target));   // and lets go
    }

    [Fact]
    public void The_inspector_is_a_diagnostic_and_never_throws()
    {
        if (!OperatingSystem.IsWindows()) return;
        var inspector = new RestartManagerLockInspector();

        // A path that cannot exist, and one on a share that is not there: both answer "cannot tell".
        Assert.Empty(inspector.WhoHolds(Path.Combine(_root, "nope", "missing.dds")));
        Assert.Empty(inspector.WhoHolds(@"\\no-such-host\share\file.dds"));
    }
}
