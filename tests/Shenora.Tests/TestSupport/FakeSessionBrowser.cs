using Shenora.Core.Sessions;

namespace Shenora.Tests.TestSupport;

/// <summary>
/// A session browser with no engine behind it (D91): the sessions' own accounting and rules, proven without a browser
/// process. It records what was asked of it; a navigation completes at once, successfully, unless told otherwise.
/// </summary>
internal sealed class FakeSessionBrowser : ISessionBrowser
{
    public List<string> Navigations { get; } = [];
    public bool IsClosed { get; private set; }

    /// <summary>False: a navigation never completes, as a renderer that stopped answering.</summary>
    public bool CompletesNavigations { get; set; } = true;

    public string Source { get; set; } = string.Empty;

    public event Action<SessionNavigationResult>? NavigationCompleted;

    public Func<string, bool>? CancelNavigation { get; set; }

    public bool CancelDownloads { get; set; }

    public void Navigate(string url)
    {
        Navigations.Add(url);
        if (CancelNavigation?.Invoke(url) == true) return;
        Source = url;
        if (CompletesNavigations) NavigationCompleted?.Invoke(new SessionNavigationResult(url, true, "Unknown"));
    }

    public Task<string?> ExecuteScriptAsync(string javaScript) => Task.FromResult<string?>("null");

    /// <summary>What a DevTools call answers; null answers <c>{}</c> at once.</summary>
    public Func<string, Task<string>>? DevTools { get; set; }

    public Task<string> CallDevToolsAsync(string method, string parametersJson) => DevTools?.Invoke(method) ?? Task.FromResult("{}");

    public IDisposable OnDevToolsEvent(string eventName, Action<string> onEvent) => new Nothing();

    public Task<IReadOnlyList<SessionCookie>> GetCookiesAsync(string origin) =>
        Task.FromResult<IReadOnlyList<SessionCookie>>([]);

    public void Focus() { }

    public void Close() => IsClosed = true;

    private sealed class Nothing : IDisposable
    {
        public void Dispose() { }
    }
}
