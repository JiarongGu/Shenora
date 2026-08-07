using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora;
using Shenora.Modules.FileDialog;
using Shenora.Core.Shell;
using Shenora.Engine.Files;

namespace Shenora.Windows;

// The dialog CONTRACT (IFileDialogs, IFileDialogPathStore, FileDialogOptions/Filter/Result) moved to
// Shenora in P5.5 H4.1 so app logic that picks files compiles with no Windows reference (D20).
// What stays here is the Windows IMPLEMENTATION and its configuration.

/// <summary>Inputs for <see cref="FileDialogs"/>.</summary>
public sealed class FileDialogsOptions
{
    /// <summary>Blocks the main window while a dialog is up + supplies the owner handle. Null = neither.</summary>
    public IFormInteraction? Interaction { get; init; }

    /// <summary>Backs <see cref="FileDialogOptions.RememberPathKey"/>. Null = no path memory.</summary>
    public IFileDialogPathStore? PathStore { get; init; }
}

/// <summary>
/// Native dialogs ported from the primary desktop sibling: every dialog runs on a DEDICATED STA
/// thread (never inline — a dialog on the WebView2's UI thread conflicts with its message
/// handling), owned by the main window for correct z-order, with the main window blocked while
/// up, and per-key last-directory memory through <see cref="IFileDialogPathStore"/>.
/// <c>SaveFileAsync</c> is new (the source only had open/folder); same pattern.
/// </summary>
public sealed class FileDialogs : IFileDialogs
{
    private readonly FileDialogsOptions _options;
    private readonly ILogger<FileDialogs> _logger;

    /// <summary>Dialogs run on a dedicated STA thread. <paramref name="options"/> null = defaults.</summary>
    public FileDialogs(FileDialogsOptions? options = null, ILogger<FileDialogs>? logger = null)
    {
        _options = options ?? new FileDialogsOptions();
        _logger = logger ?? NullLogger<FileDialogs>.Instance;
    }

