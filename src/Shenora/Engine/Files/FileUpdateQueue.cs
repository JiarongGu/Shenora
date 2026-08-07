using Shenora.Engine.Missions;
using Shenora;

namespace Shenora.Engine.Files;

/// <summary>Inputs for <see cref="FileUpdateQueue"/>.</summary>
public sealed class FileUpdateQueueOptions
{
    /// <summary>
    /// Optional cross-process exclusion. Supply one and the queue takes a lease on every path an
    /// update touches before applying it, releasing them after — so the app's OWN second process, or
    /// a child process it spawns while holding leases itself, cannot interleave with an update.
    ///
    /// <para>
    /// Null (the default) means in-process serialization only, which is all a single-instance app
    /// needs. It buys nothing against a process that does not take leases; see
    /// <see cref="LockInspector"/> for that half.
    /// </para>
    /// </summary>
    public IPathLocker? Locker { get; set; }

    /// <summary>
    /// How long to wait for leases before giving up on an update. Default 30s. Ignored without a
    /// <see cref="Locker"/>.
    /// </summary>
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional. When a change fails, the queue asks this who is holding the path and reports it in
    /// <see cref="FileUpdateResult.Holders"/> — turning "the process cannot access the file" into
    /// something an app can act on or show a user.
    /// </summary>
    public IFileLockInspector? LockInspector { get; set; }

    /// <summary>
    /// Write-ahead journal, which is what makes <see cref="FileAtomicity.AllOrNothing"/> survive the
    /// process DYING rather than merely failing. Null (the default) means rollback is in-memory only:
    /// correct for a failed change, absent after a power cut.
    ///
    /// <para>
    /// Only <see cref="FileAtomicity.AllOrNothing"/> updates are journalled. A
    /// <see cref="FileAtomicity.PerChange"/> update promises nothing about a crash, so writing a file
    /// per update to guarantee something nobody asked for would be pure cost.
    /// </para>
    ///
    /// <para>
    /// Supplying one means calling <see cref="FileUpdateQueue.RecoverAsync"/> at startup. A journal
    /// nobody replays is a directory that fills up.
    /// </para>
    /// </summary>
    public IFileUpdateJournal? Journal { get; set; }

    /// <summary>
    /// Diagnostics sink, guarded through <see cref="AppCallback.Log"/> — a throwing sink cannot take
    /// the queue down.
    /// </summary>
    public Action<string>? Log { get; set; }
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
            // Leases come AFTER the in-process gate, never before: taking a cross-process lock while
            // another thread of this same process already holds the partition would be waiting on
            // ourselves through the filesystem.
            var leases = await AcquireLeasesAsync(update, cancellationToken).ConfigureAwait(false);
            if (leases is null)
            {
                var contested = PathsOf(update).First();
                var error = new IOException(
                    $"another process holds a lease on '{contested}' (waited {_options.LeaseTimeout}).");
                return new FileUpdateResult(0, 0, error, rolledBack: false, HoldersOf(contested));
            }

