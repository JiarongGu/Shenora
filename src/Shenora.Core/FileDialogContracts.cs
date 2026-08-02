namespace Shenora.Core;

// The file-dialog CONTRACT lives here, in the platform-neutral package, while the WinForms
// implementation (dedicated-STA dialogs, owner-handle z-order, directory memory) stays in
// Shenora.Windows — D20. The point is that an app's own facades and business logic can compile
// with NO Windows reference, so the same logic runs on a non-Windows shell that implements these
// contracts (D16: mobile shells are a target).
//
// Accepted, documented lean (D20, and D16's amendment records that a transport spike does NOT
// validate this half): this contract
// is desktop-FLAVOURED. FileDialogOptions carries Win32 dialog vocabulary and FilePath is a path
// string. A mobile document picker would ignore the validation hints and return a content URI. That
// is why FilePath is specified as "a path or URI the HOST can resolve" rather than "a filesystem
// path" — and why narrowing this shape is an accepted pre-1.0 possibility at first mobile adoption
// rather than a break we pre-empt for a consumer that doesn't exist yet (D15).

/// <summary>One dialog filter row (e.g. name "Images", extensions ["png", "jpg"]).</summary>
public sealed class FileDialogFilter
{
    /// <summary>Display name of the filter row.</summary>
    public required string Name { get; init; }

    /// <summary>Extensions WITHOUT the dot or wildcard (<c>"png"</c>, not <c>"*.png"</c>).</summary>
    public required IReadOnlyList<string> Extensions { get; init; }
}

/// <summary>
/// Inputs for one dialog call — the frontend typically sends these over IPC, so the shape is
/// wire-friendly. Members documented as "default true" describe the desktop implementation; a host
/// that has no equivalent (a mobile document picker) may ignore them.
/// </summary>
public sealed class FileDialogOptions
{
    /// <summary>Dialog title. Null = a neutral default per dialog kind.</summary>
    public string? Title { get; init; }

    /// <summary>Start location when nothing is remembered. Null/missing = a host-chosen default.</summary>
    public string? DefaultPath { get; init; }

    /// <summary>Filter rows; null/empty = "All Files".</summary>
    public IReadOnlyList<FileDialogFilter>? Filters { get; init; }

    /// <summary>
    /// Key under which the last-used directory is remembered (via the app's
    /// <see cref="IFileDialogPathStore"/>) and restored next time. Null = no memory.
    /// </summary>
    public string? RememberPathKey { get; init; }

    /// <summary>
    /// Folder dialog only: also allow picking a FILE. The desktop implementation uses an
    /// OpenFileDialog with relaxed validation and a placeholder file name instead of the folder
    /// browser — the family's proven pattern for "give me a folder or an archive".
    /// </summary>
    public bool AllowFileSelection { get; init; }

    /// <summary>File dialog: require the picked file to exist. Default true. Desktop hint.</summary>
    public bool? CheckFileExists { get; init; }

    /// <summary>Require the path to exist. Default true. Desktop hint.</summary>
    public bool? CheckPathExists { get; init; }

    /// <summary>Validate the host's file-name rules. Default true for file dialogs. Desktop hint.</summary>
    public bool? ValidateNames { get; init; }

    /// <summary>Initial file name shown in the dialog.</summary>
    public string? FileName { get; init; }

    /// <summary>Save dialog: extension appended when the user omits one (no dot).</summary>
    public string? DefaultExtension { get; init; }

    /// <summary>Save dialog: prompt before overwriting an existing file. Default true. Desktop hint.</summary>
    public bool OverwritePrompt { get; init; } = true;
}

/// <summary>
/// A dialog outcome: the selection, or a cancellation (<see cref="Success"/> false). Failures
/// THROW — the source app flattened exceptions into a wire-bound error string, which is the
/// exact leak shape the IPC error contract forbids; the dispatch boundary maps throws instead.
/// </summary>
public sealed class FileDialogResult
{
    /// <summary>True when the user picked something.</summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The picked location when <see cref="Success"/> — a path or URI THE HOST CAN RESOLVE (the
    /// desktop implementation returns an absolute filesystem path; another host may return its own
    /// resolvable form). Pass it back to host services rather than parsing it yourself.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>A successful selection.</summary>
    public static FileDialogResult Selected(string path) => new() { Success = true, FilePath = path };

    /// <summary>The user cancelled.</summary>
    public static FileDialogResult Cancelled() => new() { Success = false };
}

/// <summary>
/// Where remembered dialog directories live — the seam generalizing the source app's coupling
/// to its own settings service (persist to your settings store; relativize portable paths there
/// if you want portability, the framework passes absolutes).
/// </summary>
public interface IFileDialogPathStore
{
    /// <summary>The remembered directory for a key, or null.</summary>
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
    Task<FileDialogResult> OpenFileAsync(FileDialogOptions? options = null);

    /// <summary>
    /// Pick a folder (or a file too, with <see cref="FileDialogOptions.AllowFileSelection"/>).
    /// <para>
    /// <b>DESKTOP CAPABILITY — do not expect this on every shell (D35).</b> A desktop folder browser
    /// returns ambient, permanent access to an arbitrary path; a mobile system returns a revocable,
    /// scoped grant, or nothing at all. Same word, different guarantee, and pretending otherwise
    /// makes a portable-looking call fail exactly when an app depends on it.
    /// </para>
    /// <para>
    /// Before reaching for this, check which of these you actually meant — all three ARE portable:
    /// somewhere the app owns to read and write is <see cref="ShenoraPaths"/> and needs no picker;
    /// letting the user hand over media is a media picker on mobile and a multi-select
    /// <see cref="OpenFileAsync"/> on the desktop; and a single document is
    /// <see cref="OpenFileAsync"/> plus <see cref="OpenReadAsync"/>. Only "grant me an arbitrary
    /// working directory" genuinely needs this, and that is the desktop-shaped one.
    /// </para>
    /// </summary>
    Task<FileDialogResult> OpenFolderAsync(FileDialogOptions? options = null);

