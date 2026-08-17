using Shenora.Engine.Files;

namespace Shenora.Modules.FileDialog;

// The file-dialog CONTRACT; the WinForms implementation (dedicated-STA dialogs, owner-handle z-order,
// directory memory) lives in Shenora.Windows (D20), so app logic that picks files compiles with NO
// Windows reference. The shape is desktop-FLAVOURED: FilePath is specified as "a path or URI the HOST
// can resolve", never a filesystem path, because a mobile document picker returns a content URI (D16).

/// <summary>One dialog filter row (e.g. name "Images", extensions ["png", "jpg"]).</summary>
public sealed class FileDialogFilter
{
    /// <summary>Display name of the filter row.</summary>
    public required string Name { get; init; }

    /// <summary>Extensions WITHOUT the dot or wildcard (<c>"png"</c>, not <c>"*.png"</c>).</summary>
    public required IReadOnlyList<string> Extensions { get; init; }
}

/// <summary>
/// What EVERY dialog call takes; the per-dialog types below add what only that dialog can honour.
/// Members documented as "default true" describe the desktop implementation; a host that has no
/// equivalent (a mobile document picker) may ignore them.
/// </summary>
public abstract class FileDialogOptions
{
    /// <summary>Dialog title. Null = a neutral default per dialog kind.</summary>
    public string? Title { get; init; }

    /// <summary>Start location when nothing is remembered. Null/missing = a host-chosen default.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>
    /// Key under which the last-used directory is remembered (via the app's
    /// <see cref="IFileDialogPathStore"/>) and restored next time. Null = no memory. Memory is per KEY,
    /// so the desktop implementation turns Win32's own per-application <c>RestoreDirectory</c> OFF.
    /// <para>
    /// ⚠ <b>This value can arrive from the PAGE</b> — a web bundle calling <c>useFileDialogs()</c>
    /// chooses it. Treat it as untrusted input in your <see cref="IFileDialogPathStore"/>: see that
    /// interface's remarks.
    /// </para>
    /// </summary>
    public string? RememberPathKey { get; init; }
}

/// <summary>Inputs for <see cref="IFileDialogs.OpenFileAsync"/>.</summary>
public sealed class OpenFileOptions : FileDialogOptions
{
    /// <summary>Filter rows; null/empty = "All Files".</summary>
    public IReadOnlyList<FileDialogFilter>? Filters { get; init; }

    /// <summary>Initial file name shown in the dialog.</summary>
    public string? FileName { get; init; }

    /// <summary>File dialog: require the picked file to exist. Default true. Desktop hint.</summary>
    public bool? CheckFileExists { get; init; }

    /// <summary>Require the path to exist. Default true. Desktop hint.</summary>
    public bool? CheckPathExists { get; init; }

    /// <summary>Validate the host's file-name rules. Default true for file dialogs. Desktop hint.</summary>
    public bool? ValidateNames { get; init; }
}

/// <summary>
/// Inputs for <see cref="IFileDialogs.OpenFolderAsync"/> — the smallest of the three. No file name:
/// a folder picker has nothing to pre-name.
/// </summary>
public sealed class OpenFolderOptions : FileDialogOptions
{
    /// <summary>
    /// Also allow picking a FILE. The desktop implementation swaps the folder browser for an
    /// OpenFileDialog with relaxed validation and a placeholder file name — Windows' Common Item Dialog
    /// offers folders-only (<c>FOS_PICKFOLDERS</c>) or files-only, never both.
    /// </summary>
    public bool AllowFileSelection { get; init; }

    /// <summary>
    /// Filter rows for the FILE half of <see cref="AllowFileSelection"/>; null/empty = "All Files".
    /// ⚠ Ignored unless <see cref="AllowFileSelection"/> is set — a folder browser has nothing to filter.
    /// </summary>
    public IReadOnlyList<FileDialogFilter>? Filters { get; init; }
}

