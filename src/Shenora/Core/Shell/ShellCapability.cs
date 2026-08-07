using Shenora.Core.WebView;
using Shenora.Core.Ipc;

namespace Shenora.Core.Shell;

/// <summary>
/// How a shell reports a contract it cannot honour. Owner decision, 2026-08-02: an unsupported
/// capability THROWS, naming the platform — it does not silently no-op.
/// <para>
/// The reasoning is the kit's own precedent rather than taste. A silent no-op is the
/// "mistyped resource prefix degrading to an all-404 provider" bug class this repo keeps paying for:
/// the app looks fine, does nothing, and nothing anywhere says why. <c>ModuleContext.Publish</c>
/// already fails loud and names the exact fix when its dependency was never supplied; this is the
/// same rule applied across shells.
/// </para>
/// <para>
/// <b>Use it only for capabilities that are genuinely ABSENT</b> — native drop zones, a tray icon,
/// secondary windows on a phone. A capability the platform satisfies ITS OWN way is not absent, and
/// throwing there would be wrong: a mobile picker is already modal, so
/// <see cref="IUiInteraction"/>'s block/unblock is honestly a no-op on that shell, documented as one.
/// Absent means "no expression of this exists here", not "we did it differently".
/// </para>
/// <para>
/// <b>Deliberately not a proxy.</b> The obvious implementation — one <c>DispatchProxy</c> that throws
/// for any interface — is reflection, which is exactly what iOS (Mono AOT + trimming) strips and what
/// <c>Shenora.Ipc</c>'s <c>IpcJson.AddTypeInfoResolver</c> seam exists to avoid depending on. A shell
/// writes a small explicit stub per contract it lacks and shares this message instead.
/// </para>
/// </summary>
public static class ShellCapability
{
    // ---- The well-known capability NAMES a host advertises to its client.
    //
    // They exist so a frontend can render one tree on every shell — `caps.has(WindowChrome) &&
    // <TitleBar/>` instead of sniffing the platform — which is the other half of "universal": not
    // just one interface, one page. Strings rather than an enum because an app declares its OWN
    // capabilities too, and a closed enum would make the kit the registrar of every consumer's
    // features.
    //
    // Only things a CLIENT branches on belong here. A capability the page cannot observe is the
    // app's business, not the wire's.

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
    /// The host can serve LOCAL FILES to the page through an <see cref="IWebViewInterceptor"/> — media,
    /// images, documents, generated exports.
    /// <para>
    /// A page needs this because it cannot reach a local file itself on any shell: <c>file://</c> is blocked
    /// from a virtual-host origin, and would be the wrong answer regardless. So a page that wants to render
    /// local content asks for this and falls back — to an external handler, or to hiding the control —
    /// rather than showing a player that can never load.
    /// </para>
    /// <para>
    /// ⚠ It says the host CAN serve, not WHAT it will serve: the routes, the payload shape and the allowed
    /// roots are all the app's. And note what a page must never be told here — the URL SCHEME and the range
    /// delivery. A relative url already resolves to the right scheme on each shell by itself, and
    /// <see cref="WebViewRangeDelivery"/> is a host-side fact; advertising either would put the page back to
    /// branching on platform, which is exactly what this handshake exists to stop (D36).
    /// </para>
    /// </summary>
    public const string LocalFiles = "localFiles";

    /// <summary>
    /// The exception an unsupported capability throws. <paramref name="capability"/> is what the
    /// caller asked for, <paramref name="shell"/> is the host that cannot do it, and
    /// <paramref name="alternative"/> — when there is one — is what to do instead, because an error
    /// that only says "no" leaves the caller exactly where it started.
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
