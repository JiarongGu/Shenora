using System.Drawing;
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

    /// <summary>
    /// Read a required colour, written as CSS hex: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>, alpha
    /// last as in CSS. Throws <see cref="ShenoraException"/> when the key is absent or the value is not one.
    /// </summary>
    public static Color GetRequiredColor(JsonElement? payload, string key) =>
        GetOptionalColor(payload, key) ?? throw new ShenoraException(IpcErrorCodes.MissingPayloadValue, "key", key,
            $"Missing required payload value '{key}'.");

    /// <summary>
    /// Read an optional colour, written as CSS hex (<see cref="GetRequiredColor"/>): null when the key is absent.
    /// ⚠ Unlike <see cref="GetOptionalValue{T}"/>, a value that is present and not a colour THROWS, since a mistyped
    /// colour should say so rather than paint the default.
    /// </summary>
    public static Color? GetOptionalColor(JsonElement? payload, string key)
    {
        if (!TryGetValue(payload, key, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String && TryParseHexColor(value.GetString()!, out var color)) return color;
        throw new ShenoraException(IpcErrorCodes.InvalidPayloadValue, new Dictionary<string, string> { ["key"] = key },
            $"Invalid payload value '{key}': a colour is CSS hex, #rgb, #rgba, #rrggbb or #rrggbbaa.");
    }

    private static bool TryParseHexColor(string text, out Color color)
    {
        color = default;
        if (text.Length is not (4 or 5 or 7 or 9) || text[0] != '#') return false;
        var digits = text[1..];
        foreach (var c in digits) if (!char.IsAsciiHexDigit(c)) return false;
        int Channel(int i) => digits.Length <= 4
            ? Convert.ToInt32(new string(digits[i], 2), 16)
            : Convert.ToInt32(digits.Substring(i * 2, 2), 16);
        var alpha = digits.Length is 4 or 8 ? Channel(3) : 255;
        color = Color.FromArgb(alpha, Channel(0), Channel(1), Channel(2));
        return true;
    }

    private static bool TryGetValue(JsonElement? payload, string key, out JsonElement value)
    {
        value = default;
        return payload is { ValueKind: JsonValueKind.Object } obj
            && obj.TryGetProperty(key, out value)
            && value.ValueKind is not JsonValueKind.Null;
    }
}
