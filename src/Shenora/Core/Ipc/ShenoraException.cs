namespace Shenora.Core.Ipc;

/// <summary>
/// The one exception type whose details cross the bridge: a structured error code plus optional
/// interpolation parameters, translated client-side as <c>errors.{code}</c> (see
/// <see cref="IpcError"/>). Throw it from handlers for every expected failure; anything else is
/// logged host-side and reaches the client only as <see cref="IpcErrorCodes.UnknownError"/> —
/// raw exceptions never cross the bridge (design contract §5). Deliberately unsealed so apps can
/// derive their domain error types and still be caught at the dispatch boundary.
///
/// Ported from the primary desktop sibling; its <c>GetStructuredMessage()</c> (the structured
/// error as a JSON string inside the response's string error field) is replaced by
/// <see cref="ToError"/> — the structured object now travels as the response's <c>error</c>
/// field directly.
/// </summary>
public class ShenoraException : Exception
{
    /// <summary>Error code / i18n key (e.g. <c>"IMPORT_FAILED"</c>).</summary>
    public string Code { get; }

    /// <summary>Optional values the client interpolates into the translated message.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; }

    /// <param name="code">Error code / i18n key.</param>
    /// <param name="parameters">Optional interpolation values.</param>
    /// <param name="message">
    /// Optional untranslated message; defaults to the code. 🔴 <b>IT CROSSES THE WIRE</b> — this
    /// exception's message travels verbatim to the client, which surfaces it as the JavaScript
    /// <c>Error.message</c>. Never put a filesystem path, a connection string or raw exception text
    /// here. (An exception the kit did NOT recognise is different: only its TYPE NAME crosses.)
    /// </param>
    /// <param name="innerException">Optional cause, preserved for host logs.</param>
    public ShenoraException(
        string code,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? code, innerException)
    {
        Code = code;
        Parameters = parameters;
    }

    /// <summary>Convenience for the common single-parameter case.</summary>
    public ShenoraException(string code, string paramKey, string paramValue, string? message = null)
        : this(code, new Dictionary<string, string> { [paramKey] = paramValue }, message)
    {
    }

    /// <summary>
    /// The wire form. <see cref="Exception.Message"/> is omitted when it is just the code echoed
    /// back (no explicit message was given) so the envelope stays lean.
    /// </summary>
    public IpcError ToError() => new()
    {
        Code = Code,
        Message = Message == Code ? null : Message,
        Parameters = Parameters
    };
}
