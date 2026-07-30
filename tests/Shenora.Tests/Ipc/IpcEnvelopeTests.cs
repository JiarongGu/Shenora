using System.Text.Json;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// Wire-shape tests for the envelope contract (design contract §5, D11). These assert the JSON
/// a client actually sees — property names, category values, null omission — because the TS
/// bridge in @shenora/react is written against exactly this shape.
/// </summary>
public class IpcEnvelopeTests
{
    [Fact]
    public void Request_deserializes_client_json()
    {
        var json = """
            {"id":"abc-123","module":"APP","type":"GET_ALL","scope":"tenant-1",
             "payload":{"name":"x"},"timestamp":"2026-07-30T10:00:00Z"}
            """;

        var request = IpcJson.Deserialize<IpcRequest>(json)!;

        Assert.Equal("abc-123", request.Id);
        Assert.Equal("APP", request.Module);
        Assert.Equal("GET_ALL", request.Type);
        Assert.Equal("tenant-1", request.Scope);
        Assert.Equal("x", request.Payload!.Value.GetProperty("name").GetString());
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero), request.Timestamp);
    }

    [Fact]
    public void Request_without_module_or_type_is_rejected()
    {
        Assert.Throws<JsonException>(() => IpcJson.Deserialize<IpcRequest>("""{"id":"1","type":"PING"}"""));
        Assert.Throws<JsonException>(() => IpcJson.Deserialize<IpcRequest>("""{"id":"1","module":"APP"}"""));
    }

    [Fact]
    public void Request_defaults_id_and_timestamp_when_omitted()
    {
        var request = IpcJson.Deserialize<IpcRequest>("""{"module":"APP","type":"PING"}""")!;

        Assert.False(string.IsNullOrWhiteSpace(request.Id));
        Assert.Null(request.Scope);
        Assert.Null(request.Payload);
        Assert.True(DateTimeOffset.UtcNow - request.Timestamp < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Success_response_wire_shape()
    {
        var json = IpcJson.Serialize(IpcResponse.CreateSuccess("abc-123", new { count = 2 }));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(IpcCategories.Ipc, root.GetProperty("category").GetString());
        Assert.Equal("abc-123", root.GetProperty("id").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(2, root.GetProperty("data").GetProperty("count").GetInt32());
        Assert.False(root.TryGetProperty("error", out _)); // null omitted (family wire convention)
    }

    [Fact]
    public void Success_response_with_no_data_omits_data()
    {
        var json = IpcJson.Serialize(IpcResponse.CreateSuccess("abc-123"));
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Error_response_wire_shape()
    {
        var response = IpcResponse.CreateError("abc-123", "IMPORT_FAILED",
            parameters: new Dictionary<string, string> { ["name"] = "MyThing" });

        var json = IpcJson.Serialize(response);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(IpcCategories.Ipc, root.GetProperty("category").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("IMPORT_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("MyThing", error.GetProperty("parameters").GetProperty("name").GetString());
        Assert.False(error.TryGetProperty("message", out _)); // no fallback message given → omitted
        Assert.False(root.TryGetProperty("data", out _));
    }

    [Fact]
    public void Notification_batch_wire_shape()
    {
        var batch = new IpcNotificationBatch
        {
            Payload =
            [
                new IpcNotification { Module = "APP", Type = "UPDATED", Payload = new { version = "2" } },
                new IpcNotification { Module = "APP", Type = "PING", Scope = "tenant-1" },
            ],
        };

        var json = IpcJson.Serialize(batch);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(IpcCategories.Notification, root.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("id").GetString()));
        Assert.True(root.TryGetProperty("timestamp", out _));

        var items = root.GetProperty("payload");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("UPDATED", items[0].GetProperty("type").GetString());
        Assert.Equal("2", items[0].GetProperty("payload").GetProperty("version").GetString());
        Assert.False(items[0].TryGetProperty("scope", out _)); // null omitted
        Assert.False(items[1].TryGetProperty("payload", out _)); // signal-only event
        Assert.Equal("tenant-1", items[1].GetProperty("scope").GetString());
    }

    [Fact]
    public void Envelope_names_are_pinned_independent_of_serializer_options()
    {
        // The envelopes must stay wire-correct even when an app serializes them with its own
        // options (no camelCase policy here) — that's what the JsonPropertyName pinning is for.
        var json = JsonSerializer.Serialize(IpcResponse.CreateSuccess("abc-123", null));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("category", out _));
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("success", out _));
    }
}
