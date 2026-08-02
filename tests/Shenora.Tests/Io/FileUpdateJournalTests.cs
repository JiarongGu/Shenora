using Shenora.Core;

namespace Shenora.Tests.Io;

/// <summary>
/// Crash-atomicity: <see cref="FileAtomicity.AllOrNothing"/> surviving the process DYING, not merely
/// a change failing.
///
/// <para>
/// <b>Simulating a crash needed more care than it first appeared.</b> The obvious version — make a
/// change throw and assert on what is left — tests nothing: the queue CATCHES failures by contract
/// and, under AllOrNothing, rolls back in-process. That is the opposite of a crash, where no cleanup
/// code runs at all. So these tests FREEZE the world at the moment of death: at the chosen operation
/// the double snapshots the managed files and the journal exactly as they stand, and the test then
/// restores that snapshot before recovering with a BRAND NEW queue. Nothing is carried over in
/// memory, because in a real crash nothing is.
/// </para>
/// </summary>
public class FileUpdateJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shenora-journal", Path.GetRandomFileName());

    public FileUpdateJournalTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(JournalDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        try { Directory.Delete(FrozenDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string JournalDir => Path.Combine(_root, "journal");

    private FileUpdateJournal NewJournal() =>
        new(new FileUpdateJournalOptions { Directory = JournalDir });

    private FileUpdateQueue NewQueue(IFileOperations? operations = null) =>
        operations is null
            ? new FileUpdateQueue(new FileUpdateQueueOptions { Journal = NewJournal() })
            : new FileUpdateQueue(new FileUpdateQueueOptions { Journal = NewJournal() }, operations);

    /// <summary>
    /// Freezes the world at a chosen operation and then fails, standing in for a process that simply
    /// stops. The snapshot is the point: what the disk looked like at that instant is what a restarted
    /// process would find, and it is NOT what this process leaves behind after its own rollback runs.
    /// </summary>
    /// <param name="after">
    /// When true the operation is PERFORMED and the process dies immediately afterwards. That is the
    /// case that separates a write-ahead journal from a write-after one: the change is on disk and
    /// the plan to undo it is only in the dead process's memory.
    /// </param>
    private sealed class CrashingOperations(
        IFileOperations inner, Func<string, bool> crashOnWriteTo, Action snapshot, bool after = false) : IFileOperations
    {
        public ValueTask<bool> FileExistsAsync(string path) => inner.FileExistsAsync(path);
        public ValueTask<bool> DirectoryExistsAsync(string path) => inner.DirectoryExistsAsync(path);
        public ValueTask CreateDirectoryAsync(string path) => inner.CreateDirectoryAsync(path);
        public ValueTask DeleteFileAsync(string path) => inner.DeleteFileAsync(path);
        public ValueTask DeleteDirectoryAsync(string path, bool recursive) => inner.DeleteDirectoryAsync(path, recursive);

        // BOTH write paths, not just the move: replacing a file that already exists goes through
        // ReplaceFileAsync, and hooking only MoveFileAsync meant the "crash" silently never fired.
        public async ValueTask MoveFileAsync(string from, string to, bool overwrite)
        {
            if (!crashOnWriteTo(to)) { await inner.MoveFileAsync(from, to, overwrite); return; }
            if (after) await inner.MoveFileAsync(from, to, overwrite);
            snapshot();
            throw new PowerCut();
        }

        public async ValueTask ReplaceFileAsync(string source, string destination, string backup)
        {
            if (!crashOnWriteTo(destination)) { await inner.ReplaceFileAsync(source, destination, backup); return; }
            if (after) await inner.ReplaceFileAsync(source, destination, backup);
            snapshot();
            throw new PowerCut();
        }
    }

    private sealed class PowerCut : Exception;

    private string FrozenDir => Path.Combine(Path.GetTempPath(), "shenora-frozen", Path.GetFileName(_root));

    /// <summary>Copy the whole world aside — the freeze-frame a power cut would leave.</summary>
    private void Freeze() => CopyTree(_root, FrozenDir);

    /// <summary>Put that world back, discarding everything the dying process did afterwards.</summary>
    private void Thaw()
    {
        Directory.Delete(_root, recursive: true);
        CopyTree(FrozenDir, _root);
    }

    // Manual recursion rather than a helper: this machine's Node fs.cpSync crashes, and the C# BCL
    // has no directory copy either.
    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    [Fact]
    public async Task A_new_process_rolls_back_an_update_the_old_one_died_inside()
    {
        var one = Path.Combine(_root, "one.txt");
        var two = Path.Combine(_root, "two.txt");
        await File.WriteAllTextAsync(one, "ORIGINAL one");
        await File.WriteAllTextAsync(two, "ORIGINAL two");
        var tempOne = Path.Combine(_root, "one.tmp");
        var tempTwo = Path.Combine(_root, "two.tmp");
        await File.WriteAllTextAsync(tempOne, "NEW one");
        await File.WriteAllTextAsync(tempTwo, "NEW two");

        // The doomed run: replaces one.txt, then dies replacing two.txt.
        var doomed = NewQueue(new CrashingOperations(new SystemFileOperations(), to => to == two, Freeze));
        await doomed.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace(tempOne, one), new FileChange.Replace(tempTwo, two)],
            Atomicity = FileAtomicity.AllOrNothing,
        });

        // Back to the instant of death, discarding the rollback this process went on to do — a real
        // crash runs no cleanup, and recovery has to work from the disk alone.
        Thaw();
        Assert.Equal("NEW one", await File.ReadAllTextAsync(one));   // torn: the first change landed
        Assert.Single(Directory.GetFiles(JournalDir, "*.journal"));

        // A COMPLETELY NEW queue — the restarted process — finds the journal and finishes the job.
        var restarted = NewQueue();
        Assert.Equal(1, await restarted.RecoverAsync());

        Assert.Equal("ORIGINAL one", await File.ReadAllTextAsync(one));   // rolled back
        Assert.Equal("ORIGINAL two", await File.ReadAllTextAsync(two));
        Assert.Empty(Directory.GetFiles(JournalDir, "*.journal"));        // and the journal is clear
    }

    [Fact]
    public async Task A_change_that_LANDED_before_the_crash_is_still_undone()
    {
        // THE test for write-ahead ordering, and the reason the others are not. Here the replace
        // really happens and the process dies immediately after — so the change is on disk while the
        // plan to undo it exists only in the dead process's memory. It is recoverable ONLY because
        // the journal was written BEFORE the mutation. Journalling afterwards leaves the new content
        // in place with an orphaned backup beside it, and no way to tell.
        var target = Path.Combine(_root, "landed.txt");
        var temp = Path.Combine(_root, "landed.tmp");
        await File.WriteAllTextAsync(target, "ORIGINAL");
        await File.WriteAllTextAsync(temp, "NEW");

        var doomed = NewQueue(new CrashingOperations(
            new SystemFileOperations(), to => to == target, Freeze, after: true));
        await doomed.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace(temp, target)],
            Atomicity = FileAtomicity.AllOrNothing,
        });

        Thaw();
        Assert.Equal("NEW", await File.ReadAllTextAsync(target));   // it landed, then everything stopped

        Assert.Equal(1, await NewQueue().RecoverAsync());
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_root, "*.shenora-bak-*"));   // and no orphaned backup left
    }

    [Fact]
    public async Task A_deleted_file_comes_back_after_a_crash()
    {
        // The staged delete is what makes this possible at all: the content was moved aside, not gone.
        var doomed = Path.Combine(_root, "doomed.txt");
        var target = Path.Combine(_root, "target.txt");
        var temp = Path.Combine(_root, "target.tmp");
        await File.WriteAllTextAsync(doomed, "PRECIOUS");
        await File.WriteAllTextAsync(temp, "new");

        var crashing = NewQueue(new CrashingOperations(new SystemFileOperations(), to => to == target, Freeze));
        await crashing.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Delete(doomed), new FileChange.Replace(temp, target)],
            Atomicity = FileAtomicity.AllOrNothing,
        });
        Thaw();
        Assert.False(File.Exists(doomed), "mid-crash, the file is aside and not yet back");

        Assert.Equal(1, await NewQueue().RecoverAsync());
        Assert.Equal("PRECIOUS", await File.ReadAllTextAsync(doomed));
    }

    [Fact]
    public async Task Recovery_FINISHES_an_update_that_had_already_landed()
    {
        // The stage marker earns its keep here: rolling this back would undo a SUCCESS. The entry is
        // written by hand because the window it describes — every change applied, staged deletions
        // not yet finished — is a few microseconds wide in a real run.
        var aside = Path.Combine(_root, "gone.txt.shenora-del-abc");
        await File.WriteAllTextAsync(aside, "already deleted, just not swept");

        var journal = NewJournal();
        await journal.WriteAsync(new FileUpdateJournalEntry(
            "u-committing",
            FileUpdateStage.Committing,
            [new FileUndoStep(FileUndoKind.MoveBack, Path.Combine(_root, "gone.txt"), aside)],
            [new FileUndoStep(FileUndoKind.DeleteCreatedFile, aside)],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(1, await NewQueue().RecoverAsync());

        Assert.False(File.Exists(aside), "the staged deletion should have been finished");
        Assert.False(File.Exists(Path.Combine(_root, "gone.txt")), "and NOT restored — the update had landed");
    }

    [Fact]
    public async Task Recovery_is_safe_to_run_twice()
    {
        // After a crash an undo step cannot assume the change it undoes ever happened, so every step
        // checks the world first — which also makes a second recovery a no-op instead of damage.
        var target = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(target, "content");

        var journal = NewJournal();
        await journal.WriteAsync(new FileUpdateJournalEntry(
            "u-twice",
            FileUpdateStage.Applying,
            [new FileUndoStep(FileUndoKind.RestoreBackup, target, Path.Combine(_root, "missing.bak"))],
            [],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(1, await NewQueue().RecoverAsync());
        Assert.Equal(0, await NewQueue().RecoverAsync());
        Assert.Equal("content", await File.ReadAllTextAsync(target));   // untouched by the missing backup
    }

    [Fact]
    public async Task A_successful_update_leaves_no_journal_behind()
    {
        // A journal that only grows is a disk leak with extra steps.
        var target = Path.Combine(_root, "clean.txt");
        var temp = Path.Combine(_root, "clean.tmp");
        await File.WriteAllTextAsync(temp, "content");

        var result = await NewQueue().ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace(temp, target)],
            Atomicity = FileAtomicity.AllOrNothing,
        });

        Assert.True(result.Succeeded);
        Assert.Empty(Directory.GetFiles(JournalDir, "*.journal"));
    }

    [Fact]
    public async Task PerChange_updates_are_not_journalled_at_all()
    {
        // PerChange promises nothing about a crash, so paying for a journal would buy nothing.
        var target = Path.Combine(_root, "per-change.txt");
        var temp = Path.Combine(_root, "per-change.tmp");
        await File.WriteAllTextAsync(temp, "content");

        var crashing = NewQueue(new CrashingOperations(new SystemFileOperations(), to => to == target, Freeze));
        await crashing.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace(temp, target)],
        });
        Thaw();

        Assert.Empty(Directory.GetFiles(JournalDir, "*.journal"));
        Assert.Equal(0, await NewQueue().RecoverAsync());
    }

    [Fact]
    public async Task An_unreadable_entry_is_skipped_rather_than_stopping_recovery()
    {
        // One torn file must not strand every other interrupted update.
        await File.WriteAllTextAsync(Path.Combine(JournalDir, "torn.journal"), "{ not json");

        var target = Path.Combine(_root, "real.txt");
        await File.WriteAllTextAsync(target, "content");
        await NewJournal().WriteAsync(new FileUpdateJournalEntry(
            "u-good", FileUpdateStage.Applying,
            [new FileUndoStep(FileUndoKind.DeleteCreatedFile, target)], [],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(1, await NewQueue().RecoverAsync());
        Assert.False(File.Exists(target), "the readable entry was still recovered");
    }

    [Fact]
    public async Task Recovery_without_a_journal_configured_is_a_no_op()
    {
        Assert.Equal(0, await new FileUpdateQueue().RecoverAsync());
    }
}
