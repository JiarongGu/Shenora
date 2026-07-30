using System.Text.Json;

namespace Shenora.Ipc;

/// <summary>
/// Reads typed values out of an <see cref="IpcRequest.Payload"/>. Ported from the primary
/// desktop sibling with three deliberate changes: it is static (the source's DI interface
/// wrapped a stateless helper — the type didn't earn its keep); failures throw
/// <see cref="OperationException"/> (<see cref="IpcErrorCodes.MissingPayloadValue"/> /
/// <see cref="IpcErrorCodes.InvalidPayloadValue"/>) instead of <see cref="ArgumentException"/>,
/// so payload misuse reaches the client structured and i18n-ready; and JSON <c>null</c> counts
/// as missing — the family wire convention omits nulls (<see cref="IpcJson.Options"/>), so an
/// explicit null and an absent key mean the same thing.
/// </summary>
public static class PayloadHelper
{
    /// <summary>
    /// Read a required value. Throws <see cref="OperationException"/> when the key is absent
    /// (or JSON null) or the value cannot convert to <typeparamref name="T"/>.
    /// </summary>
    public static T GetRequiredValue<T>(JsonElement? payload, string key)
    {
        if (!TryGetValue(payload, key, out var value))
        {
            throw new OperationException(IpcErrorCodes.MissingPayloadValue, "key", key,
                $"Missing required payload value '{key}'.");
        }

        try
        {
            return value.Deserialize<T>(IpcJson.Options)!;
        }
        catch (Exception ex)
        {
            // The serializer's message (CLR type names, JSON paths) stays host-side in the inner
            // exception — the wire message must not carry raw exception text (design §5).
            throw new OperationException(IpcErrorCodes.InvalidPayloadValue,
                new Dictionary<string, string> { ["key"] = key },
                $"Invalid payload value '{key}'.", ex);
        }
    }

    /// <summary>
    /// Read an optional value: <c>default</c> when the key is absent, JSON null, or the value
    /// cannot convert (the source's lenient optional semantics, kept as-is).
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
