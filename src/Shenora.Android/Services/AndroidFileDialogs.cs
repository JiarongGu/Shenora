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
/// <c>ACTION_CREATE_DOCUMENT</c>, reached via AndroidX's <c>CreateDocument</c> contract. Raw platform code
/// because MAUI Essentials has no save picker and D13 forbids taking a UI-component package for one.
/// </summary>
public sealed class AndroidFileDialogs : MobileFileDialogsBase
{
    /// <inheritdoc />
    // No `= default`: repeating the base declaration's default on an override is CS1066.
    public override async Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
                                                          Func<Stream, CancellationToken, Task> write,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        // ASK FIRST on this platform: cancelling is the common case, so do not produce the content
        // first. (iOS cannot — its export picker needs the file to exist already.)
        var destination = await PickDestinationAsync(options, cancellationToken).ConfigureAwait(false);
        if (destination is null) return FileDialogResult.Cancelled();

        cancellationToken.ThrowIfCancellationRequested();

        // Produce into a cache temp, NOT straight into the user's document: opening a content URI in
        // "wt" truncates it immediately, so a caller that throws half-way would already have destroyed
        // an existing file they picked to overwrite.
        var temp = NewTempPath(SuggestedName(options));
        try
        {
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
            }

            await CopyToDocumentAsync(temp, destination, cancellationToken).ConfigureAwait(false);

            // No path reported: a content URI is a revocable grant, not something the app can reopen
            // later, and the contract populates FilePath only for an addressable destination.
            return FileDialogResult.Completed();
        }
        finally
        {
            // Best-effort: a failed delete must never mask the real outcome.
            DiscardTemp(temp);
        }
    }

    /// <summary>
    /// Show the create-document picker and return the chosen URI, or null when the user cancelled.
    /// <para>
    /// Launched through <see cref="ActivityResultRelay"/> rather than AndroidX's registry — see its
    /// remarks — which is why the adopter's MainActivity needs a one-line <c>OnActivityResult</c> forward
    /// (<c>docs/guides/mobile.md</c>). ⚠ Past PROCESS death only <paramref name="cancellationToken"/> can
    /// escape: the awaiting task dies with the process.
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
                // A generic MIME type: the kit's filters carry EXTENSIONS, and guessing a MIME would be
                // wrong for exactly the app-specific formats that matter. The extension still reaches
                // the picker through the suggested name. A backed-out picker arrives as null, not a throw.
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
            // Release on every exit path, or the relay entry leaks for the life of the session.
            if (requestCode >= 0) ActivityResultRelay.Complete(requestCode);
        }
    }

    /// <summary>
    /// Copy the finished temp into the document the user chose. ⚠ <c>"wt"</c> truncates first, so a
    /// smaller replacement does not leave the tail of the old content behind.
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
    /// Bridges the Java callback onto the awaiting task. <c>TrySet…</c>: a cancellation may already have
    /// completed it, and the platform is free to invoke a callback more than once.
    /// </summary>
    private sealed class UriCallback(TaskCompletionSource<AndroidUri?> completion)
        : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result) =>
            completion.TrySetResult(result as AndroidUri);
    }
}
