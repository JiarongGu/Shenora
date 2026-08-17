using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shenora.Modules.Platform.Activities;

/// <summary>
/// How a <see cref="Presentation"/>, a <see cref="LiveActivityState"/> and a
/// <see cref="LiveActivityAppearance"/> are written for the Swift side to read.
/// <para>
/// 🔴 <b>The options ARE the wire.</b> The Swift mirror's property names come from
/// <c>PropertyNamingPolicy</c> and the optionality every mirrored field relies on from
/// <c>WhenWritingNull</c>, so a change here is a change to the contract.
/// </para>
/// <para>
/// ⚠ <b>No enum converter here.</b> <see cref="Axis"/>, <see cref="Justify"/>, <see cref="Align"/> and
/// <see cref="TextRole"/> carry <c>[JsonConverter]</c> on the TYPE, so they are written as member NAMES
/// whatever options serialize them. Written as numbers instead, the widget fails the decode and falls
/// back to its defaults with nothing logged on either side.
/// </para>
/// </summary>
internal static class ActivityWire
{
    /// <summary>
    /// camelCase and OMIT NULLS: the Swift mirror declares camelCase properties, and every mirrored
    /// field is optional there because nulls never arrive.
    /// </summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
