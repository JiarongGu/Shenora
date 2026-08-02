using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The app-suppliable type-info resolver seam. It exists for a failure that cannot be reproduced on
/// this machine — iOS (Mono AOT + trimming) stripping the metadata a REFLECTION resolver needs, at
/// runtime, on a device — so what is testable here is the two things that decide whether the fix
/// works there: the contributed resolver is really consulted, and it is consulted BEFORE the
/// reflection fallback.
/// <para>
/// <see cref="IpcJson"/> is process-global, and the dotnet suite shares one host, so every test here
/// restores the pristine state in <see cref="Dispose"/>.
/// </para>
/// </summary>
public sealed class IpcJsonResolverTests : IDisposable
{
    public void Dispose() => IpcJson.ResetForTests();

    private sealed record Probe(string FirstName);

    /// <summary>
    /// Answers for <see cref="Probe"/> ONLY and renames its properties, so "did the app's resolver
    /// win?" is visible in the output. Returning null for everything else is what a real generated
    /// context does for a type it was not told about — the fallback must still handle those.
    /// </summary>
    private sealed class ProbeRenamingResolver : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver _inner = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type != typeof(Probe)) return null;
            var info = _inner.GetTypeInfo(type, options);
            if (info is null) return null;
            foreach (var property in info.Properties) property.Name = "fromTheAppResolver";
            return info;
        }
    }

    [Fact]
    public void A_contributed_resolver_answers_before_the_reflection_fallback()
    {
        IpcJson.ResetForTests();
        IpcJson.AddTypeInfoResolver(new ProbeRenamingResolver());

        var json = IpcJson.Serialize(new Probe("Ada"));

        // Reflection alone would have produced "firstName" (camelCase policy). The app's resolver
        // is chained AHEAD of it, so its metadata wins — flip that order and this line is what fails.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Ada", doc.RootElement.GetProperty("fromTheAppResolver").GetString());
    }

    [Fact]
    public void Types_the_contributed_resolver_declines_still_fall_through_to_reflection()
    {
        IpcJson.ResetForTests();
        IpcJson.AddTypeInfoResolver(new ProbeRenamingResolver());

        // A type the resolver returns null for: the wire defaults must be exactly as before, or
        // contributing metadata for ONE type would silently break every other type on the wire.
        var json = IpcJson.Serialize(new IpcRequest { Id = "1", Module = "M", Type = "T" });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("M", doc.RootElement.GetProperty("module").GetString());
    }

    [Fact]
    public void Registering_after_the_options_were_built_throws_and_names_the_fix()
    {
        IpcJson.ResetForTests();
        _ = IpcJson.Options;   // whatever serializes first does exactly this

        var error = Assert.Throws<InvalidOperationException>(
            () => IpcJson.AddTypeInfoResolver(new ProbeRenamingResolver()));

        // The message has to carry the fix: the symptom of a silently-dropped resolver is a
        // stripped-metadata failure on a device, which looks nothing like "you registered too late".
        Assert.Contains("during startup", error.Message, StringComparison.Ordinal);
        Assert.Contains("AddTypeInfoResolver", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_options_are_still_one_frozen_instance()
    {
        IpcJson.ResetForTests();
        IpcJson.AddTypeInfoResolver(new ProbeRenamingResolver());

        // The seam ADDS metadata; it must not reopen the options. Same instance every read, still
        // read-only — the drifting-copies problem the single frozen instance solved stays solved.
        Assert.Same(IpcJson.Options, IpcJson.Options);
        Assert.True(IpcJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => IpcJson.Options.WriteIndented = true);
    }

    [Fact]
    public void A_null_resolver_is_a_caller_bug()
    {
        Assert.Throws<ArgumentNullException>(() => IpcJson.AddTypeInfoResolver(null!));
    }
}