            try
            {
                return await ApplyLockedAsync(update).ConfigureAwait(false);
            }
            finally
            {
                foreach (var lease in leases) await Guarded(lease.DisposeAsync).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Every path an update touches, in a stable order. Sorted so two updates that overlap acquire
    /// their leases in the SAME order — the lock-ordering rule the mission scheduler avoids by
    /// declaring claims as a set, and which returns the moment locks are taken one at a time.
    /// </summary>
    private static IEnumerable<string> PathsOf(FileUpdate update) =>
        update.Changes
            .SelectMany(change => change switch
            {
                FileChange.Replace replace => new[] { replace.TargetPath },
                FileChange.Move move => [move.From, move.To],
                FileChange.Delete delete => [delete.Path],
                FileChange.CreateDirectory create => [create.Path],
                _ => [],
            })
            .Select(PathClaims.Canonical)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when any lease could not be taken — everything already taken is released first.</summary>
    private async Task<List<IPathLease>?> AcquireLeasesAsync(FileUpdate update, CancellationToken cancellationToken)
    {
        if (_options.Locker is not { } locker) return [];

        var held = new List<IPathLease>();
        foreach (var path in PathsOf(update))
        {
            var lease = await locker.TryAcquireAsync(path, _options.LeaseTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (lease is null)
            {
                foreach (var acquired in held) await Guarded(acquired.DisposeAsync).ConfigureAwait(false);
                Log(() => $"file update deferred: could not lease {path}");
                return null;
            }
            held.Add(lease);
        }
        return held;
    }

    /// <summary>
    /// Best-effort "who is holding this?", for a failure an app would otherwise report as an opaque
    /// IOException. Never throws: a diagnostic that can fail the operation it is describing is worse
    /// than no diagnostic.
    /// </summary>
    private IReadOnlyList<FileLockHolder> HoldersOf(string path)
    {
        if (_options.LockInspector is not { } inspector) return [];
        try { return inspector.WhoHolds(path); }
        catch (Exception ex)
        {
            Log(() => $"lock inspector failed for {path}: {ex.GetType().Name}");
            return [];
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
        var undo = new List<FileUndoStep>();
        var staged = new List<FileUndoStep>();   // deletes to finish once the whole set lands
        // Journalled only for AllOrNothing: PerChange promises nothing about a crash, so recording an
        // undo plan for it would be writing a file per update to guarantee something nobody asked for.
        var journal = atomic ? _options.Journal : null;
        var updateId = $"u{Guid.NewGuid():N}";
        var startedUtc = DateTimeOffset.UtcNow;

        for (var index = 0; index < update.Changes.Count; index++)
        {
            var change = update.Changes[index];
            try
            {
                await ApplyWithRetryAsync(
                    change, atomic, undo, staged, update.Retry,
                    journal, updateId, startedUtc).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var holders = FirstPathOf(change) is { } contested ? HoldersOf(contested) : [];
                Log(() => $"file update failed at change {index} ({change.GetType().Name}): {ex.GetType().Name}"
                          + (holders.Count > 0 ? $" — held by {string.Join(", ", holders)}" : string.Empty));
                if (!atomic) return new FileUpdateResult(index, index, ex, rolledBack: false, holders);

                await RollbackAsync(undo).ConfigureAwait(false);
                if (journal is not null)
                    await Guarded(() => new ValueTask(journal.RemoveAsync(updateId, CancellationToken.None)))
                        .ConfigureAwait(false);
                return new FileUpdateResult(0, index, ex, rolledBack: true, holders);
            }
        }

        // Past this line the update has LANDED. If the process dies now, recovery must finish the
        // staged deletions rather than roll back a success — which is what the stage marker is for.
        if (journal is not null)
            await journal.WriteAsync(
                new FileUpdateJournalEntry(updateId, FileUpdateStage.Committing, undo, staged, startedUtc),
                CancellationToken.None).ConfigureAwait(false);

        foreach (var commit in staged) await Guarded(() => RunUndoAsync(commit)).ConfigureAwait(false);

        if (journal is not null)
            await Guarded(() => new ValueTask(journal.RemoveAsync(updateId, CancellationToken.None)))
                .ConfigureAwait(false);
        return new FileUpdateResult(update.Changes.Count, null, null, rolledBack: false, []);
    }

    /// <summary>
    /// Finish what a previous run left half-done. Call it at startup, before submitting anything —
    /// an interrupted update's paths are exactly the ones a new update is likely to touch.
    ///
    /// <para>
    /// Entries left <see cref="FileUpdateStage.Applying"/> are ROLLED BACK: the update never reached
    /// the point where it could claim to have landed, so the only state it is entitled to is the one
    /// before it started. Entries left <see cref="FileUpdateStage.Committing"/> are FINISHED —
    /// rolling those back would undo an update that had already succeeded.
    /// </para>
    ///
    /// <para>
    /// Safe to run twice: every undo step checks the world before acting, because after a crash it
    /// cannot assume the change it undoes ever happened.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Abandons recovery; entries not yet handled stay in the journal.</param>
    /// <returns>How many interrupted updates were resolved.</returns>
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        if (_options.Journal is not { } journal) return 0;

        var entries = await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        var resolved = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Stage == FileUpdateStage.Applying)
            {
                Log(() => $"recovering interrupted update {entry.UpdateId} (started {entry.StartedUtc:u}): rolling back {entry.Undo.Count} step(s)");
                await RollbackAsync(entry.Undo).ConfigureAwait(false);
            }
            else
            {
                Log(() => $"recovering interrupted update {entry.UpdateId}: finishing {entry.Staged.Count} staged deletion(s)");
                foreach (var commit in entry.Staged) await Guarded(() => RunUndoAsync(commit)).ConfigureAwait(false);
            }

            await journal.RemoveAsync(entry.UpdateId, cancellationToken).ConfigureAwait(false);
            resolved++;
        }
        if (resolved > 0) Log(() => $"recovered {resolved} interrupted file update(s)");
        return resolved;
    }

