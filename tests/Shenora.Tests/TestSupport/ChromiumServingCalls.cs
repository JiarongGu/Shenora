using Shenora.Chromium.Serving;
using Shenora.Core.WebView;

namespace Shenora.Tests.TestSupport;

internal static class ChromiumServingCalls
{
    /// <summary>One request through <paramref name="serving"/> on <paramref name="route"/>: status, body, content type.</summary>
    public static async Task<(int Status, string Body, string? Type)> ServeAsync(ChromiumServing serving, string url,
        ChromiumRoute route = ChromiumRoute.Bundle)
    {
        var request = new WebViewResourceRequest { Uri = new Uri(url), Method = "GET", Headers = new Dictionary<string, string>() };
        var response = await serving.ServeAsync(route, request, CancellationToken.None);
        using var reader = new StreamReader(response.Content);
        response.Headers.TryGetValue("Content-Type", out var type);
        return (response.StatusCode, await reader.ReadToEndAsync(), type);
    }
}

/// <summary>A bundle folder INSIDE its own temp folder, so a sibling file can prove containment.</summary>
internal sealed class TempBundle : IDisposable
{
    private readonly string _outer = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shenora-bundle-" + Guid.NewGuid().ToString("N"));

    public string Path { get; }

    public TempBundle(params (string Name, string Text)[] files)
    {
        Path = System.IO.Path.Combine(_outer, "bundle");
        Directory.CreateDirectory(Path);
        foreach (var (name, text) in files) File.WriteAllText(System.IO.Path.Combine(Path, name), text);
    }

    public void Dispose()
    {
        try { Directory.Delete(_outer, recursive: true); } catch (IOException) { }
    }
}
