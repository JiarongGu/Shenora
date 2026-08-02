using Foundation;
using Microsoft.Maui.ApplicationModel;
using Shenora.Core;
using UIKit;

namespace Shenora.Mobile;

/// <summary>
/// The iOS half of <see cref="MobileFileDialogs"/>: saving through
/// <see cref="UIDocumentPickerViewController"/> in its export-a-copy form.
/// <para>
/// The ORDER is forced here and it is the one real difference from Android. iOS has no
/// "create an empty document and give me a handle" picker — the export picker hands over a file that
/// already exists — so the content must be produced BEFORE the user chooses, and a cancel therefore
/// wastes the work. Android asks first precisely because it can. The shared declaration on
/// <c>MobileFileDialogs.SaveAsync</c> documents this for callers rather than leaving it to be
/// discovered.
/// </para>
/// </summary>
public sealed partial class MobileFileDialogs
{
    // No `= default` on the implementing half (CS1066) — the default lives on the shared declaration.
    /// <inheritdoc />
    public partial async Task<FileDialogResult> SaveAsync(FileDialogOptions? options,
                                                          Func<Stream, CancellationToken, Task> write,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        // The temp is not an optimisation here, it is the only way this works at all: the picker exports
        // an existing file. It doubles as the same safety Android gets from it — the user's document is
        // untouched until the content is complete, so a caller that throws half-way through a long
        // encode has destroyed nothing.
        var temp = NewTempPath(SuggestedName(options));
        try
        {
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // No path is reported on success — the contract populates FilePath only when the host has an
            // addressable destination, and what comes back here is a security-scoped URL that is not
            // valid for the app to reopen later.
            return await ExportAsync(temp, cancellationToken).ConfigureAwait(false)
                ? new FileDialogResult { Success = true }
                : FileDialogResult.Cancelled();
        }
        finally
        {
            // Only after the picker has finished: with asCopy the SYSTEM does the copying, so deleting
            // the source any earlier would race it.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* cache; the OS reclaims it */ }
        }
    }

    /// <summary>
    /// Present the export picker for <paramref name="temp"/>. True when the user chose a destination,
    /// false when they cancelled.
    /// </summary>
    private static async Task<bool> ExportAsync(string temp, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var presenter = Platform.GetCurrentUIViewController()
                ?? throw ShellCapability.NotSupported("Choosing a save destination", MauiShellNames.Shell,
                    "there is no view controller to present the document picker on — call this while a " +
                    "page is on screen, not during startup.");

            // asCopy: true — the system copies our temp to the destination and we keep ownership of the
            // temp (and delete it). The moving form would hand our cache file to the user's storage and
            // leave us deleting something that is no longer ours.
            var picker = new UIDocumentPickerViewController([NSUrl.FromFilename(temp)], asCopy: true)
            {
                Delegate = new ExportDelegate(completion),
            };
            presenter.PresentViewController(picker, animated: true, completionHandler: null);
        }).ConfigureAwait(false);

        await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken))
                         .ConfigureAwait(false))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes the awaiting task from the picker's callbacks. Both the singular and the plural
    /// did-pick overrides are implemented: the plural is the modern one, the singular still fires on
    /// some paths, and a picker that reports through the one we did NOT handle would leave the caller
    /// waiting forever with the file apparently saved. <c>TrySet…</c> throughout, because a
    /// cancellation may have completed the task already.
    /// </summary>
    private sealed class ExportDelegate(TaskCompletionSource<bool> completion) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl url) =>
            completion.TrySetResult(true);

        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) =>
            completion.TrySetResult(urls.Length > 0);

        public override void WasCancelled(UIDocumentPickerViewController controller) =>
            completion.TrySetResult(false);
    }
}
