namespace Shenora.Ipc;

/// <summary>
/// Error codes reserved by the framework, in the family's SCREAMING_SNAKE_CASE i18n-key form
/// (clients translate <c>errors.{code}</c> — see <see cref="IpcError"/>). Apps define their own
/// codes freely; these are only the ones Shenora itself emits.
/// </summary>
public static class IpcErrorCodes
{
    /// <summary>
    /// An unhandled (non-<see cref="OperationException"/>) exception reached the dispatch
    /// boundary. Details stay in the host log; the client learns nothing but the code.
    /// </summary>
    public const string UnknownError = "UNKNOWN_ERROR";

    /// <summary>Nothing in the dispatch pipeline handled the request. Parameters: <c>module</c>, <c>type</c>.</summary>
    public const string NoHandler = "NO_HANDLER";

    /// <summary>A scope-routed module was called without <see cref="IpcRequest.Scope"/>. Parameters: <c>module</c>.</summary>
    public const string ScopeRequired = "SCOPE_REQUIRED";

    /// <summary>A required payload value is absent or JSON null. Parameters: <c>key</c>.</summary>
    public const string MissingPayloadValue = "MISSING_PAYLOAD_VALUE";

    /// <summary>A payload value could not convert to the requested type. Parameters: <c>key</c>.</summary>
    public const string InvalidPayloadValue = "INVALID_PAYLOAD_VALUE";
}
