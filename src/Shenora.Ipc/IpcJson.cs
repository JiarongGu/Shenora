using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shenora.Ipc;

/// <summary>
/// The wire serializer defaults every Shenora transport uses: camelCase properties, camelCase
/// string enums, case-insensitive reads, and nulls omitted (a null value and an absent key are
/// the same thing on this wire — the client-side convention is <c>undefined</c>). One frozen
/// instance, mutated never: the source app grew three private copies of these options that could
/// drift apart — the same disease as its four duplicated IsDevelopment checks.
/// <para>
/// An app may CONTRIBUTE type metadata to that one instance with
/// <see cref="AddTypeInfoResolver"/> — see its remarks for why that is not the same thing as
/// letting the options be mutated.
/// </para>
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
            // Double-checked with a volatile field: this is read on every Serialize call, so the
            // common path must not take the lock.
            if (_options is { } built) return built;
            lock (Gate) return _options ??= CreateOptions();
        }
    }

    /// <summary>
    /// Contribute an <see cref="IJsonTypeInfoResolver"/> — typically a source-generated
    /// <see cref="JsonSerializerContext"/> — to the ONE wire options instance. Call during startup,
    /// before anything serializes.
    /// <para>
    /// This exists because the options are frozen with a REFLECTION resolver, which is fine on
    /// desktop and Android and is exactly the metadata iOS strips (Mono AOT + trimming): the failure
    /// lands at RUNTIME, on a device, rather than at build time. The same seam is what makes full
    /// AOT / NativeAOT reachable on Android, which is the strongest cold-start lever an on-device
    /// host has — one change, two payoffs.
    /// </para>
    /// <para>
    /// Contributed resolvers are consulted BEFORE the reflection fallback, so a generated context
    /// wins for the types it knows. Note what this does NOT yet buy: the kit ships no generated
    /// context for its OWN envelope types, so <see cref="IpcRequest"/> and friends still resolve
    /// through reflection. An app can cover them by including them in its own context; a kit-side
    /// context would be a separate, additive change.
    /// </para>
    /// <para>
    /// It ADDS metadata rather than reopening the options, so the drifting-copies problem the single
    /// frozen instance was created to solve stays solved — there is still exactly one instance, and
    /// it is still read-only by the time anyone can serialize with it.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Options"/> has already been built, so the chain is frozen. Registering too late
    /// THROWS rather than being ignored: a silently-dropped resolver reappears as a stripped-metadata
    /// failure on a device, which looks nothing like its cause (the same reason
    /// <see cref="ModuleContext"/> fails loud instead of no-op-ing).
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

        // App resolvers FIRST, the reflection resolver LAST. Order is the whole point: a generated
        // context must win for the types it knows, and DefaultJsonTypeInfoResolver is the fallback
        // that a trimmed iOS build cannot rely on. With no app resolvers this is exactly what
        // MakeReadOnly(populateMissingResolver: true) used to install, so the default path is
        // unchanged.
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine([.. AppResolvers, new DefaultJsonTypeInfoResolver()]);
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Serialize with the wire defaults.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserialize with the wire defaults.</summary>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Convert a live object into the <see cref="JsonElement"/> form the envelopes carry —
    /// programmatic senders build payloads from objects; the wire delivers JSON.
    /// </summary>
    public static JsonElement SerializeToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>
    /// Drop the built options and any contributed resolvers, so a test can exercise the
    /// registration window. Process-global state has no other way to be tested in a shared test
    /// host; nothing in the shipped surface can reach this.
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
