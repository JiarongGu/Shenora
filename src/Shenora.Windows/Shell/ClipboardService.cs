using System.Drawing.Imaging;
using Shenora;
using Shenora.Core.Shell;

namespace Shenora.Windows;

// IClipboardService moved to Shenora in P5.5 H4.1 — a clipboard is portable in both concept and
// signature, so app logic using it needs no Windows reference (D20). The STA-thread implementation
// below is what stays Windows-side.

/// <summary>
/// The <see cref="IClipboardService"/> implementation: every operation runs on a dedicated STA
/// thread — the WinForms clipboard is STA-only, and the source app grew ad-hoc thread wrappers
/// around every call site; this centralizes that pattern.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    /// <inheritdoc />
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return StaThread.RunAsync(() =>
        {
            // Empty means CLEAR, not throw (P5.5 H2). Clipboard.SetText rejects an empty string with
            // ArgumentNullException — a surprise for "set the clipboard to what the user selected" when
            // the selection happens to be empty, which is app data, not a programming error. Clear()
            // is what the caller meant. A null argument is still a caller bug and still throws above.
            if (text.Length == 0) Clipboard.Clear();
            else Clipboard.SetText(text);
            return true;
        });
    }

    /// <inheritdoc />
    public Task<string?> GetTextAsync() =>
        StaThread.RunAsync(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    /// <inheritdoc />
    public Task SetImageFromFileAsync(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath)) throw new FileNotFoundException("Image file does not exist.", imagePath);
        return StaThread.RunAsync(() =>
        {
            // SetImage stores a copy — disposing our load immediately is safe (source shape).
            using var image = Image.FromFile(imagePath);
            Clipboard.SetImage(image);
            return true;
        });
    }

    /// <inheritdoc />
    public Task<bool> TrySaveImageToFileAsync(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        return StaThread.RunAsync(() =>
        {
            if (!Clipboard.ContainsImage()) return false;
            using var image = Clipboard.GetImage();
            if (image is null) return false;
            image.Save(targetPath, ImageFormat.Png);
            return true;
        });
    }
}
