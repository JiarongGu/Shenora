using System.Text.Json.Serialization;

namespace Shenora.Core.Ipc;

/// <summary>
/// The host→client push envelope: <c>{ category: "notification", id, payload: [...],
/// timestamp }</c> where <c>payload</c> is the batched <see cref="IpcNotification"/> array.
/// Hosts queue notifications and flush on an interval (~50 ms, the number measured in two family
/// apps) so event bursts don't flood the bridge; a single notification still ships as a batch of
/// one. <c>category</c> alone discriminates, which is what lets this exact envelope ride any
/// transport — WebView2 postMessage today, the server-backed profile's WebSocket, a mobile
/// shell's channel (docs/DECISIONS.md D16).
///
/// DEVIATION from the primary desktop sibling: its batch was wrapped in a synthetic module/type
/// pair only because its client also received unbatched single notifications; Shenora's contract
/// is always-batched, so that wrapper is gone (design contract §5).
/// </summary>
public sealed class IpcNotificationBatch
{
    /// <summary>Always <see cref="IpcCategories.Notification"/> — this type IS the notification envelope.</summary>
    [JsonPropertyName("category")]
    public string Category => IpcCategories.Notification;

    /// <summary>Batch id, for diagnostics only — notifications are not correlated.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The batched events, oldest first.</summary>
    [JsonPropertyName("payload")]
    public required IReadOnlyList<IpcNotification> Payload { get; init; }

    /// <summary>Flush time.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
