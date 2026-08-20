namespace Shenora.Core.Ipc;

/// <summary>
/// Error codes reserved by the framework, in the family's SCREAMING_SNAKE_CASE i18n-key form
/// (clients translate <c>errors.{code}</c> — see <see cref="IpcError"/>). Apps define their own
/// codes freely; these are only the ones Shenora itself emits.
/// </summary>
public static class IpcErrorCodes
{
    /// <summary>
    /// An unhandled (non-<see cref="ShenoraException"/>) exception reached the dispatch
    /// boundary. Details stay in the host log; the client learns nothing but the code.
    /// </summary>
    public const string UnknownError = "UNKNOWN_ERROR";

    /// <summary>
    /// <b>No MODULE claimed the request</b> — nothing in the dispatch pipeline answers that name.
    /// Parameters: <c>module</c>, <c>type</c>.
    /// <para>
    /// ⚠ <b>Distinct from <see cref="NoRoute"/>:</b> this means the module was never registered,
    /// <see cref="NoRoute"/> means it WAS and has no such type — opposite fixes.
    /// </para>
    /// </summary>
    public const string NoHandler = "NO_HANDLER";

    /// <summary>
    /// <b>The module answered but has no route of that TYPE.</b> Parameters: <c>module</c>, <c>type</c>.
    /// <para>
    /// Raised by <c>ModuleBase.UnknownType</c>: the module IS mapped, so this is a route-name problem
    /// rather than a composition one.
    /// </para>
    /// </summary>
    public const string NoRoute = "NO_ROUTE";

    /// <summary>A scope-routed module was called without <see cref="IpcRequest.Scope"/>. Parameters: <c>module</c>.</summary>
    public const string ScopeRequired = "SCOPE_REQUIRED";

    /// <summary>
    /// The operation was cancelled — a NORMAL outcome, not a fault. Its own code, distinct from
    /// <see cref="UnknownError"/>, so a client can stay silent instead of showing an error for something
    /// the user or the host asked for.
    /// </summary>
    public const string OperationCancelled = "OPERATION_CANCELLED";

    /// <summary>A required payload value is absent or JSON null. Parameters: <c>key</c>.</summary>
    public const string MissingPayloadValue = "MISSING_PAYLOAD_VALUE";

    /// <summary>A payload value could not convert to the requested type. Parameters: <c>key</c>.</summary>
    public const string InvalidPayloadValue = "INVALID_PAYLOAD_VALUE";

    /// <summary>
    /// The shell has NO EXPRESSION of what was asked for — not a fault, and not something a retry fixes.
    /// Parameters: <c>capability</c> (a <see cref="Shenora.Core.Shell.ShellCapability"/> constant).
    /// <para>
    /// Its own code so a client can tell "this shell cannot do that" from "something broke" — hide the
    /// control, do not show an error. ⚠ A page should not NEED this: the ready handshake advertises
    /// <c>ShellInfo.Capabilities</c> so a bundle can decide before it asks (D36).
    /// </para>
    /// </summary>
    public const string CapabilityNotSupported = "CAPABILITY_NOT_SUPPORTED";
}
