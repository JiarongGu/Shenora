using Shenora.Core;

namespace Shenora.Tests.Io;

/// <summary>
/// The file-update queue (D30).
///
/// <para>
/// Same standard as the mission concurrency suite: prove BOTH halves in one run. A queue that
/// serializes everything trivially passes "same partition never overlaps"; one that serializes
/// nothing passes "different partitions overlap". Only asserting both at once says anything.
/// </para>
///
/// <para>
/// These use the internal <c>IFileOperations</c> seam rather than a real disk. That is not a
/// shortcut: overlap and rollback ORDER are the properties under test, and with real files they
/// could only be probed with sleeps — which is how a concurrency test becomes a flaky test that gets
/// deleted. The system implementation is a thin, separately-obvious wrapper over File/Directory.
/// </para>
/// </summary>
public class FileUpdateQueueTests
{
    /// <summary>Records what happened, in order, and lets a test hold one operation open.</summary>
    private sealed class OperationProbe : IFileOperations
    {
        private readonly object _gate = new();
        private int _active;

        public List<string> Log { get; } = [];
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MaxConcurrent { get; private set; }
        public TaskCompletionSource? Hold { get; set; }
        public Func<string, bool>? FailMoveTo { get; set; }

        private async ValueTask EnterAsync(string entry)
        {
            lock (_gate)
            {
                Log.Add(entry);
                _active++;
                MaxConcurrent = Math.Max(MaxConcurrent, _active);
            }
            if (Hold is { } hold) await hold.Task.ConfigureAwait(false);
            lock (_gate) _active--;
        }

        public ValueTask<bool> FileExistsAsync(string path) => ValueTask.FromResult(Files.Contains(path));
        public ValueTask<bool> DirectoryExistsAsync(string path) => ValueTask.FromResult(Directories.Contains(path));

        public async ValueTask CreateDirectoryAsync(string path)
        {
            await EnterAsync($"mkdir {path}");
            Directories.Add(path);
        }

        public async ValueTask MoveFileAsync(string from, string to, bool overwrite)
        {
            await EnterAsync($"move {from} -> {to}");
            if (FailMoveTo?.Invoke(to) == true) throw new IOException($"target locked: {to}");
            Files.Remove(from);
            Files.Add(to);
        }

        public async ValueTask ReplaceFileAsync(string source, string destination, string backup)
        {
            await EnterAsync($"replace {source} -> {destination} (backup {backup})");
            Files.Remove(source);
            Files.Add(destination);
            Files.Add(backup);
        }

        public async ValueTask DeleteFileAsync(string path)
        {
            await EnterAsync($"delete {path}");
            Files.Remove(path);
        }

        public async ValueTask DeleteDirectoryAsync(string path, bool recursive)
        {
            await EnterAsync($"rmdir {path}");
            Directories.Remove(path);
        }
    }

    private static FileUpdate Update(params FileChange[] changes) => new() { Changes = changes };

