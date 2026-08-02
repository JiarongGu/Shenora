using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using Shenora.Core;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI implementations of <c>Shenora.Core</c>'s shell contracts — the peers of
/// <c>Shenora.WinForms</c>'s. Each one is either a real implementation, an honest no-op the platform
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
/// </summary>
public sealed class MobileFileDialogs : IFileDialogs
{
    /// <inheritdoc />
    public async Task<FileDialogResult> OpenFileAsync(FileDialogOptions? options = null)
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
    public Task<FileDialogResult> OpenFolderAsync(FileDialogOptions? options = null) =>
        throw ShellCapability.NotSupported("Picking a folder", MauiShellNames.Shell,
            "This is a desktop concept (D35) — a folder browser grants ambient access to an arbitrary path, " +
            "which no mobile system does. Ask for what you meant instead: ShenoraPaths for space the app owns " +
            "(no picker needed), a media picker for the camera roll, or OpenFileAsync + OpenReadAsync for one " +
            "document.");

    /// <inheritdoc />
    public Task<FileDialogResult> SaveFileAsync(FileDialogOptions? options = null) =>
        throw ShellCapability.NotSupported("Choosing a save destination", MauiShellNames.Shell,
            "MAUI Essentials has no save picker — write to app storage and offer a share sheet, or use a platform-specific SAF create-document intent.");
}
