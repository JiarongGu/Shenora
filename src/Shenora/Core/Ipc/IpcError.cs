using System.Text.Json.Serialization;

namespace Shenora.Core.Ipc;

/// <summary>
/// The structured error carried by a failed <see cref="IpcResponse"/>: <c>{ code, message?,
/// parameters? }</c>. <see cref="Code"/> is an i18n key — the client translates <c>errors.{code}</c>
/// with <see cref="Parameters"/> as interpolation values, so user-facing text is produced client-side.
/// <see cref="Message"/> is only the untranslated fallback for logs and development.
/// </summary>
public sealed class IpcError
{
    /// <summary>Error code / i18n key (e.g. <c>"IMPORT_FAILED"</c>).</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Untranslated fallback message for logs/dev; not for end users.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>Values the client interpolates into the translated message.</summary>
    [JsonPropertyName("parameters")]
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}
