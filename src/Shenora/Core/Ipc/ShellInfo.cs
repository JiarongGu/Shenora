using Shenora.Core.Shell;

namespace Shenora.Core.Ipc;

/// <summary>
/// What the host is and what it can do, returned to the client as the handshake's response data.
/// <para>
/// This is the other half of "universal": the C# contracts let one app's LOGIC run on two shells,
/// and this lets one PAGE run on both. A frontend renders
/// <c>capabilities.includes('windowChrome') &amp;&amp; &lt;TitleBar/&gt;</c> instead of sniffing the
/// platform — so the same bundle ships to a desktop shell that draws its own chrome and a mobile one
/// that has no window at all.
/// </para>
/// <para>
/// <b>Declared by the APP, not inferred by the kit</b>, because it is the app that decides. Whether
/// window commands exist depends on whether it mapped <c>WindowCommandModule</c>, not merely on which
/// shell it is running; a kit-guessed list would be wrong for exactly the compositions that differ.
/// The shell packages document their typical set, and <see cref="ShellCapability"/> holds the
/// well-known names.
/// </para>
/// <para>
/// It rides the handshake the client already sends, so it costs no extra round trip and is known
/// before the app renders its first frame — which is the point, since a capability learned after
/// layout is a flash.
/// </para>
/// <para>
/// <b>Advertise only what you actually composed.</b> A name here is a promise the page will act on;
/// claiming one the app never registered turns a rendered button into a
/// <see cref="ShellCapability.NotSupported"/> throw at the moment a user presses it (D33/D36).
/// </para>
/// </summary>
public sealed class ShellInfo
{
    /// <summary>A short host identifier for diagnostics and logs (e.g. <c>"winforms"</c>, <c>"maui"</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The capabilities this host offers — see <see cref="ShellCapability"/> for the well-known names.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}
