using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using Shenora;
using Shenora.Modules.FileDialog;
using Shenora.Core.Shell;
using Shenora.Engine.Files;

namespace Shenora.Mobile;

/// <summary>
/// The MAUI implementations of <c>Shenora</c>'s shell contracts. Each is a real implementation, an
/// honest no-op the platform already satisfies, or a loud refusal — never a quiet nothing (see
/// <see cref="ShellCapability"/>).
/// </summary>
internal static class MauiShellNames
{
    /// <summary>What the refusal messages call this host.</summary>
    public const string Shell = "the MAUI shell";
}

/// <summary>
/// The mobile clipboard, over the PLATFORM's own pasteboard rather than MAUI Essentials — Essentials'
/// <c>Clipboard</c> is text-only, while <c>UIPasteboard</c> and Android's <c>ClipboardManager</c> both
/// carry pictures and typed data.
/// <para>
/// 🔴 <b><see cref="ClipboardContent.Files"/> DOES refuse, on both platforms, and that one is honest.</b>
/// "Copy these files so a file manager can paste them" is a desktop idea — neither pasteboard has an
/// expression for it, and there is no file manager on the other side to receive it.
/// </para>
/// </summary>
public sealed class MobileClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Empty means CLEAR, as on the desktop shell — SetTextAsync("") already is the clear here.
        return Clipboard.Default.SetTextAsync(text);
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync() => Clipboard.Default.GetTextAsync();

    /// <inheritdoc />
    public Task ClearAsync() => Clipboard.Default.SetTextAsync(string.Empty);

    /// <inheritdoc />
    public async Task SetAsync(ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Refuse BEFORE writing anything — the contract says nothing is written when this throws.
        if (content.Files.Count > 0)
        {
            throw ShellCapability.NotSupported("Putting FILES on the clipboard", MauiShellNames.Shell,
                "No mobile pasteboard carries a file list. Share the files instead (the platform share sheet), "
                + "or put their content on the clipboard as bytes under a media type.");
        }

        if (content.IsEmpty) { await ClearAsync().ConfigureAwait(false); return; }

        var unsupported = UnsupportedFormats(content);
        if (unsupported.Count > 0)
        {
            throw ShellCapability.NotSupported(
                $"Putting {string.Join(", ", unsupported)} on the clipboard", MauiShellNames.Shell,
                "This shell carries text and the formats listed in ClipboardContent; check what you are asking for.");
        }

        await OnMainThread(() => SetPlatformAsync(content)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ClipboardContent> GetAsync() => OnMainThread(GetPlatformAsync);

    /// <summary>Run the pasteboard work on the UI thread.</summary>
    /// <remarks>
    /// 🔴 <b>`UIPasteboard` is UIKit, and UIKit throws off the main thread</b> —
    /// <c>UIKitThreadAccessException: you are calling a UIKit method that can only be invoked from the UI
    /// thread</c>. Without this, every multi-format read and write fails.
    /// <para>
    /// ⚠ It bit only the FORMATS path: <see cref="SetTextAsync"/> and <see cref="GetTextAsync"/> go through
    /// MAUI's own <c>Clipboard.Default</c>, which marshals internally, while <see cref="SetAsync"/> and
    /// <see cref="GetAsync"/> reach the platform pasteboard directly.
    /// </para>
    /// </remarks>
    private static Task<T> OnMainThread<T>(Func<Task<T>> work) =>
        MainThread.IsMainThread ? work() : MainThread.InvokeOnMainThreadAsync(work);

    private static Task OnMainThread(Func<Task> work) =>
        MainThread.IsMainThread ? work() : MainThread.InvokeOnMainThreadAsync(work);

    /// <summary>
    /// Which of <paramref name="content"/>'s byte formats this platform has no expression for — iOS's
    /// pasteboard takes an arbitrary UTI, Android's <c>ClipData</c> does not, so an app is told exactly what
    /// it asked for that cannot happen here.
    /// </summary>
    private static IReadOnlyList<string> UnsupportedFormats(ClipboardContent content)
    {
#if IOS || MACCATALYST
        // UIPasteboard.SetData takes any pasteboard type, so nothing here is absent.
        _ = content;
        return [];
#elif ANDROID
        // ClipData carries text and HTML text directly. Everything else — a picture included — travels as a
        // content:// URI, which needs a ContentProvider the APP declares in its own manifest.
        return [.. content.Formats.Keys.Where(f => f != ClipboardContent.Html)];
#else
        return [.. content.Formats.Keys];
#endif
    }

    /// <summary>Write through the platform's own pasteboard. Text-only shells fall back to Essentials.</summary>
    private static Task SetPlatformAsync(ClipboardContent content)
    {
#if IOS || MACCATALYST
        var board = global::UIKit.UIPasteboard.General;
        // One assignment, one item: the pasteboard replaces its contents, which is the promised atomicity,
        // and the dictionary is what keeps text and picture together.
        var item = new global::Foundation.NSMutableDictionary();
        if (content.Text is { } text)
        {
            // The UTI as a literal — the binding's constant is deprecated, and this string IS the stable
            // platform identity.
            item[new global::Foundation.NSString("public.utf8-plain-text")] =
                new global::Foundation.NSString(text);
        }
        foreach (var (mediaType, bytes) in content.Formats)
        {
            // The pasteboard speaks UTIs; anything the kit does not name is the app's private type, carried
            // verbatim under its media type.
            var type = mediaType switch
            {
                ClipboardContent.PngImage => "public.png",
                ClipboardContent.Html => "public.html",
                _ => mediaType,
            };
            item[new global::Foundation.NSString(type)] =
                global::Foundation.NSData.FromArray(bytes.ToArray());
        }
        board.Items = [item];
        return Task.CompletedTask;
#elif ANDROID
        var manager = (global::Android.Content.ClipboardManager?)global::Android.App.Application.Context
            .GetSystemService(global::Android.Content.Context.ClipboardService);
        if (manager is null) return Task.CompletedTask;

        var text = content.Text ?? string.Empty;
        // NewHtmlText carries BOTH representations in one ClipData — the atomicity the contract is for.
        var wantsHtml = content.Formats.TryGetValue(ClipboardContent.Html, out var html);
        var clip = wantsHtml
            ? global::Android.Content.ClipData.NewHtmlText("Shenora", text,
                  global::System.Text.Encoding.UTF8.GetString(html.Span))
            : global::Android.Content.ClipData.NewPlainText("Shenora", text);
        manager.PrimaryClip = clip;

        // ⚠ DO NOT "verify" this write by reading PrimaryClip back here. Android restricts clipboard READS
        // to the FOCUSED app, so during startup the read-back answers as though the clip were plain text and
        // the check refuses a write that in fact succeeded — a refusal that LOOKS informed. Anything
        // measuring this must read once focus is settled (the sample's `[CLIPBOARD]` probe).
        _ = wantsHtml;
        return Task.CompletedTask;
#else
        // A shell with no platform pasteboard binding still honours the text half.
        return Clipboard.Default.SetTextAsync(content.Text ?? string.Empty);
#endif
    }

    /// <summary>Read back through the platform's own pasteboard.</summary>
    private static async Task<ClipboardContent> GetPlatformAsync()
    {
        var formats = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
#if IOS || MACCATALYST
        var board = global::UIKit.UIPasteboard.General;
        // The item's OWN types, never a fixed probe list: the write side accepts ANY media type verbatim as
        // a UTI, so probing only the two well-known UTIs silently dropped every custom format on read-back.
        // ⚠ Bounded to the kit's own shapes — this is the SYSTEM pasteboard, holding whatever the last app
        // copied (a Photos copy carries several MB-scale representations per item), and materializing every
        // foreign type on every GetAsync would read all of it into managed arrays. A kit media type contains
        // '/'; a platform UTI never does.
        foreach (var type in board.Types ?? [])
        {
            // Text is the contract's own field, read through Essentials below — not a Formats entry.
            if (type == "public.utf8-plain-text") continue;
            var mediaType = type switch
            {
                "public.png" => ClipboardContent.PngImage,
                "public.html" => ClipboardContent.Html,
                _ => type,
            };
            if (!mediaType.Contains('/')) continue;
            if (board.DataForPasteboardType(type) is { } data) formats[mediaType] = data.ToArray();
        }
#elif ANDROID
        var manager = (global::Android.Content.ClipboardManager?)global::Android.App.Application.Context
            .GetSystemService(global::Android.Content.Context.ClipboardService);
        if (manager?.PrimaryClip is { ItemCount: > 0 } clip
            && clip.GetItemAt(0)?.HtmlText is { } html)
        {
            formats[ClipboardContent.Html] = global::System.Text.Encoding.UTF8.GetBytes(html);
        }
#endif
        // Essentials for the text on every shell — the one representation all of them agree on.
        var text = await Clipboard.Default.GetTextAsync().ConfigureAwait(false);
        return new ClipboardContent { Text = text, Formats = formats };
    }
}

/// <summary>Opening a URL, over Essentials' <see cref="Browser"/>.</summary>
public sealed class MobileUrlLauncher : IUrlLauncher
{
    private readonly Action<Exception>? _onError;

    /// <param name="onError">
    /// Receives a failure. <see cref="OpenUrl"/> is void by contract while the platform API is async, so
    /// the open is started and not awaited — without this sink a failed open is invisible.
    /// </param>
    public MobileUrlLauncher(Action<Exception>? onError = null) => _onError = onError;

    /// <inheritdoc />
    public void OpenUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        // http/https only: a page must not be able to hand the shell an arbitrary scheme to launch.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Only http/https URLs can be opened (got '{url}').", nameof(url));
        }

        // Guarded continuation, never async void — that makes a rejected open an unobservable UI-thread crash.
        _ = Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred)
            .ContinueWith(t => { if (t.Exception is { } ex) _onError?.Invoke(ex.GetBaseException()); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}

/// <summary>
/// Blocking interaction with the main UI. A NO-OP, not a refusal: the things that need it — a document
/// picker, a share sheet — are already modal by the platform.
/// </summary>
public sealed class MobileUiInteraction : IUiInteraction
{
    /// <inheritdoc />
    public void BlockInteraction() { }

    /// <inheritdoc />
    public void UnblockInteraction() { }
}

/// <summary>
/// File picking over Essentials' <see cref="FilePicker"/>. SAVING is per platform —
/// <c>AndroidFileDialogs</c> and <c>IosFileDialogs</c>, each in its own shell project (see
/// <see cref="SaveAsync"/>).
/// <para>
/// ⚠ <b><c>OpenReadAsync</c> is NOT overridden because MAUI's picker COPIES the chosen document into app
/// cache</b> and returns a real filesystem path, so the interface's default path-based read is correct
/// here. The handle is a SNAPSHOT, not the live document — writing to it does not write back to the
/// user's file, and the cache can be evicted.
/// </para>
/// <para>
/// IGNORED here: <c>CheckFileExists</c>, <c>CheckPathExists</c>, <c>ValidateNames</c> and
/// <c>OverwritePrompt</c> (the picker owns validation), <c>DefaultPath</c> and <c>RememberPathKey</c>
/// (no addressable start directory), and <c>DefaultExtension</c>. <c>Title</c> and <c>Filters</c> map.
/// </para>
/// </summary>
public abstract class MobileFileDialogsBase : IFileDialogs
{
    /// <summary>
    /// Pick a destination and write to it — the portable save, native per platform:
    /// <c>ACTION_CREATE_DOCUMENT</c> on Android, <c>UIDocumentPickerViewController</c> on iOS. Both produce
    /// into a CACHE TEMP first, so the user's existing document is untouched until the content is complete.
    /// <para>
    /// ⚠ <b>Do not assume the pick happens before the write</b> — iOS must produce first, because its export
    /// picker hands over a file that already exists. Treat <paramref name="write"/> as "may run even if the
    /// user ultimately cancels".
    /// </para>
    /// </summary>
    public abstract Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
                                                     Func<Stream, CancellationToken, Task> write,
                                                     CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public async Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = options?.Title,
            // FileTypes stays unset: Android matches on MIME types while the kit's filters carry
            // EXTENSIONS, and an extension→MIME table would be wrong for exactly the app-specific formats
            // that matter.
        }).ConfigureAwait(false);

        // FullPath is the host-resolvable form on each platform, which is what the contract asks for.
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
    /// A cache file to produce content into before handing it to the platform — the space both platforms
    /// let an app write without a grant.
    /// <para>
    /// ⚠ <b>Uniqueness goes in the DIRECTORY, never in the file NAME.</b> iOS's export picker suggests the
    /// temp file's own name to the user, so a <c>{guid}-name.txt</c> temp reaches the "Save as" field as the
    /// guid. Invisible on Android, which passes the suggested name separately.
    /// </para>
    /// </summary>
    protected static string NewTempPath(string? suggestedName)
    {
        var name = string.IsNullOrWhiteSpace(suggestedName) ? "save" : Path.GetFileName(suggestedName);
        var directory = Path.Combine(FileSystem.CacheDirectory, "shenora-save", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    /// <summary>Drop a temp produced by <see cref="NewTempPath"/>, its per-call directory included —
    /// otherwise every save leaks an empty folder into the cache. Never throws.</summary>
    protected static void DiscardTemp(string tempPath)
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
    /// <see cref="SaveFileOptions.DefaultExtension"/> appended when it carries none of its own; defaults to
    /// <c>untitled</c>. The NAME is the only place an extension can be expressed here.
    /// </summary>
    protected static string SuggestedName(SaveFileOptions? options)
    {
        var name = options?.FileName;
        if (string.IsNullOrWhiteSpace(name)) name = "untitled";
        if (!Path.HasExtension(name) && options?.DefaultExtension is { Length: > 0 } extension)
            name = $"{name}.{extension.TrimStart('.')}";
        return name;
    }
}
