using System.Text.Json;

namespace Shenora.Core.Ipc;

/// <summary>
/// Reads typed values out of an <see cref="IpcRequest.Payload"/>. Failures throw
/// <see cref="ShenoraException"/> (<see cref="IpcErrorCodes.MissingPayloadValue"/> /
/// <see cref="IpcErrorCodes.InvalidPayloadValue"/>), so payload misuse reaches the client structured and
/// i18n-ready. ⚠ JSON <c>null</c> counts as MISSING — this wire omits nulls
/// (<see cref="IpcJson.Options"/>), so an explicit null and an absent key mean the same thing.
/// </summary>
public static class PayloadHelper
{
    /// <summary>
    /// Read a required value. Throws <see cref="ShenoraException"/> when the key is absent
    /// (or JSON null) or the value cannot convert to <typeparamref name="T"/>.
    /// </summary>
    public static T GetRequiredValue<T>(JsonElement? payload, string key)
    {
        if (!TryGetValue(payload, key, out var value))
        {
            throw new ShenoraException(IpcErrorCodes.MissingPayloadValue, "key", key,
                $"Missing required payload value '{key}'.");
        }

        try
        {
            return value.Deserialize<T>(IpcJson.Options)!;
        }
        catch (Exception ex)
        {
            // 🔴 The serializer's message (CLR type names, JSON paths) stays host-side in the INNER
            // exception — the wire message must not carry raw exception text.
            throw new ShenoraException(IpcErrorCodes.InvalidPayloadValue,
                new Dictionary<string, string> { ["key"] = key },
                $"Invalid payload value '{key}'.", ex);
        }
    }

    /// <summary>
    /// Read an optional value: <c>default</c> when the key is absent, JSON null, or the value cannot
    /// convert.
    /// </summary>
    public static T? GetOptionalValue<T>(JsonElement? payload, string key)
    {
        if (!TryGetValue(payload, key, out var value))
        {
            return default;
        }

        try
        {
            return value.Deserialize<T>(IpcJson.Options);
        }
        catch
        {
            return default;
        }
    }

    private static bool TryGetValue(JsonElement? payload, string key, out JsonElement value)
    {
        value = default;
        return payload is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty(key, out value)
            && value.ValueKind is not JsonValueKind.Null;
    }
}