    [Fact]
    public async Task Same_partition_serializes_while_different_partitions_overlap()
    {
        // Both halves, one run — see the class remarks.
        var probe = new OperationProbe { Hold = new TaskCompletionSource() };
        var queue = new FileUpdateQueue(null, probe);

        var first = queue.ApplyAsync(Update(new FileChange.CreateDirectory("a")));
        while (probe.Log.Count == 0) await Task.Delay(5);

        // Same partition: must NOT start while the first is held.
        var blocked = queue.ApplyAsync(Update(new FileChange.CreateDirectory("b")));
        // Different partition: must start anyway.
        var parallel = queue.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.CreateDirectory("c")],
            Partition = "other",
        });

        while (probe.MaxConcurrent < 2) await Task.Delay(5);
        Assert.Equal(2, probe.MaxConcurrent);                        // the other partition got in
        Assert.DoesNotContain("mkdir b", probe.Log);                 // the same partition did not

        probe.Hold.SetResult();
        probe.Hold = null;
        await Task.WhenAll(first, blocked, parallel);
        Assert.Contains("mkdir b", probe.Log);
    }

    [Fact]
    public async Task PerChange_stops_at_the_failure_and_leaves_earlier_changes_applied()
    {
        var probe = new OperationProbe { FailMoveTo = to => to == "two" };
        probe.Files.Add("temp1");
        probe.Files.Add("temp2");
        var queue = new FileUpdateQueue(null, probe);

        var result = await queue.ApplyAsync(Update(
            new FileChange.Replace("temp1", "one"),
            new FileChange.Replace("temp2", "two"),
            new FileChange.CreateDirectory("never")));

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedIndex);          // names WHERE it stopped
        Assert.Equal(1, result.Applied);
        Assert.False(result.RolledBack);
        Assert.Contains("one", probe.Files);          // the first change stands
        Assert.DoesNotContain("never", probe.Directories);
        Assert.IsType<IOException>(result.Error);
    }

    [Fact]
    public async Task AllOrNothing_undoes_applied_changes_in_reverse()
    {
        var probe = new OperationProbe { FailMoveTo = to => to == "two" };
        probe.Files.Add("temp1");
        probe.Files.Add("temp2");
        var queue = new FileUpdateQueue(null, probe);

        var result = await queue.ApplyAsync(new FileUpdate
        {
            Changes =
            [
                new FileChange.CreateDirectory("dir"),
                new FileChange.Replace("temp1", "one"),
                new FileChange.Replace("temp2", "two"),
            ],
            Atomicity = FileAtomicity.AllOrNothing,
        });

        Assert.True(result.RolledBack);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.FailedIndex);
        // Reverse order is the only correct one when two changes touch the same path.
        var undo = probe.Log.SkipWhile(entry => entry != "move temp2 -> two").Skip(1).ToList();
        Assert.Equal(["delete one", "rmdir dir"], undo);
        Assert.DoesNotContain("one", probe.Files);
        Assert.DoesNotContain("dir", probe.Directories);
    }

    [Fact]
    public async Task AllOrNothing_stages_a_delete_and_only_finishes_it_once_everything_lands()
    {
        var probe = new OperationProbe();
        probe.Files.Add("doomed");
        probe.Files.Add("temp");
        var queue = new FileUpdateQueue(null, probe);

        var result = await queue.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Delete("doomed"), new FileChange.Replace("temp", "target")],
            Atomicity = FileAtomicity.AllOrNothing,
        });

        Assert.True(result.Succeeded);
        // Moved aside first…
        var staged = Assert.Single(
            probe.Log, e => e.StartsWith("move doomed -> doomed.shenora-del-", StringComparison.Ordinal));
        // …and only really deleted after the LAST change landed.
        var stagedPath = staged["move doomed -> ".Length..];
        Assert.True(probe.Log.IndexOf($"delete {stagedPath}") > probe.Log.IndexOf("move temp -> target"));
        Assert.DoesNotContain("doomed", probe.Files);
    }

    [Fact]
    public async Task A_staged_delete_comes_back_when_a_later_change_fails()
    {
        // The reason a delete is staged at all: without it, AllOrNothing could not undo one.
        var probe = new OperationProbe { FailMoveTo = to => to == "target" };
        probe.Files.Add("doomed");
        probe.Files.Add("temp");
        var queue = new FileUpdateQueue(null, probe);

        var result = await queue.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Delete("doomed"), new FileChange.Replace("temp", "target")],
            Atomicity = FileAtomicity.AllOrNothing,
        });

        Assert.True(result.RolledBack);
        Assert.Contains("doomed", probe.Files);   // back where it was
    }

    [Fact]
    public async Task A_transient_failure_is_retried_per_change()
    {
        var attempts = 0;
        var probe = new OperationProbe { FailMoveTo = _ => ++attempts < 3 };
        probe.Files.Add("temp");
        var queue = new FileUpdateQueue(null, probe);

        var result = await queue.ApplyAsync(new FileUpdate
        {
            Changes = [new FileChange.Replace("temp", "target")],
            Retry = new RetryPolicy { Attempts = 3, Delay = TimeSpan.FromMilliseconds(5) },
        });

        Assert.True(result.Succeeded);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Deleting_something_already_gone_is_the_outcome_the_caller_wanted()
    {
        var probe = new OperationProbe();
        var queue = new FileUpdateQueue(null, probe);

        var result = await queue.ApplyAsync(Update(new FileChange.Delete("absent")));

        Assert.True(result.Succeeded);
        Assert.Empty(probe.Log);
    }

    [Fact]
    public async Task An_empty_update_is_a_caller_bug()
    {
        var queue = new FileUpdateQueue();
        await Assert.ThrowsAsync<ArgumentException>(() => queue.ApplyAsync(new FileUpdate { Changes = [] }));
    }
}
