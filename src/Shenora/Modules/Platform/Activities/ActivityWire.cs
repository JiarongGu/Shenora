using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shenora.Modules.Platform.Activities;

/// <summary>
/// How a <see cref="Presentation"/>, a <see cref="LiveActivityState"/> and a
/// <see cref="LiveActivityAppearance"/> are written for the Swift side to read.
///
/// <para>
/// 🔴 <b>IT LIVES HERE, BESIDE THE TYPES, BECAUSE THE OPTIONS ARE PART OF THE WIRE.</b> The property
/// names the Swift mirror declares are produced by <c>PropertyNamingPolicy</c>, and the optionality every
/// mirrored field relies on is produced by <c>WhenWritingNull</c> — so a change here is a change to the
/// contract, not to a serializer preference. It used to live in <c>Shenora.iOS</c>'s
/// <c>IosLiveActivities</c> with a hand-copy in the tripwire test, which meant the golden payload could
/// have stayed green while the SHIPPED payload went PascalCase: the one shape the Swift decoder cannot
/// read. One definition makes that divergence unrepresentable instead of merely tested.
/// </para>
///
/// <para>
/// ⚠ <b>No enum converter here on purpose.</b> <see cref="Axis"/>, <see cref="Justify"/>,
/// <see cref="Align"/> and <see cref="TextRole"/> carry <c>[JsonConverter]</c> on the TYPE, so they are
/// written as member NAMES whatever options serialize them. That placement is deliberate: as an option it
/// would have to be repeated at every call site, and the one time it was missed (2026-08-09) the widget
/// read a number, failed the decode and fell back to its defaults with nothing logged on either side.
/// Registering one here would make this file the only thing keeping that true, which is the arrangement
/// that already failed.
/// </para>
///
/// <para>
/// ⚠ <b>Internal, not public.</b> Nothing outside the kit needs to serialize a presentation yet, and the
/// consumers are two assemblies in this repo — <c>Shenora.iOS</c> (via <c>InternalsVisibleTo</c>) and the
/// test suite. It becomes public the first time an adopter asks (D15), not before.
/// </para>
/// </summary>
internal static class ActivityWire
{
    /// <summary>
    /// camelCase and OMIT NULLS. Both matter: the Swift mirror declares camelCase properties, and every
    /// mirrored field is optional there precisely because nulls never arrive — a null written explicitly
    /// decodes to nil anyway but makes the payload bigger for no gain, and nulls are the common case
    /// (most states set one or two fields).
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
