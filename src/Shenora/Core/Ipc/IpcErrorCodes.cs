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
    /// ⚠ <b>Distinct from <see cref="NoRoute"/>, and the split is the whole point.</b> This means the
    /// module was never registered; <see cref="NoRoute"/> means it WAS, and does not know that type.
    /// Those are opposite fixes — wire the module up, versus correct a route name — and until
    /// 2026-08-08 both answered <c>NO_HANDLER</c> with identical parameters, so an adopter debugging a
    /// dead page could not tell which they had.
    /// </para>
    /// </summary>
    public const string NoHandler = "NO_HANDLER";

    /// <summary>
    /// <b>The module answered but has no route of that TYPE.</b> Parameters: <c>module</c>, <c>type</c>.
    /// <para>
    /// Raised by <c>ModuleBase.UnknownType</c>. Reaching this is proof the module IS registered and
    /// mapped, which is exactly what <see cref="NoHandler"/> cannot tell you — so a page seeing this
    /// has a route-name problem, not a composition problem.
    /// </para>
    /// </summary>
    public const string NoRoute = "NO_ROUTE";

    /// <summary>A scope-routed module was called without <see cref="IpcRequest.Scope"/>. Parameters: <c>module</c>.</summary>
    public const string ScopeRequired = "SCOPE_REQUIRED";

    /// <summary>
    /// The operation was cancelled — a NORMAL outcome, not a fault. Distinguished from
    /// <see cref="UnknownError"/> (P5.5 H6) so a client can stay silent instead of showing an error for
    /// something the user or the host asked for; cancellation used to be indistinguishable from a real
    /// failure, and the reference composition had already hand-rolled the workaround.
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
    /// Its own code for the same reason <see cref="OperationCancelled"/> has one: a client must be able to
    /// tell "this shell cannot do that" from "something broke", because the correct UI is different — hide
    /// the control, do not show an error. ⚠ A page should not NEED this: the ready handshake advertises
    /// <c>ShellInfo.Capabilities</c> precisely so a bundle can decide before it asks (D36). This is the
    /// honest answer when it asks anyway, so a refusal never arrives as <see cref="UnknownError"/> plus an
    /// exception type name.
    /// </para>
    /// </summary>
    public const string CapabilityNotSupported = "CAPABILITY_NOT_SUPPORTED";
}
