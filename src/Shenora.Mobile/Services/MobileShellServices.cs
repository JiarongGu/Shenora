using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using Shenora;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI implementations of <c>Shenora</c>'s shell contracts — the peers of
/// <c>Shenora.Windows</c>'s. Each one is either a real implementation, an honest no-op the platform
/// already satisfies, or a loud refusal; never a quiet nothing (see <see cref="ShellCapability"/>).
/// </summary>
internal static class MauiShellNames
{
    /// <summary>What the refusal messages call this host.</summary>
    public const string Shell = "the MAUI shell";
}

/// <summary>
/// Clipboard over MAUI Essentials. Text works everywhere; the two IMAGE members refuse, because
/// Essentials' clipboard is text-only — there is no image API to call, so "absent" is literally true
/// rather than a shortcut.
/// </summary>
public sealed class MobileClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Empty means CLEAR, matching the desktop implementation's rule — the WinForms one routes an
        // empty string to Clipboard.Clear() because an empty selection is app DATA, not a bug. Here
        // SetTextAsync("") is already the clear, so the two shells agree without a special case.
        return Clipboard.Default.SetTextAsync(text);
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync() => Clipboard.Default.GetTextAsync();

    /// <inheritdoc />
    public Task SetImageFromFileAsync(string imagePath) =>
        throw ShellCapability.NotSupported("Putting an image on the clipboard", MauiShellNames.Shell,
            "MAUI Essentials' clipboard carries text only — use the platform share sheet (IShareTarget in your app) for images.");

    /// <inheritdoc />
    public Task<bool> TrySaveImageToFileAsync(string targetPath) =>
        throw ShellCapability.NotSupported("Reading an image from the clipboard", MauiShellNames.Shell,
            "MAUI Essentials' clipboard carries text only.");
}

/// <summary>Opening a URL, over Essentials' <see cref="Browser"/>.</summary>
public sealed class MobileUrlLauncher : IUrlLauncher
{
    private readonly Action<Exception>? _onError;

    /// <param name="onError">
    /// Receives a failure. <see cref="OpenUrl"/> is void by contract while the platform API is async,
    /// so the open is started and not awaited — without this sink a failure is invisible.
    /// </param>
    public MobileUrlLauncher(Action<Exception>? onError = null) => _onError = onError;

