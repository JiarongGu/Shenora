using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shenora.Core.Ipc;

/// <summary>
/// The request envelope a client sends to the host: <c>{ id, module, type, scope?, payload?,
/// timestamp }</c>. Transport-neutral — the same envelope travels over WebView2 postMessage, HTTP, or a
/// mobile shell's native channel (D11/D16). Property names are pinned with
/// <see cref="JsonPropertyNameAttribute"/> so the wire contract holds under ANY serializer options, not
/// only <see cref="IpcJson.Options"/>.
/// </summary>
public sealed class IpcRequest
{
    /// <summary>
    /// Correlation id, echoed back as <see cref="IpcResponse.Id"/>. Client-generated (uuid);
    /// defaults to a fresh guid for programmatic senders.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Routing: the module the request targets (e.g. <c>"APP"</c>).</summary>
    [JsonPropertyName("module")]
    public required string Module { get; init; }

    /// <summary>
    /// Routing: the action within the module (e.g. <c>"GET_ALL"</c> — SCREAMING_SNAKE_CASE is
    /// the family convention, not a contract requirement).
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Optional app-defined routing scope. Apps that partition state into scoped containers route on
    /// it; apps without scoping leave it null.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>Request arguments as raw JSON; read typed values with <see cref="PayloadHelper"/>.</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    /// <summary>Client send time. Defaults to now when the sender omits it.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
