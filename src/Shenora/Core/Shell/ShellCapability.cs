using Shenora.Core.WebView;

namespace Shenora.Core.Shell;

/// <summary>
/// How a shell reports a contract it cannot honour: an unsupported capability THROWS, naming the
/// platform — it does not silently no-op (D33).
/// <para>
/// 🔴 <b>Use it only for capabilities that are genuinely ABSENT</b> — native drop zones, a tray icon,
/// secondary windows on a phone. A capability the platform satisfies ITS OWN way is not absent, and
/// throwing there breaks portable logic that is behaving correctly: a mobile picker is already modal, so
/// <see cref="IUiInteraction"/>'s block/unblock is honestly a no-op on that shell, documented as one.
/// Absent means "no expression of this exists here", not "we did it differently".
/// </para>
/// </summary>
public static class ShellCapability
{
    // ---- The well-known capability NAMES a host advertises to its client, so a frontend can render one
    // tree on every shell (`caps.has(WindowChrome) && <TitleBar/>`) instead of sniffing the platform.
    // Strings rather than an enum because an app declares its OWN capabilities too.
    //
    // Only things a CLIENT branches on belong here.

    /// <summary>A frameless window whose chrome the page draws — minimize, maximize, drag, close.</summary>
    public const string WindowChrome = "windowChrome";

    /// <summary>Native OS file drag-and-drop over page elements (`useDropZone`).</summary>
    public const string DropZones = "dropZones";

    /// <summary>Picking a single file to read.</summary>
    public const string FilePicker = "filePicker";

    /// <summary>Picking a FOLDER — a desktop capability; see D35 before assuming it is portable.</summary>
    public const string FolderPicker = "folderPicker";

    /// <summary>Choosing a save destination.</summary>
    public const string SavePicker = "savePicker";

    /// <summary>Additional windows the app can open.</summary>
    public const string SecondaryWindows = "secondaryWindows";

    /// <summary>A tray icon.</summary>
    public const string Tray = "tray";

    /// <summary>
    /// The shell has a SYSTEM BACK gesture the page can take responsibility for — Android's hardware or
    /// gesture back. Absent on iOS and on the desktop, which have no such gesture at all.
    /// <para>
    /// 🔴 <b>Branch on it rather than intercepting unconditionally.</b> A page that asks to handle back
    /// where there is none is not refused — there is nothing to refuse — it simply never hears a press,
    /// which looks identical to a page whose handler is broken. See
    /// <see cref="Shenora.Modules.Platform.BackNavigation"/>.
    /// </para>
    /// </summary>
    public const string BackNavigation = "backNavigation";

    /// <summary>
    /// The host can put a FILE LIST on the clipboard, for the user to paste into a file manager — the one
    /// clipboard capability that genuinely differs by shell, since a phone's pasteboard has no file list.
    /// Branch on it rather than calling and catching the refusal.
    /// <para>
    /// ⚠ It says nothing about the rest of the clipboard: text and bytes work on every shell.
    /// </para>
    /// </summary>
    public const string ClipboardFiles = "clipboardFiles";

    /// <summary>
    /// The host can serve LOCAL FILES to the page through an <see cref="IWebViewInterceptor"/> — media,
    /// images, documents, generated exports. A page cannot reach a local file itself on any shell, so it
    /// branches on this to fall back rather than showing a player that can never load.
    /// <para>
    /// ⚠ It says the host CAN serve, not WHAT it will serve: the routes, the payload shape and the allowed
    /// roots are all the app's. The page is never told the URL SCHEME or the range delivery — a relative
    /// url already resolves correctly on each shell, and advertising either would put the page back to
    /// branching on platform (D36).
    /// </para>
    /// </summary>
    public const string LocalFiles = "localFiles";

    /// <summary>
    /// The shell can HOLD the window at an orientation — <see cref="IWindowOrientation"/>.
    /// <para>
    /// 🔴 <b>Branch on it, because the page's own fallback is real but weaker.</b>
    /// <c>screen.orientation.lock()</c> works in a page only while the document is FULLSCREEN, and not at
    /// all in WKWebView — so a page that finds this absent should either take fullscreen first or leave
    /// rotation alone, rather than calling a route that will be refused.
    /// </para>
    /// </summary>
    public const string WindowOrientation = "windowOrientation";

    /// <summary>
    /// The exception an unsupported capability throws. <paramref name="capability"/> is what the caller
    /// asked for, <paramref name="shell"/> is the host that cannot do it, and
    /// <paramref name="alternative"/> — when there is one — is what to do instead.
    /// </summary>
    public static NotSupportedException NotSupported(string capability, string shell, string? alternative = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        var message = $"{capability} is not available on {shell}.";
        if (!string.IsNullOrWhiteSpace(alternative)) message += $" {alternative}";
        return new NotSupportedException(message);
    }
}
