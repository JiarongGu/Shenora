using System.Text.Json.Serialization;

namespace Shenora.Ipc;

/// <summary>
/// The response envelope the host returns for an <see cref="IpcRequest"/>:
/// <c>{ category: "ipc", id, success, data?, error? }</c>. The category field discriminates
/// responses from pushed <see cref="IpcNotificationBatch"/> messages on transports that
/// multiplex both over one channel. Ported from the primary desktop sibling with two deliberate
/// deviations (design contract §5): the category value is lowercase, and the source's flat error
/// string + duplicated error info in <c>data</c> collapse into one structured
/// <see cref="IpcError"/> — raw exception text never crosses the bridge. No timestamp: the
/// source dropped it at the wire wrapper too; the correlation id is the response's identity.
/// </summary>
public sealed class IpcResponse
{
    /// <summary>Always <see cref="IpcCategories.Ipc"/> — this type IS the ipc-category envelope.</summary>
    [JsonPropertyName("category")]
    public string Category => IpcCategories.Ipc;

    /// <summary>The <see cref="IpcRequest.Id"/> this responds to.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>True when the operation succeeded; <see cref="Error"/> is set when false.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>Result data on success; null when the operation returns nothing.</summary>
    [JsonPropertyName("data")]
    public object? Data { get; init; }

    /// <summary>Structured error on failure; null on success.</summary>
    [JsonPropertyName("error")]
    public IpcError? Error { get; init; }

    /// <summary>A success response carrying <paramref name="data"/>.</summary>
    public static IpcResponse CreateSuccess(string id, object? data = null) =>
        new() { Id = id, Success = true, Data = data };

    /// <summary>A failure response carrying a structured error.</summary>
    public static IpcResponse CreateError(string id, IpcError error) =>
        new() { Id = id, Success = false, Error = error };

    /// <summary>A failure response built from the error parts (see <see cref="IpcError"/>).</summary>
    public static IpcResponse CreateError(
        string id, string code, string? message = null, IReadOnlyDictionary<string, string>? parameters = null) =>
        CreateError(id, new IpcError { Code = code, Message = message, Parameters = parameters });
}
