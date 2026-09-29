using System.Text.Json;

namespace Shenora.Chromium;

/// <summary>
/// The page half of the Chromium shell's IPC transport, which runs NO code in the renderer (D83). The
/// shell marks every HTML document it serves with a global naming a same-origin route; the page's
/// <c>createChromiumTransport</c> posts each envelope there with <c>fetch</c>, and the shell pushes by
/// calling the global's <c>receive</c>. The names are mirrored by the client's <c>transport.ts</c>, and
/// <c>WireMirrorTests</c> keeps the two sides equal. Internal: the hosts mark every HTML document they serve, the
/// app's own routes' included, so an app has nothing to call here.
/// </summary>
internal static class ChromiumTransport
{
    /// <summary>The global a marked document carries: its presence is the shell advertising itself (D36).</summary>
    public const string HostGlobal = "__shenora_chromium";

    /// <summary>The marker's member naming the route the page posts to.</summary>
    public const string IpcMember = "ipc";

    /// <summary>The marker's member the shell calls with each message; the client's transport sets it.</summary>
    public const string ReceiveMember = "receive";

    /// <summary>The same-origin route the shell answers, unless a host chooses another.</summary>
    public const string DefaultIpcPath = "/__shenora/ipc";

    /// <summary>
    /// <paramref name="html"/> with the marker as the FIRST thing in its <c>&lt;head&gt;</c>, so it exists
    /// before any page script runs. A document with no <c>&lt;head&gt;</c> gets it at the very start.
    /// </summary>
    /// <param name="html">The document the shell is about to serve.</param>
    /// <param name="ipcPath">The route the page should post to.</param>
    public static string MarkHtml(string html, string ipcPath = DefaultIpcPath)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrEmpty(ipcPath);
        var marker = MarkerScript(ipcPath);

        var head = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        // `<header>` is not `<head>`: the character after the name must end the tag or start an attribute.
        while (head >= 0 && head + 5 < html.Length && !(html[head + 5] is '>' or ' ' or '\t' or '\r' or '\n' or '/'))
            head = html.IndexOf("<head", head + 5, StringComparison.OrdinalIgnoreCase);
        if (head < 0) return marker + html;
        var close = html.IndexOf('>', head);
        return close < 0 ? marker + html : html.Insert(close + 1, marker);
    }

    /// <summary>
    /// The script that marks a document. Its values are JSON-serialized, never interpolated: the default
    /// encoder escapes <c>&lt;</c> and <c>&gt;</c>, so nothing in it can close the script element.
    /// </summary>
    /// <param name="ipcPath">The route the page should post to.</param>
    public static string MarkerScript(string ipcPath = DefaultIpcPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ipcPath);
        var marker = JsonSerializer.Serialize(new Dictionary<string, string> { [IpcMember] = ipcPath });
        return $"<script>window.{HostGlobal}={marker};</script>";
    }

    /// <summary>
    /// The script the shell runs in the page's main frame to deliver <paramref name="message"/>. The
    /// message travels as a JSON string LITERAL (escaped by the serializer, including U+2028 and U+2029),
    /// so no payload can break out of the call. A page that never created the transport ignores it.
    /// </summary>
    /// <param name="message">One serialized envelope.</param>
    public static string PushScript(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var host = $"window.{HostGlobal}";
        return $"{host}&&{host}.{ReceiveMember}&&{host}.{ReceiveMember}({JsonSerializer.Serialize(message)});";
    }
}
