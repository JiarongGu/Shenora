namespace Shenora.Core;

/// <summary>Inputs for <see cref="FileUpdateQueue"/>.</summary>
public sealed class FileUpdateQueueOptions
{
    /// <summary>
    /// Diagnostics sink, guarded through <see cref="AppCallback.Log"/> — a throwing sink cannot take
    /// the queue down.
    /// </summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The default <see cref="IFileUpdateQueue"/>: one writer per partition, changes applied in order,
/// with compensating rollback when the update asks for it.
///
/// <para>
/// The whole component is a lock plus a switch over four change kinds. That is deliberate — the
/// hard part of this problem is deciding WHAT the semantics should be (which the design doc argues),
/// not the applying.
/// </para>
/// </summary>
public sealed class FileUpdateQueue : IFileUpdateQueue
{
    private readonly Dictionary<string, SemaphoreSlim> _partitions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly FileUpdateQueueOptions _options;
    private readonly IFileOperations _operations;

    /// <param name="options">Diagnostics. Everything else is per update.</param>
    public FileUpdateQueue(FileUpdateQueueOptions? options = null)
        : this(options, new SystemFileOperations()) { }

    /// <summary>
    /// Test seam. Kept INTERNAL rather than public because the kit deliberately ships no filesystem
    /// abstraction — apps have their own and should keep them (`docs/ADOPTION.md`). It exists so the
    /// serialization and rollback invariants can be proven with an injected probe instead of with
    /// sleeps and real disks, which is the only way those assertions are worth anything.
    /// </summary>
    internal FileUpdateQueue(FileUpdateQueueOptions? options, IFileOperations operations)
    {
        _options = options ?? new FileUpdateQueueOptions();
        _operations = operations;
    }

    /// <inheritdoc/>
    public async Task<FileUpdateResult> ApplyAsync(
        FileUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Changes);
        if (update.Changes.Count == 0)
            throw new ArgumentException("An update needs at least one change.", nameof(update));

