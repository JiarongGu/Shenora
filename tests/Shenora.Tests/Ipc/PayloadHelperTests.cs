using System.Text.Json;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

public class PayloadHelperTests
{
    private static JsonElement? Payload(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private sealed record Item(string Name, int Count);

    [Fact]
    public void Required_reads_primitives_and_objects()
    {
        var payload = Payload("""{"name":"x","count":3,"item":{"name":"y","count":7}}""");

        Assert.Equal("x", PayloadHelper.GetRequiredValue<string>(payload, "name"));
        Assert.Equal(3, PayloadHelper.GetRequiredValue<int>(payload, "count"));
        Assert.Equal(new Item("y", 7), PayloadHelper.GetRequiredValue<Item>(payload, "item"));
    }

    [Fact]
    public void Required_missing_key_throws_structured()
    {
        var ex = Assert.Throws<OperationException>(
            () => PayloadHelper.GetRequiredValue<string>(Payload("""{"other":1}"""), "name"));

        Assert.Equal(IpcErrorCodes.MissingPayloadValue, ex.Code);
        Assert.Equal("name", ex.Parameters!["key"]);
    }

    [Fact]
    public void Required_treats_json_null_as_missing()
    {
        // The wire convention omits nulls (IpcJson), so an explicit null means the same as absent.
        var ex = Assert.Throws<OperationException>(
            () => PayloadHelper.GetRequiredValue<string>(Payload("""{"name":null}"""), "name"));

        Assert.Equal(IpcErrorCodes.MissingPayloadValue, ex.Code);
    }

    [Fact]
    public void Required_with_no_payload_at_all_throws_structured()
    {
        var ex = Assert.Throws<OperationException>(
            () => PayloadHelper.GetRequiredValue<string>(null, "name"));

        Assert.Equal(IpcErrorCodes.MissingPayloadValue, ex.Code);
    }

    [Fact]
    public void Required_unconvertible_value_throws_structured_with_cause()
    {
        var ex = Assert.Throws<OperationException>(
            () => PayloadHelper.GetRequiredValue<int>(Payload("""{"count":"not-a-number"}"""), "count"));

        Assert.Equal(IpcErrorCodes.InvalidPayloadValue, ex.Code);
        Assert.Equal("count", ex.Parameters!["key"]);
        // The serializer's details (CLR type names, JSON paths, the offending VALUE) stay host-side in
        // the inner exception; the message that crosses the bridge carries only the key (design §5).
        // Asserted as the INVARIANT rather than as the exact sentence (P5.5 H7): pinning the prose
        // made rewording the message a test failure, while saying nothing about the thing that
        // actually matters — that nothing leaks.
        Assert.Contains("count", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-number", ex.Message, StringComparison.Ordinal); // the raw value
        Assert.DoesNotContain("Int32", ex.Message, StringComparison.Ordinal);        // the CLR type
        Assert.DoesNotContain("$.", ex.Message, StringComparison.Ordinal);           // the JSON path
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Optional_returns_value_or_default()
    {
        var payload = Payload("""{"name":"x","bad":"nope","gone":null}""");

        Assert.Equal("x", PayloadHelper.GetOptionalValue<string>(payload, "name"));
        Assert.Null(PayloadHelper.GetOptionalValue<string>(payload, "missing"));
        Assert.Null(PayloadHelper.GetOptionalValue<string>(payload, "gone"));
        Assert.Equal(0, PayloadHelper.GetOptionalValue<int>(payload, "bad")); // lenient: default, not throw
        Assert.Null(PayloadHelper.GetOptionalValue<string>(null, "name"));
    }

    [Fact]
    public void Values_deserialize_with_wire_options()
    {
        // camelCase JSON → PascalCase C# members, proving reads go through IpcJson.Options.
        var payload = Payload("""{"item":{"name":"y","count":7}}""");

        Assert.Equal(new Item("y", 7), PayloadHelper.GetRequiredValue<Item>(payload, "item"));
    }
}
