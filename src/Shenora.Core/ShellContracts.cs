namespace Shenora.Core;

// Portable slices of the native-service contracts (D20). The rule for what lands here is NOT
// "the signature happens to be platform-neutral" — it is "app logic must be able to compile off
// Windows". Reveal-in-file-manager and launch-a-process are desktop-only CONCEPTS, so they stay on
// the Windows-side IShellLauncher, which derives from IUrlLauncher; opening a URL and blocking the
// UI are meaningful on any host, so they live here.

/// <summary>
/// Open a URL in the user's browser. The portable slice of shell launching — a mobile or web host
/// implements this even though it has no file manager and no process launcher. Depend on this from
/// app logic; depend on <c>Shenora.WinForms.IShellLauncher</c> only for the desktop-only operations.
/// </summary>
public interface IUrlLauncher
{
    /// <summary>Open an http/https URL in the system browser (anything else is rejected).</summary>
    void OpenUrl(string url);
}

/// <summary>
/// Block and unblock interaction with the app's main UI while something modal is in progress
/// (a native dialog, a long native operation). The portable slice of
/// <c>Shenora.WinForms.IFormInteraction</c>: nested, so overlapping blocks don't re-enable early.
/// </summary>
public interface IUiInteraction
{
    /// <summary>Disable interaction with the main UI (nested: pairs with <see cref="UnblockInteraction"/>).</summary>
    void BlockInteraction();

    /// <summary>Re-enable interaction once every block is released.</summary>
    void UnblockInteraction();
}

/// <summary>
/// Clipboard access. Fully portable in concept and in signature — every host has a clipboard.
/// The desktop implementation runs each operation on a dedicated STA thread.
/// </summary>
public interface IClipboardService
{
    /// <summary>Put text on the clipboard.</summary>
    Task SetTextAsync(string text);

    /// <summary>The clipboard's text, or null when it holds none.</summary>
    Task<string?> GetTextAsync();

    /// <summary>Put an image FILE's content on the clipboard (the family's copy-preview use).</summary>
    Task SetImageFromFileAsync(string imagePath);

    /// <summary>
    /// Save the clipboard's image to <paramref name="targetPath"/> as PNG (the family's
    /// paste-preview use). False when the clipboard holds no image.
    /// </summary>
    Task<bool> TrySaveImageToFileAsync(string targetPath);
}