    /// <inheritdoc />
    public void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        // http/https only, the same gate the desktop launcher enforces: a page must not be able to
        // hand the shell an arbitrary scheme to launch.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Only http/https URLs can be opened (got '{url}').", nameof(url));
        }

        // Fire-and-forget with a guarded continuation — never async void, which would make a
        // rejected open an unobservable crash on the UI thread.
        _ = Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred)
            .ContinueWith(t => { if (t.Exception is { } ex) _onError?.Invoke(ex.GetBaseException()); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}

/// <summary>
/// Blocking interaction with the main UI. An honest NO-OP rather than a refusal: on mobile the
/// things that need it — a document picker, a share sheet — are modal by the platform, so the
/// capability is satisfied, just not by us. That distinction is the one <see cref="ShellCapability"/>
/// draws; refusing here would break portable logic that correctly brackets a picker call.
/// </summary>
public sealed class MobileUiInteraction : IUiInteraction
{
    /// <inheritdoc />
    public void BlockInteraction() { }

    /// <inheritdoc />
    public void UnblockInteraction() { }
}

/// <summary>
/// File picking over Essentials' <see cref="FilePicker"/>.
/// <para>
/// This is the first REAL test of the lean <c>FileDialogContracts.cs</c> admits in writing — that
/// <c>FileDialogOptions</c> is desktop-flavoured and "a mobile document picker would ignore the
/// validation hints and return a content URI". The finding: the contract holds, and no break is
/// needed for opening a file. <see cref="FileDialogResult.FilePath"/> is specified as "a path or URI
/// the HOST can resolve", which is exactly what Android hands back.
/// </para>
/// <para>
/// <b>It does NOT override <c>OpenReadAsync</c>, and that was measured rather than assumed.</b>
/// MAUI's picker COPIES the chosen document into app cache and returns a real filesystem path
/// (<c>/data/data/&lt;pkg&gt;/cache/…/name.ext</c>) rather than a content URI, so the interface's
/// default path-based read is already correct on this shell. Verified on a device — the sample's
/// PICK_FILE route returned exactly such a path.
/// The semantic difference that copy introduces matters more than the API shape: the handle is a
/// SNAPSHOT, not the live document. Writing to it does not write back to the user's file, and the
/// cache can be evicted. Read it promptly; do not treat it as durable storage.
/// </para>
/// <para>
/// What is IGNORED here, stated rather than discovered: <c>CheckFileExists</c>,
/// <c>CheckPathExists</c>, <c>ValidateNames</c> and <c>OverwritePrompt</c> (the picker owns
/// validation), <c>DefaultPath</c> and <c>RememberPathKey</c> (no addressable start directory), and
/// <c>DefaultExtension</c>. <c>Title</c> and <c>Filters</c> DO map.
/// </para>
/// <para>
/// <b>SAVING is implemented PER PLATFORM</b> (<c>Platforms/Android/</c>, <c>Platforms/iOS/</c>) because
/// the two systems express it differently and neither resembles a desktop save dialog — see
/// <see cref="SaveAsync"/>. It is a <c>partial</c> method rather than a virtual with a fallback on
/// purpose: a THIRD platform joining this shared source cannot compile until someone decides what save
/// means there, instead of silently inheriting a stub that refuses at runtime.
/// </para>
/// </summary>
public sealed partial class MobileFileDialogs : IFileDialogs
{
    /// <summary>
    /// Pick a destination and write to it — the portable save, implemented natively per platform:
    /// <c>ACTION_CREATE_DOCUMENT</c> on Android, <c>UIDocumentPickerViewController</c> on iOS.
    /// <para>
    /// <b>Both platforms produce the content into a CACHE TEMP first and only then hand it over</b>, so
    /// the user's existing document is untouched until the content is complete — the same reasoning as
    /// the desktop's <c>Files.BeginReplace</c>, applied to a destination that is a system grant rather
    /// than a path. The remaining window is the hand-over copy itself, which is bounded by copy speed
    /// rather than by however long the caller's operation takes; that difference is the whole point,
    /// because a long encode is where an in-place write does its damage.
    /// </para>
    /// <para>
    /// <b>⚠ Do not assume the pick happens before the write.</b> Android asks first and then produces
    /// (so a cancel costs nothing); iOS must produce first, because its export picker hands over a file
    /// that already exists — so a cancel there wastes the work. Portable logic must therefore treat the
    /// callback as "may run even if the user ultimately cancels".
    /// </para>
    /// </summary>
    public partial Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
                                                    Func<Stream, CancellationToken, Task> write,
                                                    CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public async Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = options?.Title,
            // FileTypes is left unset ON PURPOSE. Android matches on MIME types while the kit's
            // filters carry EXTENSIONS, so honouring them would need an extension→MIME table the kit
            // has no business owning and would get wrong for exactly the app-specific formats that
            // matter. "Any file" is the honest answer; an app that needs narrowing passes its own
            // PickOptions through its own contract.
        }).ConfigureAwait(false);

        // FullPath is the host-resolvable form on each platform (a content URI on Android) — which is
        // what the contract asks for, and why nothing has to break here.
        return result is null ? FileDialogResult.Cancelled() : FileDialogResult.Selected(result.FullPath);
    }

    /// <inheritdoc />
    public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null) =>
        throw ShellCapability.NotSupported("Picking a folder", MauiShellNames.Shell,
            "This is a desktop concept (D35) — a folder browser grants ambient access to an arbitrary path, " +
            "which no mobile system does. Ask for what you meant instead: ShenoraPaths for space the app owns " +
            "(no picker needed), a media picker for the camera roll, or OpenFileAsync + OpenReadAsync for one " +
            "document.");

    /// <inheritdoc />
    public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null) =>
        throw ShellCapability.NotSupported("Choosing a save destination as a PATH", MauiShellNames.Shell,
            "No mobile system has that concept: the user grants access to one document and the app writes " +
            "INTO it while the grant is live, so there is no path to hand back. Use SaveAsync(options, write) " +
            "instead — it is implemented here, and it is the portable shape on every shell (D35).");

    /// <summary>
    /// A cache file to produce content into before handing it to the platform, under the cache directory
    /// because that is the space both platforms let an app write without a grant.
    /// <para>
    /// ⚠ <b>Uniqueness goes in the DIRECTORY, never in the file NAME.</b> iOS's export picker suggests
    /// the temp file's own name to the user, so a <c>{guid}-name.txt</c> temp showed up in the "Save as"
    /// field as <c>89c9bdcc7248436…</c> — found on the simulator, and invisible on Android, where the
    /// suggested name is passed separately to <c>Launch()</c> and the temp's name never surfaces. A
    /// per-call directory gives the same collision safety with the filename left alone.
    /// </para>
    /// </summary>
    private static string NewTempPath(string? suggestedName)
    {
        var name = string.IsNullOrWhiteSpace(suggestedName) ? "save" : Path.GetFileName(suggestedName);
        var directory = Path.Combine(FileSystem.CacheDirectory, "shenora-save", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    /// <summary>
    /// Drop a temp produced by <see cref="NewTempPath"/>, its per-call directory included — otherwise
    /// every save leaks an empty folder into the cache for the life of the install. Best-effort by
    /// design: a cache the OS reclaims is not worth masking a real outcome for.
    /// </summary>
    private static void DiscardTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            var directory = Path.GetDirectoryName(tempPath);
            if (directory is { Length: > 0 } && Directory.Exists(directory)) Directory.Delete(directory);
        }
        catch
        {
            // cache; the platform reclaims it
        }
    }

    /// <summary>
    /// The name to suggest in the picker: the caller's <see cref="SaveFileOptions.FileName"/>, with
    /// <see cref="SaveFileOptions.DefaultExtension"/> appended when it carries no extension of its
    /// own. Unlike the desktop, the NAME is the only place an extension can be expressed here — the
    /// MIME type is deliberately generic (see the platform implementations).
    /// </summary>
    private static string SuggestedName(SaveFileOptions? options)
    {
        var name = options?.FileName;
        if (string.IsNullOrWhiteSpace(name)) name = "untitled";
        if (!Path.HasExtension(name) && options?.DefaultExtension is { Length: > 0 } extension)
            name = $"{name}.{extension.TrimStart('.')}";
        return name;
    }
}