    /// <summary>
    /// Pick a save destination and get the PATH back.
    /// <para>
    /// <b>DESKTOP CAPABILITY — prefer <see cref="SaveAsync"/> in portable logic (D35's shape again).</b>
    /// This promises an addressable destination the app can then write to at its leisure, and that
    /// promise is not expressible on mobile at all: the platform hands back a document the app may
    /// write INTO, not a path it may write AT, and a picked cache path cannot be written back to the
    /// user's file. So a shell without that concept refuses this loudly (D33) rather than returning
    /// something path-shaped that silently goes nowhere.
    /// </para>
    /// </summary>
    Task<FileDialogResult> SaveFileAsync(FileDialogOptions? options = null);

    /// <summary>
    /// Pick a destination AND write to it, in ONE call — the PORTABLE save. This is the counterpart to
    /// <see cref="OpenReadAsync"/>: open became universal by letting the host do the reading, and save
    /// becomes universal by letting the host do the writing.
    /// <para>
    /// <b>Why the shape is a callback and not a returned path.</b> "Give me somewhere to save to" is
    /// not expressible on mobile — the user grants access to one document, the app writes into it while
    /// the grant is live, and there is no path it can keep. Handing the host a <paramref name="write"/>
    /// delegate is the only shape that is honest on every shell, so it is the shape portable logic
    /// should use even on the desktop, where the weaker one also happens to work.
    /// </para>
    /// <para>
    /// <b>The write is ATOMIC, and that matters more here than anywhere else in the kit.</b> The default
    /// implementation produces the content into a sibling temp and swaps it in only once
    /// <paramref name="write"/> has completed, so a save that is cancelled, throws, or is interrupted
    /// half-way <b>leaves the user's existing file exactly as it was</b> — it costs the work, never the
    /// original. This is the case that motivated the primitive: a save picker is usually pointed at a
    /// long operation (an encode, a report, an export), and the longer the operation the wider the
    /// window a naive write-over-the-target leaves open. See <see cref="Files.BeginReplace"/>.
    /// </para>
    /// <para>
    /// A default implementation over <see cref="SaveFileAsync"/>, so adding this breaks no existing
    /// implementor and any shell with a real save picker gets it for free. A shell whose platform has
    /// no addressable destination overrides this and refuses <see cref="SaveFileAsync"/> instead — the
    /// mirror of how <see cref="OpenReadAsync"/> lets a shell substitute its own read.
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
    /// The destination on success. <see cref="FileDialogResult.FilePath"/> is populated only when the
    /// host HAS an addressable destination to report — a shell that wrote into a one-time grant reports
    /// <see cref="FileDialogResult.Success"/> with no path, because there is nothing the app could
    /// legitimately do with one.
    /// </returns>
    async Task<FileDialogResult> SaveAsync(FileDialogOptions? options,
                                           Func<Stream, CancellationToken, Task> write,
                                           CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);

        var picked = await SaveFileAsync(options).ConfigureAwait(false);
        if (!picked.Success || picked.FilePath is not { Length: > 0 } path)
            return FileDialogResult.Cancelled();

        // Cancellation is checked AFTER the pick, not before: the user has just chosen a destination,
        // and doing the work anyway would write to a file they may have moved on from. Checking here
        // means a cancelled save never touches the destination at all.
        cancellationToken.ThrowIfCancellationRequested();

        using var replacement = Files.BeginReplace(path);
        var stream = new FileStream(replacement.TempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using (stream.ConfigureAwait(false))
        {
            await write(stream, cancellationToken).ConfigureAwait(false);
        }
        // Only now does the destination change. Anything thrown above — including the caller's own
        // "this output is not valid" — escapes with the previous file intact, because Dispose discards
        // the temp on the way out.
        replacement.Commit();
        return FileDialogResult.Selected(path);
    }

    /// <summary>
    /// Read the content behind a <see cref="FileDialogResult.FilePath"/>. This is how PORTABLE app
    /// logic consumes a picked file, and the reason it exists is the design goal that the frontend
    /// and the interface stay universal while the implementation is device-dependent.
    /// <para>
    /// <b>Do not call <c>File.OpenRead</c> on a picked handle yourself.</b> The contract says
    /// FilePath is "a path or URI the HOST can resolve", and only the host knows which. It happens
    /// to be a real path on both shells today — Windows returns the file, and MAUI's picker COPIES
    /// the chosen document into app cache and returns that path — so the default implementation
    /// below is correct on both. That is a fact about today's two shells, not a property of the
    /// contract: a shell handing back a genuine content URI (raw Storage Access Framework, iOS
    /// security-scoped URLs) overrides this and app logic never notices.
    /// </para>
    /// <para>
    /// A default implementation, so adding this breaks no existing implementor. Null when the
    /// handle no longer resolves — a cache copy can be evicted, and a picked file can be deleted
    /// between choosing it and reading it.
    /// </para>
    /// </summary>
    Task<Stream?> OpenReadAsync(string handle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        return Task.FromResult<Stream?>(File.Exists(handle) ? File.OpenRead(handle) : null);
    }
}
