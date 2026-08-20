using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Shenora.Core.Shell;

// Portable slices of the native-service contracts (D20). The test for what lands here is not "the
// signature happens to be platform-neutral" but "app logic must be able to compile off Windows".

/// <summary>
/// Open a URL in the user's browser. Depend on this from app logic; depend on
/// <c>Shenora.Windows.IShellLauncher</c> only for the desktop-only operations (reveal in file manager,
/// launch a process).
/// </summary>
public interface IUrlLauncher
{
    /// <summary>Open an http/https URL in the system browser (anything else is rejected).</summary>
    void OpenUrl(string url);
}

/// <summary>
/// Block and unblock interaction with the app's main UI while something modal is in progress
/// (a native dialog, a long native operation). The portable slice of
/// <c>Shenora.Windows.IFormInteraction</c>: nested, so overlapping blocks don't re-enable early.
/// </summary>
public interface IUiInteraction
{
    /// <summary>Disable interaction with the main UI (nested: pairs with <see cref="UnblockInteraction"/>).</summary>
    void BlockInteraction();

    /// <summary>Re-enable interaction once every block is released.</summary>
    void UnblockInteraction();
}

/// <summary>
/// Clipboard access. Fully portable — every host has a clipboard. The desktop implementation runs each
/// operation on a dedicated STA thread.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Put <paramref name="text"/> on the clipboard, replacing whatever was there. Shorthand for
    /// <see cref="SetAsync"/> with only <see cref="ClipboardContent.Text"/> set.
    /// </summary>
    Task SetTextAsync(string text);

    /// <summary>The clipboard's text, or null when it holds none. Shorthand for <see cref="GetAsync"/>.</summary>
    Task<string?> GetTextAsync();

    /// <summary>
    /// Everything the clipboard is currently offering, in one read. Formats this shell cannot express
    /// are simply absent — reading never throws for a format that is not there.
    /// </summary>
    Task<ClipboardContent> GetAsync();

    /// <summary>
    /// Put <paramref name="content"/> on the clipboard, replacing whatever was there — <b>every format
    /// in ONE operation</b>.
    /// <para>
    /// 🔴 <b>The ATOMICITY is why this exists.</b> A clipboard holds one item offering several
    /// representations, so each platform's <c>Set</c> REPLACES the lot: calling a text setter and then an
    /// image setter leaves the image and silently discards the text, with no error.
    /// </para>
    /// <para>
    /// ⚠ Throws <see cref="NotSupportedException"/> (via <see cref="ShellCapability.NotSupported"/>) when
    /// the content asks for something this shell has no expression for — FILES on a phone's clipboard, for
    /// instance. Nothing is written when it throws.
    /// </para>
    /// </summary>
    Task SetAsync(ClipboardContent content);

    /// <summary>Leave the clipboard holding nothing.</summary>
    Task ClearAsync();
}

/// <summary>
/// One clipboard item and every representation it offers — the shape a native Copy actually has, and the
/// shape the web's own <c>ClipboardItem</c> uses. <see cref="Text"/> and <see cref="Files"/> are named
/// because every platform has a first-class API for them; everything else lives in <see cref="Formats"/>
/// keyed by media type, so an app can carry its OWN representation.
/// </summary>
public sealed record ClipboardContent
{
    /// <summary>The plain-text representation, or null for none. Empty string means an EMPTY text item,
    /// which is not the same as no text item at all.</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Absolute paths, for the copy a file manager can paste. ⚠ A DESKTOP idea: a phone's clipboard has
    /// no expression for it, so a mobile shell throws rather than dropping them silently.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// Every other representation, keyed by media type — <see cref="PngImage"/>, <see cref="Html"/>, or
    /// an app's own <c>application/…</c> type. A shell translates the well-known ones into what other
    /// applications actually read and carries the rest verbatim.
    /// </summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Formats { get; init; } =
        ReadOnlyDictionary<string, ReadOnlyMemory<byte>>.Empty;

    /// <summary>PNG bytes — the interchange image format every platform and browser reads.</summary>
    public const string PngImage = "image/png";

    /// <summary>UTF-8 HTML, for a paste that keeps its formatting.</summary>
    public const string Html = "text/html";

    /// <summary>True when this carries no representation at all — the same thing <c>ClearAsync</c> leaves.</summary>
    /// <remarks>⚠ Not serialized: this record crosses the IPC wire, where a derived flag would be a
    /// second source of truth a client could contradict.</remarks>
    [JsonIgnore]
    public bool IsEmpty => Text is null && Files.Count == 0 && Formats.Count == 0;
}
