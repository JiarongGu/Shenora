using System.Text.Json;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

internal enum SampleStatus
{
    InProgress,
    Done,
}

public class IpcJsonTests
{
    private sealed record Sample(string FirstName, SampleStatus Status, string? Missing = null);

    [Fact]
    public void Serializes_camel_case_with_camel_case_enums_and_omitted_nulls()
    {
        var json = IpcJson.Serialize(new Sample("Ada", SampleStatus.InProgress));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Ada", root.GetProperty("firstName").GetString());
        Assert.Equal("inProgress", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("missing", out _));
    }

    [Fact]
    public void Deserializes_case_insensitive()
    {
        var sample = IpcJson.Deserialize<Sample>("""{"FIRSTNAME":"Ada","Status":"done"}""")!;

        Assert.Equal("Ada", sample.FirstName);
        Assert.Equal(SampleStatus.Done, sample.Status);
    }

    [Fact]
    public void SerializeToElement_uses_the_same_wire_shape()
    {
        var element = IpcJson.SerializeToElement(new Sample("Ada", SampleStatus.Done));

        Assert.Equal("Ada", element.GetProperty("firstName").GetString());
        Assert.Equal("done", element.GetProperty("status").GetString());
    }

    [Fact]
    public void Options_are_frozen()
    {
        // One shared instance must never drift — mutation attempts throw.
        Assert.True(IpcJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => IpcJson.Options.WriteIndented = true);
    }
}
