using System.Text.Json;

using Shenora;

namespace Shenora.Engine.Files;

/// <summary>What an interrupted update needs done to it when the process comes back.</summary>
public enum FileUpdateStage
{
    /// <summary>
    /// Changes were still being applied. Recovery ROLLS BACK — the update never reached the point
    /// where it could claim to have landed.
    /// </summary>
    Applying = 0,

    /// <summary>
    /// Every change landed and only the staged deletions were left to finish. Recovery FINISHES them:
    /// rolling back here would undo an update that had already succeeded.
    /// </summary>
    Committing = 1,
}

/// <summary>One undoable thing an update did, recorded before it was done.</summary>
public enum FileUndoKind
{
    /// <summary>Delete a file this update created (<c>Target</c>). No backup existed.</summary>
    DeleteCreatedFile = 0,

    /// <summary>Move <c>Source</c> back over <c>Target</c> — a backup taken before a replace.</summary>
    RestoreBackup = 1,

    /// <summary>Move <c>Source</c> back to <c>Target</c> — undoing a move, or a staged delete.</summary>
    MoveBack = 2,

    /// <summary>Remove a directory this update created (<c>Target</c>), if still empty.</summary>
    RemoveCreatedDirectory = 3,
}

/// <summary>
/// One step of an update's undo plan. Data, not a closure, so it survives a process restart — which
/// is the entire reason the journal can exist.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Target">The path being restored or removed.</param>
/// <param name="Source">Where the content currently is, for the kinds that move something back.</param>
public sealed record FileUndoStep(FileUndoKind Kind, string Target, string? Source = null);

/// <summary>
/// The write-ahead record of one in-flight update: enough to undo it, or to finish it, with no other
/// context. Written BEFORE anything is touched and removed only once the update is fully done.
/// </summary>
/// <param name="UpdateId">Identity, so recovery can remove exactly this entry.</param>
/// <param name="Stage">Whether recovery should roll back or finish.</param>
/// <param name="Undo">Steps in APPLY order; recovery walks them backwards.</param>
/// <param name="Staged">Aside-copies of staged deletes, to remove when finishing.</param>
/// <param name="StartedUtc">When the update began — for a human reading a stuck journal.</param>
public sealed record FileUpdateJournalEntry(
    string UpdateId,
    FileUpdateStage Stage,
    IReadOnlyList<FileUndoStep> Undo,
    IReadOnlyList<FileUndoStep> Staged,
    DateTimeOffset StartedUtc);

/// <summary>
/// Where the write-ahead records of in-flight updates live, so that
/// <see cref="FileAtomicity.AllOrNothing"/> survives the process DYING rather than merely failing.
///
/// <para>
/// Without one, rollback is compensating and in-memory: correct for a failed change, useless after a
/// power cut, because the plan to undo died with the process. With one, the plan is on disk before
/// anything is touched, and <see cref="FileUpdateQueue.RecoverAsync"/> completes it at startup.
/// </para>
///
/// <para>
/// An implementation must be crash-safe ITSELF — a journal that can be half-written is a journal that
/// can leave a torn recovery, which is worse than no journal at all. <see cref="FileUpdateJournal"/>
/// is the shipped one; supply your own only if you already have storage with the same property.
/// </para>
/// </summary>
public interface IFileUpdateJournal
{
    /// <summary>Record or update an entry. Must be durable before it returns.</summary>
    Task WriteAsync(FileUpdateJournalEntry entry, CancellationToken cancellationToken);

    /// <summary>Remove a finished entry. Must not throw when it is already gone.</summary>
    Task RemoveAsync(string updateId, CancellationToken cancellationToken);

    /// <summary>Every entry still recorded — i.e. every update interrupted by the last stop.</summary>
    Task<IReadOnlyList<FileUpdateJournalEntry>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Inputs for <see cref="FileUpdateJournal"/>.</summary>
public sealed class FileUpdateJournalOptions
{
    /// <summary>
    /// Directory the journal files live in — the app's own storage, and on the SAME volume as the
    /// files being updated if you can manage it, so a disk that survives the crash keeps both.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>Diagnostics sink, guarded through <see cref="AppCallback.Log"/>.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// The shipped journal: one small JSON file per in-flight update, written through to disk.
///
/// <para>
/// Written with <see cref="FileOptions.WriteThrough"/> and flushed before the write is considered
/// done, because a journal sitting in the OS write cache when the power goes is exactly the journal
/// that was not there. It is deliberately one file per update rather than an append log: a torn
/// append is a parsing problem at the worst possible moment, whereas a torn single file is one
/// unreadable entry that recovery can report and skip.
/// </para>
///
/// <para>
/// <b>The kit ships this one</b> despite shipping no other storage — a journal that is not crash-safe
/// is pointless, and "write your own crash-safe store" is not a reasonable thing to ask of every
/// adopter for a mechanism whose entire purpose is surviving a crash.
/// </para>
/// </summary>
public sealed class FileUpdateJournal : IFileUpdateJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly FileUpdateJournalOptions _options;

    /// <param name="options">Where the journal lives.</param>
    public FileUpdateJournal(FileUpdateJournalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.Directory);
        _options = options;
        System.IO.Directory.CreateDirectory(options.Directory);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(FileUpdateJournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
        var path = PathFor(entry.UpdateId);

        // Write to a temp file and replace: an entry is either the old one or the new one, never a
        // half-written mixture of the two — the same rule the queue applies to the files it manages.
        var temp = $"{path}.writing";
        await using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        }))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string updateId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(updateId);
        try { File.Delete(PathFor(updateId)); }
        catch (FileNotFoundException) { /* already gone is the outcome we wanted */ }
        catch (DirectoryNotFoundException) { }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileUpdateJournalEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!System.IO.Directory.Exists(_options.Directory)) return [];

        var entries = new List<FileUpdateJournalEntry>();
        foreach (var file in System.IO.Directory.GetFiles(_options.Directory, "*.journal"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
                if (JsonSerializer.Deserialize<FileUpdateJournalEntry>(bytes, Json) is { } entry)
                    entries.Add(entry);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A torn or unreadable entry is REPORTED and skipped, never thrown: one bad file must
                // not stop every other interrupted update from being recovered.
                AppCallback.Log(_options.Log, () => $"unreadable journal entry {file}: {ex.GetType().Name}");
            }
        }
        return entries;
    }

    private string PathFor(string updateId) =>
        System.IO.Path.Combine(_options.Directory, $"{updateId}.journal");
}
