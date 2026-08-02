using System.Text.Json;

namespace Shenora.Windows;

/// <summary>
/// The simplest <see cref="IWindowStateStore"/>: one JSON file (e.g.
/// <c>%LocalAppData%\MyApp\window.json</c>). Both directions are best-effort — a corrupt or
/// unwritable file must never break startup or close.
/// </summary>
public sealed class JsonFileWindowStateStore(string filePath) : IWindowStateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The persisted state, or null when absent OR unreadable — a corrupt file must not stop startup.</summary>
    public WindowState? Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<WindowState>(File.ReadAllText(filePath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Persist the state. Best-effort: a failure here must never take the app down on exit.</summary>
    public void Save(WindowState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, JsonSerializer.Serialize(state, Json));
        }
        catch
        {
            // window state is a nicety — never block close on it
        }
    }
}
