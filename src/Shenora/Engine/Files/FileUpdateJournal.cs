using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shenora.Engine.Files;

/// <summary>What an interrupted update needs done to it when the process comes back.</summary>
public enum FileUpdateStage
{
    /// <summary>Changes were still being applied. Recovery ROLLS BACK.</summary>
    Applying = 0,

    /// <summary>
    /// Every change landed and only the staged deletions were left to finish. Recovery FINISHES them —
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
/// One step of an update's undo plan. Data, not a closure, so it survives a process restart.
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
/// <para>
/// 🔴 An implementation must be crash-safe ITSELF — a journal that can be half-written leaves a torn
/// recovery, which is worse than no journal at all. <see cref="FileUpdateJournal"/> is the shipped one.
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
    /// files being updated if you can manage it.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>Diagnostics sink, guarded through <see cref="AppCallback.Log"/>.</summary>
    public ILogger? Log { get; init; }
}

/// <summary>
/// The shipped journal: one small JSON file per in-flight update, written with
/// <see cref="FileOptions.WriteThrough"/> and flushed before the write is considered done — a journal
/// sitting in the OS write cache when the power goes is exactly the journal that was not there.
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
        // half-written mixture.
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
                // Reported and skipped, never thrown: one bad file must not stop every other
                // interrupted update from being recovered.
                AppCallback.Log(_options.Log, () => $"unreadable journal entry {file}: {ex.GetType().Name}");
            }
        }
        return entries;
    }

    private string PathFor(string updateId) =>
        System.IO.Path.Combine(_options.Directory, $"{updateId}.journal");
}
