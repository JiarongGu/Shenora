using AndroidX.Activity;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Microsoft.Maui.ApplicationModel;
using Shenora;
using AndroidUri = Android.Net.Uri;
using Shenora.Modules.FileDialog;
using Shenora.Core.Shell;
using Shenora.Engine.Files;

using Shenora.Mobile;

namespace Shenora.Android;

/// <summary>
/// The Android half of <see cref="MobileFileDialogsBase"/>: saving through the Storage Access Framework's
/// <c>ACTION_CREATE_DOCUMENT</c>, reached via AndroidX's <c>CreateDocument</c> contract.
/// <para>
/// MAUI Essentials has no save picker, and the obvious third-party one (<c>FileSaver</c>) lives in
/// CommunityToolkit.Maui — a UI-component package D13 forbids the kit from taking. So this is raw
/// platform code, which is exactly what <c>Platforms/</c> exists for.
/// </para>
/// </summary>
public sealed class AndroidFileDialogs : MobileFileDialogsBase
{
    /// <inheritdoc />
    // No `= default` here: the default belongs to the base declaration, and repeating it on an
    // override is CS1066.
    public override async Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
                                                          Func<Stream, CancellationToken, Task> write,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        // ASK FIRST on this platform. The pick is cheap and cancelling is the common case, so producing
        // the content before knowing there is anywhere to put it would waste a potentially long
        // operation. (iOS cannot do this — its export picker needs the file to exist already.)
        var destination = await PickDestinationAsync(options, cancellationToken).ConfigureAwait(false);
        if (destination is null) return FileDialogResult.Cancelled();

        cancellationToken.ThrowIfCancellationRequested();

        // Produce into a cache temp, NOT straight into the user's document. If the caller throws or
        // cancels half-way, the document they picked is still whatever it was — which matters most when
        // they picked an EXISTING file to overwrite, because opening a content URI in "wt" mode
        // truncates it immediately. Same reasoning as Files.BeginReplace on the desktop.
        var temp = NewTempPath(SuggestedName(options));
        try
        {
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
            }

            await CopyToDocumentAsync(temp, destination, cancellationToken).ConfigureAwait(false);

            // No path is reported. The contract says FilePath is populated only when the host HAS an
            // addressable destination, and a content URI is a revocable grant rather than something the
            // app could legitimately reopen later — handing one back would invite exactly that.
            return FileDialogResult.Completed();
        }
        finally
        {
            // Best-effort: a leftover temp in the cache is harmless and the platform reclaims it, but a
            // failure to delete must never mask the real outcome.
            DiscardTemp(temp);
        }
    }

    /// <summary>
    /// Show the create-document picker and return the chosen URI, or null when the user cancelled.
    /// <para>
    /// Launched through <see cref="ActivityResultRelay"/> — the framework's own
    /// <c>StartActivityForResult</c> with a relay-owned request code, NOT AndroidX's registry. The
    /// relay's doc carries the measurement that forced the choice: a MAUI activity does not
    /// round-trip AndroidX instance state, so a registry registration cannot survive the host being
    /// recreated while the picker is open, while the framework's routing provably can. The price is
    /// the one-line <c>OnActivityResult</c> forward in the adopter's MainActivity (docs/ADOPTION.md).
    /// </para>
    /// <para>
    /// ⚠ PROCESS death is the boundary: the awaiting task dies with the process, so the caller's
    /// <paramref name="cancellationToken"/> is the only escape the API can honestly offer past it.
    /// </para>
    /// </summary>
    private static async Task<AndroidUri?> PickDestinationAsync(SaveFileOptions? options,
                                                                CancellationToken cancellationToken)
    {
        if (Platform.CurrentActivity is not ComponentActivity activity)
        {
            throw ShellCapability.NotSupported("Choosing a save destination", MauiShellNames.Shell,
                "there is no current Activity to show the document picker on — call this while a page is " +
                "on screen, not during startup.");
        }

        var completion = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCode = -1;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // A generic MIME type on purpose, consistent with OpenFileAsync's decision not to own an
                // extension→MIME table: the kit's filters carry EXTENSIONS, and guessing a MIME type
                // would be wrong for exactly the app-specific formats that matter. The EXTENSION still
                // reaches the picker, through the suggested file name.
                // A null result means the user backed out — the callback turns that into null, not a
                // throw, because cancelling a dialog is not an error.
                requestCode = ActivityResultRelay.Begin(activity,
                    new ActivityResultContracts.CreateDocument("application/octet-stream"),
                    SuggestedName(options), new UriCallback(completion));
            }).ConfigureAwait(false);

            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken))
                             .ConfigureAwait(false))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            // Release on every exit path or the relay entry outlives the request. It is keyed per
            // call, so leaking one is not a collision — it is an unbounded leak across a long session.
            if (requestCode >= 0) ActivityResultRelay.Complete(requestCode);
        }
    }

    /// <summary>
    /// Copy the finished temp into the document the user chose. <c>"wt"</c> truncates first, so a
    /// smaller replacement does not leave the tail of the old content behind — the classic bug when a
    /// content URI is opened in plain write mode.
    /// </summary>
    private static async Task CopyToDocumentAsync(string temp, AndroidUri destination,
                                                  CancellationToken cancellationToken)
    {
        var resolver = Platform.AppContext.ContentResolver
                       ?? throw new InvalidOperationException("No ContentResolver is available.");

        var target = resolver.OpenOutputStream(destination, "wt")
                     ?? throw new IOException("The chosen document could not be opened for writing.");

        await using (target.ConfigureAwait(false))
        {
            var source = File.OpenRead(temp);
            await using (source.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Bridges the Java callback onto the awaiting task. <c>TrySet…</c> throughout: a cancellation may
    /// already have completed the task, and the platform is free to invoke a callback more than once.
    /// </summary>
    private sealed class UriCallback(TaskCompletionSource<AndroidUri?> completion)
        : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result) =>
            completion.TrySetResult(result as AndroidUri);
    }
}
