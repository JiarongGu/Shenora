using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Modules.FileDialog;

namespace Shenora.Chromium.Host;

/// <summary>
/// The Chromium shell's <see cref="IFileDialogs"/>: CEF's own file dialog, which is each OS's native one, over the
/// main window. The contract's semantics are the WebView2 shell's <c>FileDialogs</c>: the start folder is the
/// remembered one (<see cref="IFileDialogPathStore"/>, dropped when it no longer exists), then
/// <see cref="FileDialogOptions.DefaultPath"/>, then Documents; a successful pick is remembered; the titles default
/// alike.
/// <para>
/// ⚠ What CEF cannot express. <see cref="OpenFolderOptions.AllowFileSelection"/> is a folder pick: CEF has no
/// file-or-folder mode, and its one folder mode is Chromium's upload-folder, whose accept button reads "Upload".
/// <see cref="SaveFileOptions.DefaultExtension"/> is appended here when the user typed no extension. The remaining
/// desktop hints (existence and name checks, the overwrite prompt) are the OS dialog's own.
/// </para>
/// <para>
/// The start folder is Chromium's own last-used folder for the profile, set before each dialog, because CEF passes
/// on no folder of its own; so a page's own <c>&lt;input type="file"&gt;</c> afterwards opens there too.
/// </para>
/// </summary>
internal sealed class ChromiumFileDialogs(ChromiumWindows windows, CefUiDispatcher ui, IFileDialogPathStore? store, ILogger? log) : IFileDialogs
{
    public Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null) =>
        ShowAsync(options, cef_file_dialog_mode_t.FILE_DIALOG_OPEN, options?.Title ?? "Select File", options?.FileName, options?.Filters,
            picked => (picked, Path.GetDirectoryName(picked)));

    public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null) =>
        ShowAsync(options, cef_file_dialog_mode_t.FILE_DIALOG_OPEN_FOLDER, options?.Title ?? "Select Folder", null, null,
            picked => (picked, picked));

    public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null) =>
        ShowAsync(options, cef_file_dialog_mode_t.FILE_DIALOG_SAVE, options?.Title ?? "Save File", options?.FileName, options?.Filters,
            picked =>
            {
                var path = WithDefaultExtension(picked, options?.DefaultExtension);
                return (path, Path.GetDirectoryName(path));
            });

    /// <summary>CEF's filter form, <c>Images|.png;.jpg</c>, from the wire's rows. None = all files.</summary>
    internal static IReadOnlyList<string> AcceptFilters(IReadOnlyList<FileDialogFilter>? filters) =>
        filters is not { Count: > 0 }
            ? []
            : [.. filters.Select(f => $"{f.Name}|{string.Join(";", f.Extensions.Select(e => "." + e.TrimStart('.')))}")];

    internal static string WithDefaultExtension(string path, string? extension) =>
        string.IsNullOrWhiteSpace(extension) || Path.HasExtension(path) ? path : $"{path}.{extension.TrimStart('.')}";

    private async Task<FileDialogResult> ShowAsync(FileDialogOptions? options, cef_file_dialog_mode_t mode, string title, string? fileName,
        IReadOnlyList<FileDialogFilter>? filters, Func<string, (string Path, string? Directory)> accept)
    {
        var initial = await ResolveInitialPathAsync(options).ConfigureAwait(false);
        var picked = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = AcceptFilters(filters);
        var posted = ui.Post(() =>
        {
            if (!windows.RunFileDialog(mode, title, initial, fileName, accepted, files => picked.TrySetResult(files)))
                picked.TrySetException(new InvalidOperationException("No Chromium window is open to own the file dialog."));
        });
        if (!posted) throw new InvalidOperationException("The Chromium shell is not running.");

        var files = await picked.Task.ConfigureAwait(false);
        if (files.Length == 0 || string.IsNullOrWhiteSpace(files[0])) return FileDialogResult.Cancelled();
        var (path, directory) = accept(files[0]);
        await RememberAsync(options, directory).ConfigureAwait(false);
        return FileDialogResult.Selected(path);
    }

    internal async Task<string> ResolveInitialPathAsync(FileDialogOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.RememberPathKey) && store is not null)
        {
            try
            {
                var remembered = await store.GetPathAsync(options.RememberPathKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered)) return remembered;
            }
            catch (Exception ex)
            {
                AppCallback.Log(log, () => $"[Shenora.Chromium] The dialog path store failed to read '{options.RememberPathKey}'", LogLevel.Warning, ex);
            }
        }
        if (!string.IsNullOrWhiteSpace(options?.DefaultPath) && Directory.Exists(options.DefaultPath)) return options.DefaultPath;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // Never allowed to fail the pick the user already made.
    internal async Task RememberAsync(FileDialogOptions? options, string? directory)
    {
        if (string.IsNullOrWhiteSpace(options?.RememberPathKey) || string.IsNullOrWhiteSpace(directory)
            || !Directory.Exists(directory) || store is null) return;
        try { await store.SavePathAsync(options.RememberPathKey, directory).ConfigureAwait(false); }
        catch (Exception ex) { AppCallback.Log(log, () => "[Shenora.Chromium] Remembering the dialog path failed", LogLevel.Warning, ex); }
    }
}