    /// <inheritdoc />
    public Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null) =>
        ShowAsync(options, (opts, initialPath, owner) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = opts?.Title ?? "Select File",
                InitialDirectory = initialPath,
                RestoreDirectory = false, // directory memory is ours (per-key, cross-session)
                CheckFileExists = opts?.CheckFileExists ?? true,
                CheckPathExists = opts?.CheckPathExists ?? true,
                ValidateNames = opts?.ValidateNames ?? true,
                FileName = opts?.FileName ?? string.Empty,
                Filter = BuildFilterString(opts?.Filters),
            };
            if (ShowDialog(dialog, owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return FileDialogResult.Cancelled();
            RememberPathFireAndForget(opts, Path.GetDirectoryName(dialog.FileName));
            return FileDialogResult.Selected(dialog.FileName);
        });

    /// <inheritdoc />
    public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null) =>
        ShowAsync(options, (opts, initialPath, owner) =>
            opts?.AllowFileSelection == true
                ? ShowFileOrFolderDialog(opts, initialPath, owner)
                : ShowFolderBrowser(opts, initialPath, owner));

    /// <inheritdoc />
    public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null) =>
        ShowAsync(options, (opts, initialPath, owner) =>
        {
            using var dialog = new SaveFileDialog
            {
                Title = opts?.Title ?? "Save File",
                InitialDirectory = initialPath,
                RestoreDirectory = false,
                CheckPathExists = opts?.CheckPathExists ?? true,
                FileName = opts?.FileName ?? string.Empty,
                DefaultExt = opts?.DefaultExtension ?? string.Empty,
                AddExtension = !string.IsNullOrEmpty(opts?.DefaultExtension),
                OverwritePrompt = opts?.OverwritePrompt ?? true,
                Filter = BuildFilterString(opts?.Filters),
            };
            if (ShowDialog(dialog, owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
                return FileDialogResult.Cancelled();
            RememberPathFireAndForget(opts, Path.GetDirectoryName(dialog.FileName));
            return FileDialogResult.Selected(dialog.FileName);
        });

    /// <summary>The shared flow: resolve start dir → block the window → dedicated STA thread → unblock.</summary>
    private async Task<FileDialogResult> ShowAsync<TOptions>(
        TOptions? options, Func<TOptions?, string, IntPtr, FileDialogResult> show)
        where TOptions : FileDialogOptions
    {
        // Load the start directory BEFORE showing (the store may be async I/O).
        var initialPath = await ResolveInitialPathAsync(options).ConfigureAwait(false);
        var owner = _options.Interaction?.GetMainFormHandle() ?? IntPtr.Zero;

        _options.Interaction?.BlockInteraction();
        try
        {
            // ALWAYS a dedicated STA thread — never the WebView2's UI thread (measured conflicts
            // with its message handling in the source app).
            return await StaThread.RunAsync(() => show(options, initialPath, owner)).ConfigureAwait(false);
        }
        finally
        {
            _options.Interaction?.UnblockInteraction();
        }
    }

    private FileDialogResult ShowFileOrFolderDialog(OpenFolderOptions opts, string initialPath, IntPtr owner)
    {
        // "Folder OR file" via OpenFileDialog with relaxed validation and a placeholder name —
        // FolderBrowserDialog can't offer files, and this keeps the modern dialog UI.
        using var dialog = new OpenFileDialog
        {
            Title = opts.Title ?? "Select Folder or File",
            InitialDirectory = initialPath,
            RestoreDirectory = false,
            CheckFileExists = false, // allow non-file selections
            CheckPathExists = true,
            ValidateNames = false,   // allow folder paths
            FileName = FolderPlaceholder,  // the placeholder enables picking the current folder
            Filter = BuildFilterString(opts.Filters),
        };

        if (ShowDialog(dialog, owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            return FileDialogResult.Cancelled();

        var selected = ResolveFileOrFolderSelection(dialog.FileName);

        if (!File.Exists(selected) && !Directory.Exists(selected))
        {
            _logger.LogWarning("Dialog selection does not exist: {Path}", selected);
            return FileDialogResult.Cancelled();
        }

        RememberPathFireAndForget(opts, File.Exists(selected) ? Path.GetDirectoryName(selected) : selected);
        return FileDialogResult.Selected(selected);
    }

    /// <summary>
    /// The fake file name that makes an <c>OpenFileDialog</c> able to return the folder the user is
    /// standing in. Not localised, and it does not need to be: it is never shown as a label, only typed
    /// into the name box and read straight back out — see <see cref="ResolveFileOrFolderSelection"/> for
    /// why a user who happens to have a real file of this name is no longer affected by it.
    /// </summary>
    internal const string FolderPlaceholder = "Folder Selection";

    /// <summary>
    /// Work out what the user actually picked out of the file-or-folder dialog: a real path, or the
    /// <see cref="FolderPlaceholder"/> standing in for "the folder I am looking at".
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>A REAL FILE WINS OVER THE PLACEHOLDER, and that ordering is the whole fix.</b> This used to
    /// test the name FIRST — including <c>GetFileNameWithoutExtension(selected) == placeholder</c> — so
    /// picking an existing file called <c>Folder Selection.txt</c> silently returned its DIRECTORY
    /// instead of the file. Unlikely input, but a wrong ANSWER rather than a refusal, which is the class
    /// that matters. The placeholder is a name nothing put on disk, so it can only mean "this folder"
    /// when no file by that name exists.
    /// </para>
    /// <para>
    /// Pure and <c>internal</c> so it is testable with no dialog, alongside
    /// <see cref="BuildFilterString"/> and <see cref="ResolveInitialPathAsync"/> — the disambiguation is
    /// the only part of this dialog with a decision in it, and it was previously reachable only by
    /// opening one.
    /// </para>
    /// <para>
    /// This trick exists because Windows offers no "either" mode: the Common Item Dialog picks folders
    /// (<c>FOS_PICKFOLDERS</c>, what <see cref="ShowFolderBrowser"/> uses) or files, never both. So it is
    /// a workaround by necessity — worth knowing before anyone tries to delete it as a hack.
    /// </para>
    /// </remarks>
    internal static string ResolveFileOrFolderSelection(string selected)
    {
        if (File.Exists(selected)) return selected;

        var name = Path.GetFileName(selected);
        return name == FolderPlaceholder || Path.GetFileNameWithoutExtension(selected) == FolderPlaceholder
            ? Path.GetDirectoryName(selected) ?? selected
            : selected;
    }

    private FileDialogResult ShowFolderBrowser(OpenFolderOptions? opts, string initialPath, IntPtr owner)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = opts?.Title ?? "Select Folder",
            SelectedPath = initialPath,
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true, // proper title display on modern Windows
        };
        if (ShowDialog(dialog, owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return FileDialogResult.Cancelled();
        RememberPathFireAndForget(opts, dialog.SelectedPath);
        return FileDialogResult.Selected(dialog.SelectedPath);
    }

    private static DialogResult ShowDialog(CommonDialog dialog, IntPtr owner) =>
        owner != IntPtr.Zero
            ? dialog.ShowDialog(new WindowHandleWrapper(owner)) // owned: keeps z-order over the main window
            : dialog.ShowDialog();

    /// <summary>WinForms filter string from the wire-friendly rows. Internal seam for tests.</summary>
    internal static string BuildFilterString(IReadOnlyList<FileDialogFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return "All Files (*.*)|*.*";
        return string.Join("|", filters.Select(f =>
            $"{f.Name}|{string.Join(";", f.Extensions.Select(ext => $"*.{ext}"))}"));
    }

    /// <summary>
    /// Start-directory resolution (internal seam for tests): remembered path (validated, stale
    /// entries dropped) → <see cref="FileDialogOptions.DefaultPath"/> → the user's Documents.
    /// </summary>
    internal async Task<string> ResolveInitialPathAsync(FileDialogOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.RememberPathKey) && _options.PathStore is { } store)
        {
            try
            {
                var remembered = await store.GetPathAsync(options.RememberPathKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    if (Directory.Exists(remembered))
                        return remembered;
                    // Stale memory (the folder moved/was deleted) — fall through; the next
                    // successful pick overwrites it.
                    _logger.LogDebug("Remembered dialog path is gone: {Path}", remembered);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dialog path store read failed for key {Key}", options.RememberPathKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(options?.DefaultPath) && Directory.Exists(options.DefaultPath))
            return options.DefaultPath;

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>Persist the last-used directory (internal seam for tests; the dialog flow fires-and-forgets).</summary>
    internal async Task RememberPathAsync(FileDialogOptions? options, string? directory)
    {
        if (string.IsNullOrWhiteSpace(options?.RememberPathKey)
            || string.IsNullOrWhiteSpace(directory)
            || !Directory.Exists(directory)
            || _options.PathStore is not { } store)
        {
            return;
        }
        await store.SavePathAsync(options.RememberPathKey, directory).ConfigureAwait(false);
    }

    private void RememberPathFireAndForget(FileDialogOptions? options, string? directory) =>
        // Fire-and-forget from the dialog thread — persistence must never hold the dialog open.
        _ = RememberPathAsync(options, directory).ContinueWith(
            t => _logger.LogWarning(t.Exception, "Failed to remember dialog path"),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Raw-handle <see cref="IWin32Window"/> for cross-thread dialog ownership.</summary>
    private sealed class WindowHandleWrapper(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle => handle;
    }
}
