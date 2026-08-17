using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shenora.Core.Ipc;

/// <summary>
/// The wire serializer defaults every Shenora transport uses: camelCase properties, camelCase
/// string enums, case-insensitive reads, and nulls omitted (a null value and an absent key are
/// the same thing on this wire — the client-side convention is <c>undefined</c>). ONE instance,
/// built once and frozen; an app may CONTRIBUTE type metadata to it with
/// <see cref="AddTypeInfoResolver"/> but never mutate it.
/// </summary>
public static class IpcJson
{
    private static readonly Lock Gate = new();
    private static readonly List<IJsonTypeInfoResolver> AppResolvers = [];
    private static volatile JsonSerializerOptions? _options;

    /// <summary>
    /// The frozen wire options (read-only; attempts to mutate throw). Built on FIRST ACCESS, which
    /// is also the point <see cref="AddTypeInfoResolver"/> stops being available.
    /// </summary>
    public static JsonSerializerOptions Options
    {
        get
        {
            // Double-checked against a volatile field — read on every Serialize call, so the common
            // path must not take the lock.
            if (_options is { } built) return built;
            lock (Gate) return _options ??= CreateOptions();
        }
    }

    /// <summary>
    /// Contribute an <see cref="IJsonTypeInfoResolver"/> — typically a source-generated
    /// <see cref="JsonSerializerContext"/> — to the ONE wire options instance. Call during startup,
    /// before anything serializes.
    /// <para>
    /// ⚠ Without one, every type resolves through the REFLECTION fallback — the metadata iOS strips (Mono
    /// AOT + trimming) — and a type it cannot resolve fails at RUNTIME, on a device, not at build time.
    /// </para>
    /// <para>
    /// Contributed resolvers are consulted BEFORE the reflection fallback, so a generated context
    /// wins for the types it knows. The kit ships no generated context for its OWN envelope types, so
    /// <see cref="IpcRequest"/> and friends still resolve through reflection; an app can cover them by
    /// including them in its own context.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Options"/> has already been built, so the chain is frozen. Registering too late
    /// THROWS rather than being ignored: a silently-dropped resolver reappears as a stripped-metadata
    /// failure on a device, which looks nothing like its cause.
    /// </exception>
    public static void AddTypeInfoResolver(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (Gate)
        {
            if (_options is not null)
            {
                throw new InvalidOperationException(
                    "IpcJson.Options has already been built, so its type-info resolver chain is frozen. " +
                    "Call IpcJson.AddTypeInfoResolver during startup — before the host is built and " +
                    "before any transport, facade or serializer touches IpcJson.Options.");
            }
            AppResolvers.Add(resolver);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        // App resolvers FIRST, the reflection resolver LAST: a generated context must win for the types
        // it knows, and DefaultJsonTypeInfoResolver is the fallback a trimmed iOS build cannot rely on.
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine([.. AppResolvers, new DefaultJsonTypeInfoResolver()]);
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Serialize with the wire defaults.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserialize with the wire defaults.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Convert a live object into the <see cref="JsonElement"/> form the envelopes carry.</summary>
    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>
    /// Drop the built options and any contributed resolvers, so a test can exercise the registration
    /// window. Nothing in the shipped surface can reach this.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            AppResolvers.Clear();
            _options = null;
        }
    }
}
