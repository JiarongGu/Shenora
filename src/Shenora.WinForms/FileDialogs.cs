using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora.Core;

namespace Shenora.WinForms;

// The dialog CONTRACT (IFileDialogs, IFileDialogPathStore, FileDialogOptions/Filter/Result) moved to
// Shenora.Core in P5.5 H4.1 so app logic that picks files compiles with no Windows reference (D20).
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

    public FileDialogs(FileDialogsOptions? options = null, ILogger<FileDialogs>? logger = null)
    {
        _options = options ?? new FileDialogsOptions();
        _logger = logger ?? NullLogger<FileDialogs>.Instance;
    }

    /// <inheritdoc />
    public Task<FileDialogResult> OpenFileAsync(FileDialogOptions? options = null) =>
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
    public Task<FileDialogResult> OpenFolderAsync(FileDialogOptions? options = null) =>
        ShowAsync(options, (opts, initialPath, owner) =>
            opts?.AllowFileSelection == true
                ? ShowFileOrFolderDialog(opts, initialPath, owner)
                : ShowFolderBrowser(opts, initialPath, owner));

    /// <inheritdoc />
    public Task<FileDialogResult> SaveFileAsync(FileDialogOptions? options = null) =>
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
    private async Task<FileDialogResult> ShowAsync(
        FileDialogOptions? options, Func<FileDialogOptions?, string, IntPtr, FileDialogResult> show)
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

    private FileDialogResult ShowFileOrFolderDialog(FileDialogOptions opts, string initialPath, IntPtr owner)
    {
        // "Folder OR file" via OpenFileDialog with relaxed validation and a placeholder name —
        // FolderBrowserDialog can't offer files, and this keeps the modern dialog UI.
        const string placeholder = "Folder Selection";
        using var dialog = new OpenFileDialog
        {
            Title = opts.Title ?? "Select Folder or File",
            InitialDirectory = initialPath,
            RestoreDirectory = false,
            CheckFileExists = false, // allow non-file selections
            CheckPathExists = true,
            ValidateNames = false,   // allow folder paths
            FileName = placeholder,  // the placeholder enables picking the current folder
            Filter = BuildFilterString(opts.Filters),
        };

        if (ShowDialog(dialog, owner) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            return FileDialogResult.Cancelled();

        var selected = dialog.FileName;
        // Picking the placeholder means "this folder".
        if (Path.GetFileName(selected) == placeholder || Path.GetFileNameWithoutExtension(selected) == placeholder)
            selected = Path.GetDirectoryName(selected) ?? selected;

        if (!File.Exists(selected) && !Directory.Exists(selected))
        {
            _logger.LogWarning("Dialog selection does not exist: {Path}", selected);
            return FileDialogResult.Cancelled();
        }

        RememberPathFireAndForget(opts, File.Exists(selected) ? Path.GetDirectoryName(selected) : selected);
        return FileDialogResult.Selected(selected);
    }

    private FileDialogResult ShowFolderBrowser(FileDialogOptions? opts, string initialPath, IntPtr owner)
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
