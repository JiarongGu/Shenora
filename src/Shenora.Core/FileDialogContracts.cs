namespace Shenora.Core;

// The file-dialog CONTRACT lives here, in the platform-neutral package, while the WinForms
// implementation (dedicated-STA dialogs, owner-handle z-order, directory memory) stays in
// Shenora.WinForms — D20. The point is that an app's own facades and business logic can compile
// with NO Windows reference, so the same logic runs on a non-Windows shell that implements these
// contracts (D16: mobile shells are a target).
//
// Accepted, documented lean (see docs/2026-07-30-shenora-relayering-design.md §4.1): this contract
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
/// Native file/folder/save dialogs. The desktop implementation is <c>Shenora.WinForms.FileDialogs</c>;
/// depend on this interface so app logic that picks files needs no Windows reference.
/// </summary>
public interface IFileDialogs
{
    /// <summary>Pick an existing file.</summary>
    Task<FileDialogResult> OpenFileAsync(FileDialogOptions? options = null);

    /// <summary>Pick a folder (or a file too, with <see cref="FileDialogOptions.AllowFileSelection"/>).</summary>
    Task<FileDialogResult> OpenFolderAsync(FileDialogOptions? options = null);

    /// <summary>Pick a save destination.</summary>
    Task<FileDialogResult> SaveFileAsync(FileDialogOptions? options = null);
}
