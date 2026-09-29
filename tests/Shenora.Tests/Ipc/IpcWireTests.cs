using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// <see cref="IpcWire"/> reads the request envelope and writes the handshake's answer by hand, for the handshake's
/// first-use cost. So every input here goes through it AND through <see cref="IpcJson"/>, and the two must agree:
/// every property of <see cref="IpcRequest"/> the same (found by reflection, so a property added later is compared
/// too), or both refusing.
/// </summary>
public class IpcWireTests
{
    private static readonly string[] RequestJson =
    [
        """{"id":"c1","module":"M","type":"T","scope":"s","payload":{"a":[1,2,{"b":null}]},"timestamp":"2026-09-30T01:02:03.456Z"}""",
        """{"module":"M","type":"T"}""",
        """{"ID":"c1","Module":"M","TYPE":"T","Scope":"s","PAYLOAD":5,"TimeStamp":"2026-09-30T01:02:03+10:00"}""",
        """{"id":null,"module":null,"type":null,"scope":null,"payload":null}""",
        """{"id":"a","id":"b","module":"M","module":"N","type":"T"}""",
        """{"id":"c1","module":"M","type":"T","extra":{"deep":[1,{"x":"y"}]},"another":true}""",
        """{"id":"é😀<>&'\"","module":"M\\N","type":"T\n"}""",
        """{"id":"c1","module":"M","type":"T","payload":[]}""",
        """{"id":"c1","module":"M","type":"T","payload":"text"}""",
        """{"id":"c1","module":"M","type":"T","timestamp":"2026-09-30"}""",
        """{"id":"c1","module":"M","type":"T","timestamp":"2026-09-30T01:02:03.1234567Z"}""",
        """{"id":"c1","module":"M","type":"T","timestamp":"2026-09-30T01:02:03"}""",
        // Each of these is refused by both.
        """{"id":"c1","type":"T"}""",
        """{"id":"c1","module":"M"}""",
        """{"id":5,"module":"M","type":"T"}""",
        """{"id":"c1","module":true,"type":"T"}""",
        """{"id":"c1","module":"M","type":"T","scope":{}}""",
        """{"id":"c1","module":"M","type":"T","timestamp":"yesterday"}""",
        """{"id":"c1","module":"M","type":"T","timestamp":null}""",
        """{"id":"c1","module":"M","type":"T","timestamp":1727650000}""",
        """[{"module":"M","type":"T"}]""",
        "5",
        "\"text\"",
        "{",
        """{"module":"M","type":"T",}""",
        """{"module":"M","type":"T"} {}""",
        """{"module":"M","type":"T"/*note*/}""",
        "",
        // A JSON null is no request, for both.
        "null",
        // Nested past the default depth of 64, in the payload.
        $$"""{"module":"M","type":"T","payload":{{new string('[', 70)}}{{new string(']', 70)}}}""",
    ];

    public static TheoryData<string> Requests => new(RequestJson);

    [Theory]
    [MemberData(nameof(Requests))]
    public void A_request_reads_as_the_serializer_reads_it(string json)
    {
        var (expected, expectedError) = Try(() => IpcJson.Deserialize<IpcRequest>(json));
        var (actual, actualError) = Try(() => IpcWire.ReadRequest(json));
        Assert.True((expectedError is null) == (actualError is null),
            $"IpcJson {(expectedError is null ? "read it" : $"refused it ({expectedError.GetType().Name})")}, "
            + $"IpcWire {(actualError is null ? "read it" : $"refused it ({actualError.GetType().Name}: {actualError.Message})")}");
        if (expectedError is not null) return;
        if (expected is null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        foreach (var property in typeof(IpcRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            AssertSame(property, property.GetValue(expected), property.GetValue(actual));
    }

    [Fact]
    public void The_inputs_cover_reading_refusing_and_null() =>
        Assert.Equal([true, true, true], new[]
        {
            RequestJson.Any(r => Try(() => IpcJson.Deserialize<IpcRequest>(r)).Value is not null),
            RequestJson.Any(r => Try(() => IpcJson.Deserialize<IpcRequest>(r)).Error is not null),
            RequestJson.Any(r => r == "null"),
        });

    // A property IpcRequest gains later is read by IpcJson from the start, so a request naming every property the
    // type declares must read the same through both, or IpcWire has not learnt the new one.
    [Fact]
    public void Every_name_the_request_type_declares_is_read()
    {
        var members = typeof(IpcRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (Property: p, Name: p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name))
            .ToList();
        Assert.All(members, m => Assert.NotNull(m.Name));
        var json = "{" + string.Join(",", members.Select(m => $"\"{m.Name}\":{Sample(m.Property.PropertyType, m.Name!)}")) + "}";
        var expected = IpcJson.Deserialize<IpcRequest>(json)!;
        var actual = IpcWire.ReadRequest(json)!;
        foreach (var (property, _) in members) AssertSame(property, property.GetValue(expected), property.GetValue(actual));
    }

    public static TheoryData<string?, ShellInfo?> Handshakes => new()
    {
        { "c1", null },
        { "c1", new ShellInfo { Name = "chromium", Capabilities = ["windowChrome", "tray"] } },
        { "c1", new ShellInfo { Name = "ios", Capabilities = [] } },
        { null, new ShellInfo { Name = "winforms" } },
        { "<script>&'\"é中😀", new ShellInfo { Name = "a\"b\\c\n<é>", Capabilities = ["x&y", null!] } },
        { "c1", new ShellInfo { Name = null!, Capabilities = null! } },
    };

    [Theory]
    [MemberData(nameof(Handshakes))]
    public void The_handshake_answer_is_written_as_the_serializer_writes_it(string? id, ShellInfo? shell) =>
        Assert.Equal(IpcJson.Serialize(IpcResponse.CreateSuccess(id!, shell)), IpcWire.WriteHandshakeResponse(id!, shell));

    private static void AssertSame(PropertyInfo property, object? expected, object? actual)
    {
        switch (expected, actual)
        {
            case (JsonElement e, JsonElement a):
                Assert.Equal(e.GetRawText(), a.GetRawText());
                break;
            // Absent, each made its own default as it read: a fresh id, the time of reading.
            case (string e, string a) when property.Name == nameof(IpcRequest.Id) && e != a:
                Assert.True(Guid.TryParse(e, out _) && Guid.TryParse(a, out _), $"Id: {e} != {a}");
                break;
            case (DateTimeOffset e, DateTimeOffset a) when e != a:
                Assert.True((DateTimeOffset.UtcNow - e).Duration() < TimeSpan.FromMinutes(1) && (DateTimeOffset.UtcNow - a).Duration() < TimeSpan.FromMinutes(1),
                    $"{property.Name}: {e:O} != {a:O}");
                break;
            default:
                Assert.Equal(expected, actual);
                break;
        }
    }

    private static string Sample(Type type, string name) =>
        (Nullable.GetUnderlyingType(type) ?? type) == typeof(JsonElement) ? """{"sample":true}"""
        : (Nullable.GetUnderlyingType(type) ?? type) == typeof(DateTimeOffset) ? "\"2026-09-30T01:02:03Z\""
        : $"\"sample-{name}\"";

    private static (T? Value, Exception? Error) Try<T>(Func<T?> read)
    {
        try { return (read(), null); }
        catch (Exception ex) { return (default, ex); }
    }
}
