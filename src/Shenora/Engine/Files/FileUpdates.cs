using Shenora.Core.Shell;

namespace Shenora.Engine.Files;

/// <summary>How much of a <see cref="FileUpdate"/> has to survive together.</summary>
public enum FileAtomicity
{
    /// <summary>
    /// Each change stands alone. Changes are applied in order and the first failure stops the update,
    /// leaving earlier ones applied — right when the changes are independent (N unrelated files).
    /// </summary>
    PerChange = 0,

    /// <summary>
    /// The change set is ONE action: on failure, everything already applied is undone. Right when a
    /// half-applied set is a broken product — a bundle plus its index, files plus their manifest.
    /// <para>
    /// ⚠ <b>How far this goes depends on whether you configured a journal.</b> Without
    /// <see cref="FileUpdateQueueOptions.Journal"/> rollback covers a change that FAILS, but a process
    /// killed mid-apply leaves whatever it had reached. WITH one, the undo plan is on disk before each
    /// change and <see cref="FileUpdateQueue.RecoverAsync"/> finishes the job at startup.
    /// </para>
    /// </summary>
    AllOrNothing = 1,
}

/// <summary>
/// One filesystem mutation. A closed set of four, each of which the OS can perform atomically on its own.
/// </summary>
public abstract record FileChange
{
    private FileChange() { }

    /// <summary>Put <paramref name="TempPath"/> in place of <paramref name="TargetPath"/>.</summary>
    /// <param name="TempPath">The staged file, already written. Consumed by the move.</param>
    /// <param name="TargetPath">Where it lands. Replaced if it exists, created if it does not.</param>
    public sealed record Replace(string TempPath, string TargetPath) : FileChange;

    /// <summary>Move a file — or a directory, when the destination does not exist yet.</summary>
    /// <param name="From">Source path.</param>
    /// <param name="To">Destination path.</param>
    /// <param name="Overwrite">Replace an existing destination FILE. False fails instead. ⚠ An existing
    /// destination DIRECTORY always fails, whichever this says — delete it first when replacing the tree
    /// is really meant.</param>
    public sealed record Move(string From, string To, bool Overwrite = false) : FileChange;

    /// <summary>
    /// Delete a file or directory. Under <see cref="FileAtomicity.AllOrNothing"/> the deletion is
    /// STAGED — moved aside, really removed only once the whole update lands.
    /// </summary>
    /// <param name="Path">What to delete. Missing is not an error.</param>
    /// <param name="Recursive">For a directory, delete its contents too.</param>
    public sealed record Delete(string Path, bool Recursive = false) : FileChange;

    /// <summary>Create a directory, including parents. Existing is not an error.</summary>
    /// <param name="Path">The directory.</param>
    public sealed record CreateDirectory(string Path) : FileChange;
}

/// <summary>
/// A set of filesystem mutations to land as one unit of scheduling. Hand it to an
/// <see cref="IFileUpdateQueue"/>: the queue decides when it runs, the update decides what happens if
/// part of it fails.
/// </summary>
public sealed class FileUpdate
{
    /// <summary>The changes, applied IN ORDER. At least one.</summary>
    public required IReadOnlyList<FileChange> Changes { get; init; }

    /// <summary>
    /// Updates with the same partition are serialized against each other; different partitions may
    /// land concurrently. Null (the default) is the global partition — one writer for everything.
    /// Partition only by something that makes two updates genuinely independent: a library root, a drive.
    /// <para>
    /// ⚠ <b>KEEP THE SET OF VALUES SMALL AND STABLE — the queue keeps ONE gate per distinct value for the
    /// process lifetime, and never prunes it.</b> A partition per FILE (or per job id) grows a dictionary
    /// of semaphores that nothing reclaims.
    /// </para>
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>What has to survive together. Default <see cref="FileAtomicity.PerChange"/>.</summary>
    public FileAtomicity Atomicity { get; init; }

    /// <summary>
    /// Retries an individual CHANGE that fails. Null = no retry. Rollback (under
    /// <see cref="FileAtomicity.AllOrNothing"/>) happens only once the retry budget is spent.
    /// </summary>
    public RetryPolicy? Retry { get; init; }
}

/// <summary>
/// How an update ended. A failing change is REPORTED here rather than thrown; caller errors (an empty
/// change set, a disposed queue) still throw.
/// </summary>
public sealed class FileUpdateResult
{
    internal FileUpdateResult(
        int applied, int? failedIndex, Exception? error, bool rolledBack, IReadOnlyList<FileLockHolder> holders)
    {
        Applied = applied;
        FailedIndex = failedIndex;
        Error = error;
        RolledBack = rolledBack;
        Holders = holders;
    }

    /// <summary>Changes that landed. Equals the change count on success; 0 after a rollback.</summary>
    public int Applied { get; }

    /// <summary>Index of the change that failed, or null on success.</summary>
    public int? FailedIndex { get; }

    /// <summary>The failure, or null on success.</summary>
    public Exception? Error { get; }

    /// <summary>True when <see cref="FileAtomicity.AllOrNothing"/> undid the applied changes.</summary>
    public bool RolledBack { get; }

    /// <summary>
    /// Who was holding the contested path, when a <see cref="FileUpdateQueueOptions.LockInspector"/>
    /// is configured and could tell. Empty otherwise — including when the holder is on another machine.
    /// </summary>
    public IReadOnlyList<FileLockHolder> Holders { get; }

    /// <summary>True when every change landed.</summary>
    public bool Succeeded => FailedIndex is null;

    /// <summary>Rethrow the failure, preserving its stack, for callers who prefer exceptions.</summary>
    public void ThrowIfFailed()
    {
        if (Error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(Error).Throw();
    }
}

/// <summary>
/// Serializes filesystem mutations so that missions can run in PARALLEL while their changes land ONE
/// AT A TIME. Separate from the mission scheduler, which decides only which missions may run (D30).
/// </summary>
public interface IFileUpdateQueue
{
    /// <summary>
    /// Queue an update and complete when THIS update has landed or failed. Ordering between callers
    /// on one partition is arrival order.
    /// </summary>
    /// <param name="update">The changes and how much of them must survive together.</param>
    /// <param name="cancellationToken">
    /// ⚠ Cancels while WAITING for the partition only. Once an update starts applying it runs to
    /// completion or failure.
    /// </param>
    Task<FileUpdateResult> ApplyAsync(FileUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finish what a previous run left half-done. ⚠ Call it at startup, before submitting anything — the
    /// journal is on by default, and one nobody replays is a directory that fills up while interrupted
    /// updates stay un-rolled-back.
    /// <para>
    /// Entries left <see cref="FileUpdateStage.Applying"/> are ROLLED BACK; entries left
    /// <see cref="FileUpdateStage.Committing"/> are FINISHED. Safe to run twice.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Abandons recovery; entries not yet handled stay in the journal.</param>
    /// <returns>How many interrupted updates were resolved. Zero when there is no journal.</returns>
    Task<int> RecoverAsync(CancellationToken cancellationToken = default);
}
