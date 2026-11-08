using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shenora;
using Shenora.Modules.FileDialog;
using Shenora.Core.Shell;
using Shenora.Engine.Files;

namespace Shenora.Windows;

/// <summary>Inputs for <see cref="FileDialogs"/>.</summary>
public sealed class FileDialogsOptions
{
    /// <summary>Blocks the main window while a dialog is up + supplies the owner handle. Null = neither.</summary>
    public IFormInteraction? Interaction { get; init; }

    /// <summary>Backs <see cref="FileDialogOptions.RememberPathKey"/>. Null = no path memory.</summary>
    public IFileDialogPathStore? PathStore { get; init; }
}

/// <summary>
/// Native dialogs. 🔴 Every dialog runs on a DEDICATED STA thread, never inline — a dialog on the
/// WebView2's UI thread conflicts with its message handling. Owned by the window showing the page that
/// asked (<see cref="PageWindow"/>), else the main window, for correct z-order, with that window and no other blocked while up,
/// and per-key last-directory memory through <see cref="IFileDialogPathStore"/>. A cancelled dialog is
/// <see cref="FileDialogResult.Cancelled"/>, never an exception.
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

    /// <summary>The shared flow: resolve the owner and start dir → block the owner → dedicated STA thread → unblock.</summary>
    private async Task<FileDialogResult> ShowAsync<TOptions>(
        TOptions? options, Func<TOptions?, string, IntPtr, FileDialogResult> show)
        where TOptions : FileDialogOptions
    {
        // The owner first, before any await: it is read from the request's context, which is the caller's.
        var owner = ResolveOwner();
        // Load the start directory BEFORE showing (the store may be async I/O).
        var initialPath = await ResolveInitialPathAsync(options).ConfigureAwait(false);

        var blocked = Block(owner);
        try
        {
            return await StaThread.RunAsync(() => show(options, initialPath, owner.Handle)).ConfigureAwait(false);
        }
        finally
        {
            Unblock(blocked);
        }
    }

    /// <summary>Who a dialog opens over: the window and its handle (zero for none).</summary>
    internal readonly record struct DialogOwner(Form? Window, IntPtr Handle);

    /// <summary>
    /// The window to own a dialog now: the one showing the page that asked (<see cref="PageWindow.Current"/>) while it is
    /// open and shown, else the main window, else none. A request from a secondary window is answered over that window,
    /// and a hidden or closed one falls back rather than opening a dialog over nothing.
    /// </summary>
    internal DialogOwner ResolveOwner()
    {
        if (PageWindow.Current is { IsDisposed: false, IsHandleCreated: true, Visible: true } asking)
            return new DialogOwner(asking, asking.Handle);
        return new DialogOwner(_options.Interaction?.GetMainForm(), _options.Interaction?.GetMainFormHandle() ?? IntPtr.Zero);
    }

    // The main window blocks through the app's IFormInteraction, which may be its own; any other owner, here.
    private Form? Block(DialogOwner owner)
    {
        if (owner.Window is { } window && window != _options.Interaction?.GetMainForm())
        {
            WindowBlock.Block(window);
            return window;
        }

        _options.Interaction?.BlockInteraction();
        return null;
    }

    private void Unblock(Form? other)
    {
        if (other is not null) WindowBlock.Unblock(other);
        else _options.Interaction?.UnblockInteraction();
    }

    private FileDialogResult ShowFileOrFolderDialog(OpenFolderOptions opts, string initialPath, IntPtr owner)
    {
        // "Folder OR file" via OpenFileDialog with relaxed validation and a placeholder name:
        // FolderBrowserDialog cannot offer files, and this keeps the modern dialog UI.
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
            AppCallback.Log(_logger, () => $"Dialog selection does not exist: {selected}", LogLevel.Warning);
            return FileDialogResult.Cancelled();
        }

        RememberPathFireAndForget(opts, File.Exists(selected) ? Path.GetDirectoryName(selected) : selected);
        return FileDialogResult.Selected(selected);
    }

    /// <summary>
    /// The fake file name that makes an <c>OpenFileDialog</c> able to return the folder the user is
    /// standing in. Not localised, and it does not need to be: it is never shown as a label, only typed
    /// into the name box and read straight back out.
    /// </summary>
    internal const string FolderPlaceholder = "Folder Selection";

    /// <summary>
    /// Work out what the user actually picked out of the file-or-folder dialog: a real path, or the
    /// <see cref="FolderPlaceholder"/> standing in for "the folder I am looking at".
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>A REAL FILE WINS OVER THE PLACEHOLDER, and the ordering is the point.</b> Testing the name
    /// first meant picking an existing file called <c>Folder Selection.txt</c> silently returned its
    /// DIRECTORY instead of the file — a wrong answer rather than a refusal.
    /// </para>
    /// <para>
    /// The placeholder trick exists because Windows offers no "either" mode: the Common Item Dialog picks
    /// folders (<c>FOS_PICKFOLDERS</c>, what <see cref="ShowFolderBrowser"/> uses) or files, never both.
    /// A workaround by necessity, not a hack to delete.
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
                    AppCallback.Log(_logger, () => $"Remembered dialog path is gone: {remembered}", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                AppCallback.Log(_logger, () => $"Dialog path store read failed for key {options.RememberPathKey}", LogLevel.Warning, ex);
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
            t => AppCallback.Log(_logger, () => "Failed to remember dialog path", LogLevel.Warning, t.Exception),
            TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Raw-handle <see cref="IWin32Window"/> for cross-thread dialog ownership.</summary>
    private sealed class WindowHandleWrapper(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle => handle;
    }
}
