using Shenora.Core.Shell;

namespace Shenora.Core.Ipc;

/// <summary>
/// What the host is and what it can do, returned to the client as the handshake's response data, so a
/// page renders <c>capabilities.includes('windowChrome') &amp;&amp; &lt;TitleBar/&gt;</c> instead of
/// sniffing the platform (D33/D36).
/// <para>
/// <b>Declared by the APP, not inferred by the kit:</b> whether window commands exist depends on
/// whether the app mapped <c>WindowCommandModule</c>, not merely on which shell it runs.
/// <see cref="ShellCapability"/> holds the well-known names.
/// </para>
/// <para>
/// ⚠ <b>Advertise only what you actually composed.</b> A name here is a promise the page will act on;
/// claiming one the app never registered turns a rendered button into a
/// <see cref="ShellCapability.NotSupported"/> throw at the moment a user presses it.
/// </para>
/// </summary>
public sealed class ShellInfo
{
    /// <summary>A short host identifier for diagnostics and logs (e.g. <c>"winforms"</c>, <c>"maui"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The capabilities this host offers — see <see cref="ShellCapability"/> for the well-known names.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