/// <summary>Inputs for <see cref="IFileDialogs.SaveFileAsync"/> and <see cref="IFileDialogs.SaveAsync"/>.</summary>
public sealed class SaveFileOptions : FileDialogOptions
{
    /// <summary>Filter rows; null/empty = "All Files".</summary>
    public IReadOnlyList<FileDialogFilter>? Filters { get; init; }

    /// <summary>Initial file name shown in the dialog — on a save, the name being suggested.</summary>
    public string? FileName { get; init; }

    /// <summary>Extension appended when the user omits one (no dot).</summary>
    public string? DefaultExtension { get; init; }

    /// <summary>Prompt before overwriting an existing file. Default true. Desktop hint.</summary>
    public bool OverwritePrompt { get; init; } = true;

    /// <summary>Require the path to exist. Default true. Desktop hint.</summary>
    public bool? CheckPathExists { get; init; }
}

/// <summary>
/// A dialog outcome: the selection, or a cancellation (<see cref="Success"/> false). Failures THROW;
/// the dispatch boundary maps them.
/// <para>
/// ⚠ <b>THREE outcomes, not two:</b> cancelled; succeeded WITH a location (<see cref="FilePath"/> set);
/// and <b>succeeded with NO location</b> — what <see cref="IFileDialogs.SaveAsync"/> returns on both
/// mobile shells, where the bytes went to a revocable grant. <c>result.FilePath!</c> after checking
/// <c>Success</c> is a null-reference waiting for a phone.
/// </para>
/// </summary>
public sealed class FileDialogResult
{
    /// <summary>True when the operation completed — the user picked something, or the save went through.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The picked location, or <b>null even on success</b> when the host has no addressable one. When set
    /// it is a path or URI THE HOST CAN RESOLVE — pass it back to host services rather than parsing it.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>A successful selection at a location the caller can name.</summary>
    public static FileDialogResult Selected(string path) => new() { Success = true, FilePath = path };

    /// <summary>Success with NO addressable location — the write completed somewhere the app cannot reopen.</summary>
    public static FileDialogResult Completed() => new() { Success = true };

    /// <summary>The user cancelled.</summary>
    public static FileDialogResult Cancelled() => new() { Success = false };
}

/// <summary>
/// Where remembered dialog directories live — persist to your own settings store. The framework passes
/// absolute paths; relativize here if you want portability.
/// </summary>
public interface IFileDialogPathStore
{
    /// <summary>The remembered directory for a key, or null.</summary>
    /// <param name="key">
    /// ⚠ <b>UNTRUSTED when the kit's file-dialog routes are registered</b> — a page supplies
    /// <see cref="FileDialogOptions.RememberPathKey"/> and it reaches you verbatim, so an implementation
    /// that composes it into a FILENAME (<c>{key}.json</c>, a registry path, a directory per key) can now
    /// be handed <c>../../config</c>. Look it up in a keyed table, or sanitise before it touches a path.
    /// </param>
    Task<string?> GetPathAsync(string key);

    /// <summary>Remember a directory for a key.</summary>
    Task SavePathAsync(string key, string path);
}

