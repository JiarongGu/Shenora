using Shenora.Core.Ipc;


namespace Shenora.Tests.TestSupport;

/// <summary>
/// The ONE builder for a test <see cref="IpcRequest"/> (P5.5 H7). Five per-class factories had grown
/// four different signatures over this same shape; the part worth having a single owner is the
/// <see cref="IpcRequest.Payload"/> convention — <c>null</c> means ABSENT on this wire, so a test must
/// leave the element null rather than serialize a JSON <c>null</c> (<c>IpcJson</c> omits nulls and
/// <c>PayloadHelper</c> treats an explicit null as missing). That ternary was hand-written in two
/// places and is the one detail a sixth copy would plausibly get wrong.
/// <para>
/// Classes still keep a thin local <c>Request(…)</c> that binds their module — that is local context,
/// not duplication. What went away is the repeated BODY.
/// </para>
/// </summary>
internal static class IpcRequests
{
    /// <summary>Build a request; <paramref name="payload"/> is serialized only when non-null.</summary>
    public static IpcRequest Create(
        string module,
        string type = "ANY",
        string? scope = null,
        object? payload = null) =>
        new()
        {
            Module = module,
            Type = type,
            Scope = scope,
            Payload = payload is null ? null : IpcJson.SerializeToElement(payload),
        };
}