        var gate = GateFor(update.Partition);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyLockedAsync(update).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GateFor(string? partition)
    {
        var key = partition ?? string.Empty;
        lock (_gate)
        {
            if (!_partitions.TryGetValue(key, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _partitions[key] = gate;
            }
            return gate;
        }
    }

    /// <summary>
    /// Runs with the partition held, so nothing else is mutating these paths through this queue. The
    /// cancellation token is deliberately NOT observed past this point: a half-applied set abandoned
    /// mid-way is the one outcome no caller can do anything with.
    /// </summary>
    private async Task<FileUpdateResult> ApplyLockedAsync(FileUpdate update)
    {
        var atomic = update.Atomicity == FileAtomicity.AllOrNothing;
        var undo = new List<Func<ValueTask>>();
        var staged = new List<Func<ValueTask>>();   // deletes to finish once the whole set lands

        for (var index = 0; index < update.Changes.Count; index++)
        {
            var change = update.Changes[index];
            try
            {
                await ApplyWithRetryAsync(change, atomic, undo, staged, update.Retry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(() => $"file update failed at change {index} ({change.GetType().Name}): {ex.GetType().Name}");
                if (!atomic) return new FileUpdateResult(index, index, ex, rolledBack: false);

                await RollbackAsync(undo).ConfigureAwait(false);
                return new FileUpdateResult(0, index, ex, rolledBack: true);
            }
        }

        // Only now are staged deletions real: until the last change landed, the update could still
        // have needed them back.
        foreach (var commit in staged) await Guarded(commit).ConfigureAwait(false);
        return new FileUpdateResult(update.Changes.Count, null, null, rolledBack: false);
    }

    private async ValueTask ApplyWithRetryAsync(
        FileChange change, bool atomic, List<Func<ValueTask>> undo, List<Func<ValueTask>> staged, RetryPolicy? retry)
    {
        var policy = retry ?? RetryPolicy.None;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await ApplyChangeAsync(change, atomic, undo, staged).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < policy.Attempts && policy.IsTransient(ex))
            {
                await Task.Delay(policy.Delay * attempt).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ApplyChangeAsync(
        FileChange change, bool atomic, List<Func<ValueTask>> undo, List<Func<ValueTask>> staged)
    {
        switch (change)
        {
            case FileChange.CreateDirectory create:
            {
                if (await _operations.DirectoryExistsAsync(create.Path).ConfigureAwait(false)) return;
                await _operations.CreateDirectoryAsync(create.Path).ConfigureAwait(false);
                // Only remove what we created, and only if still empty — another change may have
                // filled it, and undoing that is not this change's business.
                if (atomic) undo.Add(() => _operations.DeleteDirectoryAsync(create.Path, recursive: false));
                return;
            }

            case FileChange.Replace replace:
            {
                var existed = await _operations.FileExistsAsync(replace.TargetPath).ConfigureAwait(false);
                if (!existed)
                {
                    await _operations.MoveFileAsync(replace.TempPath, replace.TargetPath, overwrite: false)
                        .ConfigureAwait(false);
                    if (atomic) undo.Add(() => _operations.DeleteFileAsync(replace.TargetPath));
                    return;
                }

                if (!atomic)
                {
                    await _operations.MoveFileAsync(replace.TempPath, replace.TargetPath, overwrite: true)
                        .ConfigureAwait(false);
                    return;
                }

                // Keep the displaced original: that is what makes the rollback possible at all.
                var backup = SidecarPath(replace.TargetPath, "bak");
                await _operations.ReplaceFileAsync(replace.TempPath, replace.TargetPath, backup)
                    .ConfigureAwait(false);
                undo.Add(() => _operations.MoveFileAsync(backup, replace.TargetPath, overwrite: true));
                staged.Add(() => _operations.DeleteFileAsync(backup));
                return;
            }

            case FileChange.Move move:
            {
                var existed = move.Overwrite
                    && await _operations.FileExistsAsync(move.To).ConfigureAwait(false);
                if (existed && atomic)
                {
                    var backup = SidecarPath(move.To, "bak");
                    await _operations.ReplaceFileAsync(move.From, move.To, backup).ConfigureAwait(false);
                    undo.Add(async () =>
                    {
                        await _operations.MoveFileAsync(move.To, move.From, overwrite: true).ConfigureAwait(false);
                        await _operations.MoveFileAsync(backup, move.To, overwrite: true).ConfigureAwait(false);
                    });
                    staged.Add(() => _operations.DeleteFileAsync(backup));
                    return;
                }

                await _operations.MoveFileAsync(move.From, move.To, move.Overwrite).ConfigureAwait(false);
                if (atomic) undo.Add(() => _operations.MoveFileAsync(move.To, move.From, overwrite: true));
                return;
            }

            case FileChange.Delete delete:
            {
                var isFile = await _operations.FileExistsAsync(delete.Path).ConfigureAwait(false);
                var isDirectory = !isFile
                    && await _operations.DirectoryExistsAsync(delete.Path).ConfigureAwait(false);
                if (!isFile && !isDirectory) return;   // already gone is the outcome the caller wanted

                if (!atomic)
                {
                    if (isFile) await _operations.DeleteFileAsync(delete.Path).ConfigureAwait(false);
                    else await _operations.DeleteDirectoryAsync(delete.Path, delete.Recursive).ConfigureAwait(false);
                    return;
                }

                // STAGED: a delete is the one change that cannot be undone from nothing, so under
                // AllOrNothing it is a move aside now and a real delete only once everything lands.
                var aside = SidecarPath(delete.Path, "del");
                await _operations.MoveFileAsync(delete.Path, aside, overwrite: false).ConfigureAwait(false);
                undo.Add(() => _operations.MoveFileAsync(aside, delete.Path, overwrite: true));
                staged.Add(() => isFile
                    ? _operations.DeleteFileAsync(aside)
                    : _operations.DeleteDirectoryAsync(aside, recursive: true));
                return;
            }

            default:
                // The hierarchy is closed (FileChange's constructor is private), so this is only
                // reachable if a kind is added without a case here — fail loudly rather than skip it.
                throw new NotSupportedException($"Unhandled change kind '{change.GetType().Name}'.");
        }
    }

    /// <summary>
    /// Undo applied changes in REVERSE order — the only order that is correct when two changes touch
    /// the same path. Each step is guarded: a rollback that throws half way would strand the update in
    /// a state nobody can reason about, so failures are logged and the rest still runs.
    /// </summary>
    private async ValueTask RollbackAsync(List<Func<ValueTask>> undo)
    {
        for (var index = undo.Count - 1; index >= 0; index--)
            await Guarded(undo[index]).ConfigureAwait(false);
    }

    private async ValueTask Guarded(Func<ValueTask> step)
    {
        try { await step().ConfigureAwait(false); }
        catch (Exception ex) { Log(() => $"file update cleanup step failed: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// A sibling of the target, so the move is same-volume and therefore atomic and instant. A temp
    /// DIRECTORY elsewhere would silently become a cross-volume copy of the very file being replaced.
    /// </summary>
    private static string SidecarPath(string path, string tag) =>
        $"{path}.shenora-{tag}-{Guid.NewGuid():N}";

    private void Log(Func<string> message) => AppCallback.Log(_options.Log, message);
}

/// <summary>
/// The filesystem operations <see cref="FileUpdateQueue"/> performs. Internal: the kit ships no
/// filesystem abstraction as public surface, and this exists so the queue's invariants are testable.
/// </summary>
internal interface IFileOperations
{
    ValueTask<bool> FileExistsAsync(string path);
    ValueTask<bool> DirectoryExistsAsync(string path);
    ValueTask CreateDirectoryAsync(string path);
    ValueTask MoveFileAsync(string from, string to, bool overwrite);
    ValueTask ReplaceFileAsync(string source, string destination, string backup);
    ValueTask DeleteFileAsync(string path);
    ValueTask DeleteDirectoryAsync(string path, bool recursive);
}

/// <summary>The real thing. Synchronous underneath — these are metadata operations on one volume.</summary>
internal sealed class SystemFileOperations : IFileOperations
{
    public ValueTask<bool> FileExistsAsync(string path) => ValueTask.FromResult(File.Exists(path));

    public ValueTask<bool> DirectoryExistsAsync(string path) => ValueTask.FromResult(Directory.Exists(path));

    public ValueTask CreateDirectoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask MoveFileAsync(string from, string to, bool overwrite)
    {
        // A directory move and a file move are the same intent here; Directory.Move has no overwrite,
        // and an existing destination directory is a genuine conflict rather than something to clobber.
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReplaceFileAsync(string source, string destination, string backup)
    {
        File.Replace(source, destination, backup);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteFileAsync(string path)
    {
        File.Delete(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteDirectoryAsync(string path, bool recursive)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive);
        return ValueTask.CompletedTask;
    }
}
