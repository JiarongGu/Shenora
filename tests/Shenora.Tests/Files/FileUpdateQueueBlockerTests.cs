using Shenora.Engine.Files;

// ⚠ `Shenora.Tests.Io`, matching every sibling in this folder — NOT `Shenora.Tests.Files`, which shadows
// the kit's own `Shenora.Engine.Files.Files` static class and breaks `Files.WriteAllText(…)` in the tests
// next door.
namespace Shenora.Tests.Io;

/// <summary>
/// The three <c>FileUpdateQueue</c> defects the 2026-08-21 full review called blocking. Each is SILENT in
/// the shipped code — no exception, no log an app can act on, and a result that says it worked — so each
/// test asserts the thing the caller was told, not merely that an operation ran.
/// </summary>
public class FileUpdateQueueBlockerTests
{
    /// <summary>
    /// A filesystem that behaves like a real one where these defects live: a NON-recursive directory
    /// delete refuses a directory that still has children, which is exactly what
    /// <c>Directory.Delete(path, recursive: false)</c> does and what the shared test probe does not model.
    /// </summary>
    private sealed class Fs : IFileOperations
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Log { get; } = [];

        private bool HasChildren(string path) =>
            Files.Any(f => f.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            || Directories.Any(d => d.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        public ValueTask<bool> FileExistsAsync(string path) => ValueTask.FromResult(Files.Contains(path));
        public ValueTask<bool> DirectoryExistsAsync(string path) => ValueTask.FromResult(Directories.Contains(path));

        public ValueTask CreateDirectoryAsync(string path)
        {
            Log.Add($"mkdir {path}");
            Directories.Add(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask MoveFileAsync(string from, string to, bool overwrite)
        {
            Log.Add($"move {from} -> {to}");
            // A directory move carries its whole subtree, which is what makes the sidecar non-empty.
            foreach (var child in Files.Where(f => f.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Files.Remove(child);
                Files.Add(to + child[from.Length..]);
            }
            if (Directories.Remove(from)) Directories.Add(to);
            if (Files.Remove(from)) Files.Add(to);
            return ValueTask.CompletedTask;
        }

        public ValueTask ReplaceFileAsync(string source, string destination, string backup)
        {
            Log.Add($"replace {source} -> {destination}");
            Files.Remove(source);
            Files.Add(destination);
            Files.Add(backup);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteFileAsync(string path)
        {
            Log.Add($"delete {path}");
            Files.Remove(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteDirectoryAsync(string path, bool recursive)
        {
            Log.Add($"rmdir {path} recursive={recursive}");
            if (!recursive && HasChildren(path))
                throw new IOException($"The directory is not empty: '{path}'.");
            foreach (var child in Files.Where(f => f.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                Files.Remove(child);
            foreach (var child in Directories.Where(d => d.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                Directories.Remove(child);
            Directories.Remove(path);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryJournal : IFileUpdateJournal
    {
        public Dictionary<string, FileUpdateJournalEntry> Entries { get; } = [];

        public Task WriteAsync(FileUpdateJournalEntry entry, CancellationToken cancellationToken)
        {
            Entries[entry.UpdateId] = entry;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string updateId, CancellationToken cancellationToken)
        {
            Entries.Remove(updateId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FileUpdateJournalEntry>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FileUpdateJournalEntry>>([.. Entries.Values]);
    }

    private static string Abs(string relative) =>
        Path.Combine(Path.GetTempPath(), "shenora-blocker", relative);

    [Fact]
    public async Task AllOrNothing_delete_of_a_NON_EMPTY_directory_does_not_report_success_and_orphan_the_tree()
    {
        // 🔴 The staged delete moves the tree aside, then finishes it with an undo step whose contract is
        // "remove if still EMPTY". The tree is not empty, so the delete threw, the guard swallowed it, the
        // journal entry was removed anyway, and the caller was told the update succeeded while the whole
        // tree sat under a sidecar name that nothing would ever look at again.
        var fs = new Fs();
        var root = Abs("app");
        fs.Directories.Add(root);
        fs.Files.Add(Path.Combine(root, "payload.dll"));

        var journal = new MemoryJournal();
        var queue = new FileUpdateQueue(new FileUpdateQueueOptions { Journal = journal }, fs);

        var result = await queue.ApplyAsync(new FileUpdate
        {
            Atomicity = FileAtomicity.AllOrNothing,
            Changes = [new FileChange.Delete(root) { Recursive = true }],
        });

        Assert.True(result.Succeeded);
        // The directory and everything under it is GONE — not merely renamed.
        Assert.DoesNotContain(fs.Directories, d => d.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fs.Files, f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        // And a finished update leaves no journal entry behind.
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task A_staged_delete_that_could_not_finish_KEEPS_its_journal_entry_for_recovery()
    {
        // ⚠ The mirror of the test above, and the reason the orphan was permanent: the entry recording
        // that a deletion was still owed used to be removed even when the deletion failed, so RecoverAsync
        // could never see it again. A NON-recursive request over a non-empty tree is the honest failure —
        // the caller asked for a delete that cannot succeed, and it must not be silently upgraded.
        var fs = new Fs();
        var root = Abs("keep");
        fs.Directories.Add(root);
        fs.Files.Add(Path.Combine(root, "child.txt"));

        var journal = new MemoryJournal();
        var queue = new FileUpdateQueue(new FileUpdateQueueOptions { Journal = journal }, fs);

        await queue.ApplyAsync(new FileUpdate
        {
            Atomicity = FileAtomicity.AllOrNothing,
            Changes = [new FileChange.Delete(root) { Recursive = false }],
        });

        Assert.NotEmpty(journal.Entries);
        Assert.Equal(FileUpdateStage.Committing, journal.Entries.Values.Single().Stage);
    }

    [Fact]
    public async Task RolledBack_is_FALSE_when_the_undo_could_not_finish()
    {
        // 🔴 `rolledBack: true` was a literal while every undo step was guarded and swallowed, so a caller
        // branching on it — an installer deciding whether to retry or to warn — was told "nothing changed"
        // over a half-applied tree. That is the precise outcome AllOrNothing exists to make impossible.
        var fs = new UndoRefusingFs();
        var target = Abs("one.txt");
        var second = Abs("two.txt");
        fs.Files.Add(Abs("src-one.txt"));

        var queue = new FileUpdateQueue(new FileUpdateQueueOptions(), fs);
        var result = await queue.ApplyAsync(new FileUpdate
        {
            Atomicity = FileAtomicity.AllOrNothing,
            Changes =
            [
                new FileChange.Move(Abs("src-one.txt"), target),
                new FileChange.Move(Abs("missing.txt"), second),   // fails: source does not exist
            ],
        });

        Assert.False(result.Succeeded);
        Assert.False(result.RolledBack);
    }

    /// <summary>Applies moves, then refuses every attempt to move anything BACK — a locked original.</summary>
    private sealed class UndoRefusingFs : IFileOperations
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        private bool _applied;

        public ValueTask<bool> FileExistsAsync(string path) => ValueTask.FromResult(Files.Contains(path));
        public ValueTask<bool> DirectoryExistsAsync(string path) => ValueTask.FromResult(false);
        public ValueTask CreateDirectoryAsync(string path) => ValueTask.CompletedTask;

        public ValueTask MoveFileAsync(string from, string to, bool overwrite)
        {
            if (_applied) throw new IOException($"cannot move back to '{to}' — it is locked.");
            if (!Files.Contains(from)) throw new FileNotFoundException(from);
            Files.Remove(from);
            Files.Add(to);
            _applied = true;   // every LATER move is an undo attempt in these fixtures
            return ValueTask.CompletedTask;
        }

        public ValueTask ReplaceFileAsync(string source, string destination, string backup) => ValueTask.CompletedTask;
        public ValueTask DeleteFileAsync(string path) { Files.Remove(path); return ValueTask.CompletedTask; }
        public ValueTask DeleteDirectoryAsync(string path, bool recursive) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Leases_already_held_are_RELEASED_when_acquiring_a_later_one_throws()
    {
        // 🔴 The release loop ran only when a lease was REFUSED (returned null). A throw — an ordinary user
        // cancel, or an access error on one path of several — walked straight past every lease already
        // taken, and each is held for the life of the process.
        var locker = new ThrowingLocker(throwOnCall: 2);
        var queue = new FileUpdateQueue(
            new FileUpdateQueueOptions { Locker = locker }, new Fs());

        await Assert.ThrowsAsync<OperationCanceledException>(() => queue.ApplyAsync(new FileUpdate
        {
            Changes =
            [
                new FileChange.CreateDirectory(Abs("a")),
                new FileChange.CreateDirectory(Abs("b")),
            ],
        }));

        Assert.Equal(1, locker.Granted);
        Assert.Equal(1, locker.Released);
    }

    /// <summary>Grants leases until the Nth call, which throws — the shape a cancel takes.</summary>
    private sealed class ThrowingLocker(int throwOnCall) : IPathLocker
    {
        private int _calls;
        public int Granted { get; private set; }
        public int Released { get; set; }

        public Task<IPathLease?> TryAcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (++_calls >= throwOnCall) throw new OperationCanceledException("the caller cancelled");
            Granted++;
            return Task.FromResult<IPathLease?>(new Lease(path, this));
        }

        private sealed class Lease(string path, ThrowingLocker owner) : IPathLease
        {
            public string Path => path;
            public ValueTask DisposeAsync() { owner.Released++; return ValueTask.CompletedTask; }
        }
    }
}
