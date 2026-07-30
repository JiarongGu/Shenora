using System.Drawing.Imaging;

namespace Shenora.WinForms;

/// <summary>System-clipboard access, safe to call from any thread.</summary>
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
            Clipboard.SetText(text);
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