    /// <summary>The path a failure is most likely ABOUT, for the "who holds it" question.</summary>
    private static string? FirstPathOf(FileChange change) => change switch
    {
        FileChange.Replace replace => replace.TargetPath,
        FileChange.Move move => move.To,
        FileChange.Delete delete => delete.Path,
        FileChange.CreateDirectory create => create.Path,
        _ => null,
    };

    private async ValueTask ApplyWithRetryAsync(
        FileChange change, bool atomic, List<FileUndoStep> undo, List<FileUndoStep> staged, RetryPolicy? retry,
        IFileUpdateJournal? journal, string updateId, DateTimeOffset startedUtc)
    {
        var policy = retry ?? RetryPolicy.None;
        var attempt = 0;
        while (true)
        {
            attempt++;
            // Re-planned on every attempt: a retry happens because the world refused, and the world
            // may look different now (the target that existed a moment ago may be gone).
            var planned = await PlanChangeAsync(change, atomic).ConfigureAwait(false);
            try
            {
                // WRITE-AHEAD. The plan for undoing this change is durable BEFORE the change happens.
                // An entry written afterwards is missing exactly the change that got interrupted,
                // which is the only one recovery needs.
                if (journal is not null && (planned.Undo.Count > 0 || planned.Staged.Count > 0))
                    await journal.WriteAsync(
                        new FileUpdateJournalEntry(
                            updateId, FileUpdateStage.Applying,
                            [.. undo, .. planned.Undo], [.. staged, .. planned.Staged], startedUtc),
                        CancellationToken.None).ConfigureAwait(false);

                await planned.Apply().ConfigureAwait(false);
                undo.AddRange(planned.Undo);
                staged.AddRange(planned.Staged);
                return;
            }
            catch (Exception ex) when (attempt < policy.Attempts && policy.IsTransient(ex))
            {
                await Task.Delay(policy.Delay * attempt).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// What a change WILL do and how to undo it, decided before anything is touched.
    ///
    /// <para>
    /// The split exists for the journal: an undo plan is only useful if it is durable BEFORE the
    /// mutation, and it can only be computed from the current state (does the target exist? what
    /// sidecar name will the backup get?). Deciding and doing in one step, as this used to, makes a
    /// write-ahead record impossible — the plan would only exist after the change it protects.
    /// </para>
    /// </summary>
    private readonly record struct PlannedChange(
        IReadOnlyList<FileUndoStep> Undo, IReadOnlyList<FileUndoStep> Staged, Func<ValueTask> Apply)
    {
        public static PlannedChange Nothing { get; } = new([], [], () => ValueTask.CompletedTask);
    }

    private async ValueTask<PlannedChange> PlanChangeAsync(FileChange change, bool atomic)
    {
        switch (change)
        {
            case FileChange.CreateDirectory create:
            {
                if (await _operations.DirectoryExistsAsync(create.Path).ConfigureAwait(false))
                    return PlannedChange.Nothing;
                return new PlannedChange(
                    // Only remove what we created, and only if still empty — another change may have
                    // filled it, and undoing that is not this change's business.
                    atomic ? [new FileUndoStep(FileUndoKind.RemoveCreatedDirectory, create.Path)] : [],
                    [],
                    () => _operations.CreateDirectoryAsync(create.Path));
            }

            case FileChange.Replace replace:
            {
                var existed = await _operations.FileExistsAsync(replace.TargetPath).ConfigureAwait(false);
                if (!existed)
                    return new PlannedChange(
                        atomic ? [new FileUndoStep(FileUndoKind.DeleteCreatedFile, replace.TargetPath)] : [],
                        [],
                        () => _operations.MoveFileAsync(replace.TempPath, replace.TargetPath, overwrite: false));

                if (!atomic)
                    return new PlannedChange([], [],
                        () => _operations.MoveFileAsync(replace.TempPath, replace.TargetPath, overwrite: true));

                // Keep the displaced original: that is what makes the rollback possible at all.
                var backup = SidecarPath(replace.TargetPath, "bak");
                return new PlannedChange(
                    [new FileUndoStep(FileUndoKind.RestoreBackup, replace.TargetPath, backup)],
                    [new FileUndoStep(FileUndoKind.DeleteCreatedFile, backup)],
                    () => _operations.ReplaceFileAsync(replace.TempPath, replace.TargetPath, backup));
            }

            case FileChange.Move move:
            {
                var existed = move.Overwrite
                    && await _operations.FileExistsAsync(move.To).ConfigureAwait(false);
                if (existed && atomic)
                {
                    var backup = SidecarPath(move.To, "bak");
                    return new PlannedChange(
                        // Two steps, in apply order, so the reverse walk restores correctly.
                        [
                            new FileUndoStep(FileUndoKind.RestoreBackup, move.To, backup),
                            new FileUndoStep(FileUndoKind.MoveBack, move.From, move.To),
                        ],
                        [new FileUndoStep(FileUndoKind.DeleteCreatedFile, backup)],
                        () => _operations.ReplaceFileAsync(move.From, move.To, backup));
                }

                return new PlannedChange(
                    atomic ? [new FileUndoStep(FileUndoKind.MoveBack, move.From, move.To)] : [],
                    [],
                    () => _operations.MoveFileAsync(move.From, move.To, move.Overwrite));
            }

            case FileChange.Delete delete:
            {
                var isFile = await _operations.FileExistsAsync(delete.Path).ConfigureAwait(false);
                var isDirectory = !isFile
                    && await _operations.DirectoryExistsAsync(delete.Path).ConfigureAwait(false);
                if (!isFile && !isDirectory)
                    return PlannedChange.Nothing;   // already gone is the outcome the caller wanted

                if (!atomic)
                    return new PlannedChange([], [], () => isFile
                        ? _operations.DeleteFileAsync(delete.Path)
                        : _operations.DeleteDirectoryAsync(delete.Path, delete.Recursive));

                // STAGED: a delete is the one change that cannot be undone from nothing, so under
                // AllOrNothing it is a move aside now and a real delete only once everything lands.
                var aside = SidecarPath(delete.Path, "del");
                return new PlannedChange(
                    [new FileUndoStep(FileUndoKind.MoveBack, delete.Path, aside)],
                    [isFile
                        ? new FileUndoStep(FileUndoKind.DeleteCreatedFile, aside)
                        : new FileUndoStep(FileUndoKind.RemoveCreatedDirectory, aside)],
                    () => _operations.MoveFileAsync(delete.Path, aside, overwrite: false));
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
    private async ValueTask RollbackAsync(IReadOnlyList<FileUndoStep> undo)
    {
        for (var index = undo.Count - 1; index >= 0; index--)
            await Guarded(() => RunUndoAsync(undo[index])).ConfigureAwait(false);
    }

    /// <summary>
    /// Perform one undo step, TOLERATING a world that does not match the plan. After a crash the step
    /// may already have been done, or never have happened at all: the change it undoes may have been
    /// interrupted half way. So every step checks first and does nothing when there is nothing to do —
    /// which also makes recovery safe to run twice.
    /// </summary>
    private async ValueTask RunUndoAsync(FileUndoStep step)
    {
        switch (step.Kind)
        {
            case FileUndoKind.DeleteCreatedFile:
                if (await _operations.FileExistsAsync(step.Target).ConfigureAwait(false))
                    await _operations.DeleteFileAsync(step.Target).ConfigureAwait(false);
                return;

            case FileUndoKind.RestoreBackup:
            case FileUndoKind.MoveBack:
                if (step.Source is { } source
                    && (await _operations.FileExistsAsync(source).ConfigureAwait(false)
                        || await _operations.DirectoryExistsAsync(source).ConfigureAwait(false)))
                    await _operations.MoveFileAsync(source, step.Target, overwrite: true).ConfigureAwait(false);
                return;

            case FileUndoKind.RemoveCreatedDirectory:
                if (await _operations.DirectoryExistsAsync(step.Target).ConfigureAwait(false))
                    await _operations.DeleteDirectoryAsync(step.Target, recursive: false).ConfigureAwait(false);
                return;

            default:
                throw new NotSupportedException($"Unhandled undo kind '{step.Kind}'.");
        }
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
