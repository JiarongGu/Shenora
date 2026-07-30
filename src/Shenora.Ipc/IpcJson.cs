using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shenora.Ipc;

/// <summary>
/// The wire serializer defaults every Shenora transport uses: camelCase properties, camelCase
/// string enums, case-insensitive reads, and nulls omitted (a null value and an absent key are
/// the same thing on this wire — the client-side convention is <c>undefined</c>). One frozen
/// instance, mutated never: the source app grew three private copies of these options that could
/// drift apart — the same disease as its four duplicated IsDevelopment checks.
/// </summary>
public static class IpcJson
{
    /// <summary>The frozen wire options (read-only; attempts to mutate throw).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Serialize with the wire defaults.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserialize with the wire defaults.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Convert a live object into the <see cref="JsonElement"/> form the envelopes carry —
    /// programmatic senders build payloads from objects; the wire delivers JSON.
    /// </summary>
    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
