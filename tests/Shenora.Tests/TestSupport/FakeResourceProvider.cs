using System.Text;
using Microsoft.Extensions.Logging;
using Shenora.Core.WebView;

namespace Shenora.Tests.TestSupport;

/// <summary>A bundle in memory: the paths it holds, and how often it was warmed.</summary>
internal sealed class FakeResourceProvider(params (string Path, string Text)[] files) : IWebViewResourceProvider
{
    private readonly Dictionary<string, string> _files = files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase);

    public int Warmed { get; private set; }

    public Stream? GetResourceStream(string virtualPath) =>
        _files.TryGetValue(virtualPath.TrimStart('/'), out var text) ? new MemoryStream(Encoding.UTF8.GetBytes(text)) : null;

    public bool Exists(string virtualPath) => _files.ContainsKey(virtualPath.TrimStart('/'));

    public void BeginWarmup() => Warmed++;
}

/// <summary>A provider that fails every read, with a path in its message that must never reach a page.</summary>
internal sealed class ThrowingResourceProvider : IWebViewResourceProvider
{
    public Stream? GetResourceStream(string virtualPath) => throw new IOException(@"C:\secret\" + virtualPath);

    public bool Exists(string virtualPath) => throw new IOException("no");
}

/// <summary>Every line logged, prefixed with its level.</summary>
internal sealed class RecordingLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{level} {formatter(state, error)}");
}