/// <summary>
/// Native file/folder/save dialogs. The desktop implementation is <c>Shenora.Windows.FileDialogs</c>;
/// depend on this interface so app logic that picks files needs no Windows reference.
/// </summary>
public interface IFileDialogs
{
    /// <summary>Pick an existing file.</summary>
    Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null);

    /// <summary>
    /// Pick a folder (or a file too, with <see cref="OpenFolderOptions.AllowFileSelection"/>).
    /// <para>
    /// ⚠ <b>DESKTOP CAPABILITY (D35)</b> — a shell with no expression of it refuses. Only "grant me an
    /// arbitrary working directory" needs this: storage the app owns is <see cref="ShenoraPaths"/> and
    /// needs no picker, and a single document is <see cref="OpenFileAsync"/> + <see cref="OpenReadAsync"/>.
    /// </para>
    /// </summary>
    Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null);

    /// <summary>
    /// Pick a save destination and get the PATH back.
    /// <para>
    /// ⚠ <b>DESKTOP CAPABILITY — prefer <see cref="SaveAsync"/> in portable logic (D35).</b> Mobile hands
    /// back a document the app may write INTO, not a path it may write AT, so a shell without that
    /// concept refuses this loudly (D33) rather than returning something path-shaped that goes nowhere.
    /// </para>
    /// </summary>
    Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null);

    /// <summary>
    /// Pick a destination AND write to it, in ONE call — the PORTABLE save, the counterpart to
    /// <see cref="OpenReadAsync"/>: the HOST does the writing, so this is the only save shape that works
    /// on every shell.
    /// <para>
    /// <b>The write is ATOMIC.</b> The default implementation produces the content into a sibling temp
    /// and swaps it in only once <paramref name="write"/> has completed, so a save that is cancelled,
    /// throws, or is interrupted half-way <b>leaves the user's existing file exactly as it was</b>. See
    /// <see cref="Files.BeginReplace"/>.
    /// </para>
    /// <para>
    /// A default implementation over <see cref="SaveFileAsync"/>; a shell with no addressable
    /// destination overrides this and refuses <see cref="SaveFileAsync"/> instead.
    /// </para>
    /// </summary>
    /// <param name="options">Dialog inputs; a host without an equivalent ignores what it cannot honour.</param>
    /// <param name="write">
    /// Produces the content. Receives a writable stream — do NOT dispose it; the caller owns its
    /// lifetime and closing it early would truncate a host that wraps it. Throw to abandon the save;
    /// the destination is then left untouched.
    /// </param>
    /// <param name="cancellationToken">Cancels the write. The user's existing file survives.</param>
    /// <returns>
    /// The destination on success — with a null <see cref="FileDialogResult.FilePath"/> on a host that
    /// wrote into a one-time grant. See <see cref="FileDialogResult"/>.
    /// </returns>
    async Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
                                           Func<Stream, CancellationToken, Task> write,
                                           CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        var picked = await SaveFileAsync(options).ConfigureAwait(false);
        if (!picked.Success || picked.FilePath is not { Length: > 0 } path)
            return FileDialogResult.Cancelled();

        // Checked AFTER the pick, so a cancelled save never touches the destination at all.
        cancellationToken.ThrowIfCancellationRequested();

        using var replacement = Files.BeginReplace(path);
        var stream = new FileStream(replacement.TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await write(stream, cancellationToken).ConfigureAwait(false);
        }
        // Only now does the destination change: anything thrown above escapes with the previous file
        // intact, because Dispose discards the temp on the way out.
        replacement.Commit();
        return FileDialogResult.Selected(path);
    }

    /// <summary>
    /// Read the content behind a <see cref="FileDialogResult.FilePath"/> — how PORTABLE app logic
    /// consumes a picked file.
    /// <para>
    /// <b>Do not call <c>File.OpenRead</c> on a picked handle yourself</b> — FilePath is "a path or URI
    /// the HOST can resolve". It happens to be a real path on both shells today (MAUI's picker COPIES the
    /// document into app cache); a shell handing back a genuine content URI overrides this.
    /// </para>
    /// <para>Null when the handle no longer resolves — a cache copy can be evicted, a picked file deleted.</para>
    /// </summary>
    Task<Stream?> OpenReadAsync(string handle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        try
        {
            return Task.FromResult<Stream?>(File.Exists(handle) ? File.OpenRead(handle) : null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // The check-then-open window: a file deleted BETWEEN the two calls is still "the handle no
            // longer resolves", and must be the documented null rather than the throw the doc rules out.
            // A file that resolves but cannot be shared still throws — that is a different answer.
            return Task.FromResult<Stream?>(null);
        }
    }
}
