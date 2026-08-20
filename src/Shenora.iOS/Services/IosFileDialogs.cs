using Foundation;
using Microsoft.Maui.ApplicationModel;
using Shenora;
using UIKit;
using Shenora.Modules.FileDialog;
using Shenora.Core.Shell;

using Shenora.Mobile;

namespace Shenora.iOS;

/// <summary>
/// The iOS half of <see cref="MobileFileDialogsBase"/>: saving through
/// <see cref="UIDocumentPickerViewController"/> in its export-a-copy form. ⚠ iOS has no "create an empty
/// document and give me a handle" picker — the export picker hands over a file that ALREADY EXISTS — so the
/// content is produced BEFORE the user chooses and a cancel wastes the work
/// (<see cref="MobileFileDialogsBase.SaveAsync"/>).
/// </summary>
public sealed class IosFileDialogs : MobileFileDialogsBase
{
    // No `= default` on the implementing half (CS1066) — the default lives on the shared declaration.
    /// <inheritdoc />
    public override async Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
                                                          Func<Stream, CancellationToken, Task> write,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        // The temp is not an optimisation, it is the only way this works: the picker exports an EXISTING
        // file. It doubles as safety — the user's document is untouched until the content is complete.
        var temp = NewTempPath(SuggestedName(options));
        try
        {
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // No path on success: the contract populates FilePath only for an addressable destination, and
            // what comes back here is a security-scoped URL the app cannot reopen later.
            return await ExportAsync(temp, cancellationToken).ConfigureAwait(false)
                ? FileDialogResult.Completed()
                : FileDialogResult.Cancelled();
        }
        finally
        {
            // Only after the picker has finished: with asCopy the SYSTEM does the copying, so deleting
            // the source any earlier would race it.
            DiscardTemp(temp);
        }
    }

    /// <summary>Present the export picker for <paramref name="temp"/>. True when the user chose a
    /// destination, false when they cancelled.</summary>
    private static async Task<bool> ExportAsync(string temp, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var presenter = Platform.GetCurrentUIViewController()
                ?? throw ShellCapability.NotSupported("Choosing a save destination", MauiShellNames.Shell,
                    "there is no view controller to present the document picker on — call this while a " +
                    "page is on screen, not during startup.");

            // asCopy: true — the system copies our temp to the destination and we keep ownership of it. The
            // moving form would hand our cache file to the user's storage and leave us deleting it.
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
    /// Completes the awaiting task from the picker's callbacks. ⚠ BOTH did-pick overrides are implemented —
    /// the plural is the modern one, the singular still fires on some paths, and one reported through the
    /// override we did NOT handle leaves the caller waiting for ever with the file apparently saved.
    /// <c>TrySet…</c> throughout: a cancellation may have completed the task already.
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
