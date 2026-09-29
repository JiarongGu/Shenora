using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Shenora.Core.Ipc;

/// <summary>
/// The request envelope and the handshake's answer, read and written by hand over <see cref="JsonDocument"/> and
/// <see cref="Utf8JsonWriter"/>. Through <see cref="IpcJson"/> the first request a transport reads builds the envelope's
/// metadata by reflection, and it was the ready handshake's largest cost: 58 ms against 13 ms by hand, first use in a
/// fresh process. A generated context lost that back to JIT-compiling itself.
/// <para>
/// ⚠ They answer exactly as <see cref="IpcJson"/> does, which a test pins input by input: names match ignoring case,
/// <c>module</c> and <c>type</c> are required, <c>id</c> and <c>timestamp</c> keep their defaults when absent, a null
/// string stays null, the last of a repeated name wins, unknown names are ignored, and everything else is an
/// exception, as the serializer's would be.
/// </para>
/// </summary>
internal static class IpcWire
{
    /// <summary>The request in <paramref name="json"/>, or null for a JSON <c>null</c>.</summary>
    /// <exception cref="JsonException">It is not a request: malformed, not an object, a required name missing, or a
    /// value of the wrong kind.</exception>
    public static IpcRequest? ReadRequest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null) return null;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException($"An IPC request is an object, not {root.ValueKind}.");

        string? id = null, module = null, type = null, scope = null;
        bool hasId = false, hasModule = false, hasType = false, hasTimestamp = false;
        JsonElement? payload = null;
        var timestamp = default(DateTimeOffset);
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            if (Is(property, "id")) { id = Text(value, "id"); hasId = true; }
            else if (Is(property, "module")) { module = Text(value, "module"); hasModule = true; }
            else if (Is(property, "type")) { type = Text(value, "type"); hasType = true; }
            else if (Is(property, "scope")) scope = Text(value, "scope");
            else if (Is(property, "payload")) payload = value.ValueKind == JsonValueKind.Null ? null : value.Clone();
            else if (Is(property, "timestamp"))
            {
                if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out timestamp))
                    throw new JsonException("An IPC request's timestamp is not an ISO 8601 date and time.");
                hasTimestamp = true;
            }
        }
        if (!hasModule || !hasType)
            throw new JsonException($"An IPC request is missing its {(hasModule ? "type" : "module")}.");
        return new IpcRequest
        {
            Id = hasId ? id! : Guid.NewGuid().ToString(),
            Module = module!,
            Type = type!,
            Scope = scope,
            Payload = payload,
            Timestamp = hasTimestamp ? timestamp : DateTimeOffset.UtcNow,
        };
    }

    /// <summary>The handshake's success response, as <see cref="IpcJson.Serialize{T}(T)"/> writes
    /// <see cref="IpcResponse.CreateSuccess"/> with <paramref name="shell"/> as its data.</summary>
    public static string WriteHandshakeResponse(string id, ShellInfo? shell)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // A null is left out, as IpcJson leaves it: a request may send "id": null.
            writer.WriteStartObject();
            writer.WriteString("category", IpcCategories.Ipc);
            if (id is not null) writer.WriteString("id", id);
            writer.WriteBoolean("success", true);
            if (shell is not null)
            {
                writer.WriteStartObject("data");
                if (shell.Name is not null) writer.WriteString("name", shell.Name);
                if (shell.Capabilities is { } capabilities)
                {
                    writer.WriteStartArray("capabilities");
                    foreach (var capability in capabilities) writer.WriteStringValue(capability);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool Is(JsonProperty property, string name) => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    // A string or null, as the serializer reads a string property; anything else is the wrong kind.
    private static string? Text(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => throw new JsonException($"An IPC request's {name} is a string, not {value.ValueKind}."),
    };
}
