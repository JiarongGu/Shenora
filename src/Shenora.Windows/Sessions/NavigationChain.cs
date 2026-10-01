namespace Shenora.Windows;

/// <summary>
/// Whether an aborted navigation is the one <c>Navigate</c> started or one it replaced. Starting a navigation while another
/// loads aborts that one, and its completion can arrive after the new one began, so a session waiting on its own took it.
/// The navigation is the address asked for and every main-frame start since: a redirect hop, and the engine's own
/// spelling of the address (an IDN host in punycode). An abort of any other address ended a navigation it replaced. The
/// Chromium shell keeps the same rule.
/// </summary>
internal sealed class NavigationChain
{
    // Chromium follows at most 20 redirects. Past this the oldest starts go; the address asked for stays.
    private const int MaxTracked = 32;
    private readonly List<string> _addresses = [];

    /// <summary><c>Navigate</c> was called for <paramref name="url"/>.</summary>
    public void Requested(string url)
    {
        _addresses.Clear();
        _addresses.Add(url);
    }

    /// <summary>A main-frame navigation started, or was redirected, to <paramref name="address"/>.</summary>
    public void Started(string address)
    {
        if (_addresses.Count == 0) return;   // no Navigate yet: nothing to replace
        if (_addresses.Count == MaxTracked) _addresses.RemoveAt(1);
        _addresses.Add(address);
    }

    /// <summary>Whether a navigation to <paramref name="address"/> that ended so is one the current one replaced.</summary>
    public bool Replaced(string address, bool aborted) =>
        aborted && _addresses.Count > 0 && !_addresses.Exists(a => SameAddress(a, address));

    private static bool SameAddress(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var a) && Uri.TryCreate(right, UriKind.Absolute, out var b)
            ? a.AbsoluteUri == b.AbsoluteUri
            : string.Equals(left, right, StringComparison.Ordinal);
}
