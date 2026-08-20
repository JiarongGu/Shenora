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
/// The mobile clipboard, over the PLATFORM's own pasteboard rather than MAUI Essentials.
/// <para>
/// ⚠ <b>Essentials' <c>Clipboard</c> is text-only, and that is an Essentials limit, not a platform
/// one</b> — <c>UIPasteboard</c> and Android's <c>ClipboardManager</c> both carry pictures and typed
/// data. <see cref="ShellCapability"/>'s rule is that a refusal means the capability is genuinely
/// ABSENT here, so refusing an image because the convenience wrapper lacks one would have been the
/// wrong kind of "no". Text and the byte formats go through the platform APIs below.
/// </para>
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

        // Refuse BEFORE writing anything: the contract says nothing is written when this throws, and a
        // half-applied clipboard is worse than a refused one.
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

    /// <summary>
    /// Run the pasteboard work on the UI thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>`UIPasteboard` is UIKit, and UIKit throws off the main thread</b> —
    /// <c>UIKitThreadAccessException: you are calling a UIKit method that can only be invoked from the UI
    /// thread</c>. Without this, every multi-format read and write fails.
    /// </para>
    /// <para>
    /// ⚠ <b>It bit only the FORMATS path, which is why a compile and the text path both looked fine.</b>
    /// <see cref="SetTextAsync"/> and <see cref="GetTextAsync"/> go through MAUI's own
    /// <c>Clipboard.Default</c>, which marshals internally; <see cref="SetAsync"/> and
    /// <see cref="GetAsync"/> reach the platform pasteboard directly and had nothing doing it for them.
    /// </para>
    /// <para>
    /// ⚠ <b>An async API that must be called from the UI thread is a trap</b>, and the caller cannot see
    /// it: the signature says "await me", so awaiting it from a background thread — which is what
    /// `Task.Run` and every library continuation give you — is the natural thing to write. The kit
    /// marshals rather than documenting a rule nobody will read at the call site.
    /// </para>
    /// </remarks>
    private static Task<T> OnMainThread<T>(Func<Task<T>> work) =>
        MainThread.IsMainThread ? work() : MainThread.InvokeOnMainThreadAsync(work);

    private static Task OnMainThread(Func<Task> work) =>
        MainThread.IsMainThread ? work() : MainThread.InvokeOnMainThreadAsync(work);

    /// <summary>
    /// Which of <paramref name="content"/>'s byte formats this platform has no expression for. ⚠ The
    /// answer differs per platform on purpose — iOS's pasteboard takes an arbitrary UTI, Android's
    /// <c>ClipData</c> does not — so an app is told exactly what it asked for that cannot happen here,
    /// rather than "clipboard not supported".
    /// </summary>
    private static IReadOnlyList<string> UnsupportedFormats(ClipboardContent content)
    {
#if IOS || MACCATALYST
        // UIPasteboard.SetData takes any pasteboard type, so nothing here is absent.
        _ = content;
        return [];
#elif ANDROID
        // ClipData carries text and HTML text directly. Everything else — a picture included — travels as
        // a content:// URI, which needs a ContentProvider the APP declares in its own manifest; the kit
        // cannot supply one on its behalf, so it says so rather than inventing a path that fails later.
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
        // One assignment, one item — the pasteboard replaces its contents, which is the atomicity the
        // contract promises. Building the dictionary first is what keeps text and picture together.
        var item = new global::Foundation.NSMutableDictionary();
        if (content.Text is { } text)
        {
            // The UTI as a literal: the binding's constant is deprecated in favour of a type the kit
            // would otherwise have to reference, and this string IS the stable platform identity.
            item[new global::Foundation.NSString("public.utf8-plain-text")] =
                new global::Foundation.NSString(text);
        }
        foreach (var (mediaType, bytes) in content.Formats)
        {
            // The pasteboard speaks UTIs; the two the kit names have well-known ones, and anything else
            // is the app's private type, carried verbatim under its media type.
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
        // NewHtmlText carries BOTH representations in one ClipData, which is exactly the atomicity the
        // contract is for: a receiver that wants markup gets it, a plain-text receiver gets the text.
        var wantsHtml = content.Formats.TryGetValue(ClipboardContent.Html, out var html);
        var clip = wantsHtml
            ? global::Android.Content.ClipData.NewHtmlText("Shenora", text,
                  global::System.Text.Encoding.UTF8.GetString(html.Span))
            : global::Android.Content.ClipData.NewPlainText("Shenora", text);
        manager.PrimaryClip = clip;

        // ⚠ **DO NOT "verify" this write by reading PrimaryClip back here.** That was tried and reverted
        // the same day: Android restricts clipboard READS to the focused app, so during startup the
        // read-back answers as though the clip were plain text, and the check refuses a write that in
        // fact succeeded. Turning a working capability into a named refusal is worse than the silent
        // drop it was meant to replace, and the failure is invisible because the refusal LOOKS informed.
        // Anything measuring this must read once focus is settled — see the sample's `[CLIPBOARD]` probe.
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
        // The item's OWN types, never a fixed probe list: the write side accepts ANY media type
        // verbatim as a UTI (UnsupportedFormats is empty here), so probing only the two well-known
        // UTIs silently dropped every custom format on read-back — the round-trip
        // ClipboardContent.Formats exists to promise.
        // ⚠ Bounded to the kit's own shapes. This is the SYSTEM pasteboard, holding whatever the last
        // app copied — a Photos copy carries several MB-scale representations per item — and
        // materializing every foreign type on every GetAsync would read all of it into managed arrays
        // for nothing. The kit writes media types, which contain '/'; a platform UTI never does, so
        // the filter is exactly "what this contract can round-trip".
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
        // Essentials for the text on every shell: it is the one representation all of them agree on, and
        // its null-versus-empty behaviour is already the contract's.
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

        // Guarded continuation, never async void: that would make a rejected open an unobservable
        // crash on the UI thread.
        _ = Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred)
            .ContinueWith(t => { if (t.Exception is { } ex) _onError?.Invoke(ex.GetBaseException()); },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}

/// <summary>
/// Blocking interaction with the main UI. A NO-OP rather than a refusal: the things that need it — a
/// document picker, a share sheet — are already modal by the platform, so refusing here would break
/// portable logic that correctly brackets a picker call.
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
/// ⚠ <b><c>OpenReadAsync</c> is NOT overridden because MAUI's picker COPIES the chosen document into
/// app cache</b> and returns a real filesystem path, so the interface's default path-based read is
/// correct here. The copy is what a caller must account for: the handle is a SNAPSHOT, not the live
/// document — writing to it does not write back to the user's file, and the cache can be evicted.
/// </para>
/// <para>
/// IGNORED here: <c>CheckFileExists</c>, <c>CheckPathExists</c>, <c>ValidateNames</c> and
/// <c>OverwritePrompt</c> (the picker owns validation), <c>DefaultPath</c> and <c>RememberPathKey</c>
/// (no addressable start directory), and <c>DefaultExtension</c>. <c>Title</c> and <c>Filters</c> map.
/// </para>
/// <para>
/// <b>SAVING is per platform</b> — <c>AndroidFileDialogs</c> and <c>IosFileDialogs</c>, each in its own
/// shell project — so a third platform joining this shared source cannot compile until it says what save
/// means there. See <see cref="SaveAsync"/>.
/// </para>
/// </summary>
public abstract class MobileFileDialogsBase : IFileDialogs
{
    /// <summary>
    /// Pick a destination and write to it — the portable save, native per platform:
    /// <c>ACTION_CREATE_DOCUMENT</c> on Android, <c>UIDocumentPickerViewController</c> on iOS. Both
    /// produce the content into a CACHE TEMP first, so the user's existing document is untouched until
    /// the content is complete.
    /// <para>
    /// <b>⚠ Do not assume the pick happens before the write.</b> Android asks first and then produces (a
    /// cancel costs nothing); iOS must produce first, because its export picker hands over a file that
    /// already exists. Treat <paramref name="write"/> as "may run even if the user ultimately cancels".
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
            // EXTENSIONS, so honouring them needs an extension→MIME table that would be wrong for
            // exactly the app-specific formats that matter. An app needing narrowing passes its own
            // PickOptions through its own contract.
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
    /// ⚠ <b>Uniqueness goes in the DIRECTORY, never in the file NAME.</b> iOS's export picker suggests
    /// the temp file's own name to the user, so a <c>{guid}-name.txt</c> temp reaches the "Save as" field
    /// as the guid. Invisible on Android, which passes the suggested name separately to <c>Launch()</c>.
    /// </para>
    /// </summary>
    protected static string NewTempPath(string? suggestedName)
    {
        var name = string.IsNullOrWhiteSpace(suggestedName) ? "save" : Path.GetFileName(suggestedName);
        var directory = Path.Combine(FileSystem.CacheDirectory, "shenora-save", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    /// <summary>
    /// Drop a temp produced by <see cref="NewTempPath"/>, its per-call directory included — otherwise
    /// every save leaks an empty folder into the cache for the life of the install. Never throws.
    /// </summary>
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
    /// <see cref="SaveFileOptions.DefaultExtension"/> appended when it carries no extension of its own.
    /// Defaults to <c>untitled</c>. The NAME is the only place an extension can be expressed here — the
    /// platform implementations keep the MIME type generic.
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
