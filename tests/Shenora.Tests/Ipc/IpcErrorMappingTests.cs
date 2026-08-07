using Shenora.Tests.TestSupport;
using Shenora.Core.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The error boundary became PUBLIC in P6.4, because the fifth copy of it turned out to be an
/// adopter's: an app whose IPC surface reports failures as EVENTS has no response to attach an error
/// to, so it needs the mapping on its own. Retyping the policy is exactly the leak this type exists
/// to prevent. These tests pin what the mapping is allowed to say.
/// </summary>
public class IpcErrorMappingTests
{
    private const string Secret = "Host=db;Password=hunter2";

    [Fact]
    public void An_unexpected_exception_crosses_as_the_code_and_the_type_name_only()
    {
        var error = IpcErrorMapping.ToError(new InvalidOperationException(Secret));

        Assert.Equal(IpcErrorCodes.UnknownError, error.Code);
        Assert.Equal("InvalidOperationException", error.Parameters?["exceptionType"]);
        // The whole point, asserted against the SERIALIZED form — a leak could hide in any field.
        Assert.DoesNotContain("hunter2", IpcJson.Serialize(error), StringComparison.Ordinal);
    }

    [Fact]
    public void An_OperationException_keeps_its_own_words()
    {
        var error = IpcErrorMapping.ToError(
            new OperationException("IMPORT_FAILED", "file", "notes.json", "Could not read notes.json."));

        Assert.Equal("IMPORT_FAILED", error.Code);
        Assert.Equal("notes.json", error.Parameters?["file"]);
        Assert.Equal("Could not read notes.json.", error.Message);
    }

    [Fact]
    public void The_message_of_an_OperationException_crosses_verbatim_which_is_why_it_must_never_wrap_ex_Message()
    {
        // This is the sharp edge, pinned deliberately rather than left as prose. The one sanctioned
        // channel through the boundary is the app's OWN words — so an adapter that "helpfully" writes
        // `new OperationException(code, message: ex.Message)` turns that channel into a complete
        // bypass. Found by the P6.4 adapter probe, which reproduced it against a planted secret; the
        // rule lives in .claude/knowledge/ipc-contracts.md and the trap is called out in ADOPTION.md.
        var wrapped = IpcErrorMapping.ToError(
            new OperationException("MODULE_FAILED", message: new InvalidOperationException(Secret).Message));

        Assert.Contains("hunter2", IpcJson.Serialize(wrapped), StringComparison.Ordinal);
    }

    [Fact]
    public void Cancellation_is_a_normal_outcome_with_its_own_code()
    {
        var error = IpcErrorMapping.ToError(new OperationCanceledException());

        Assert.Equal(IpcErrorCodes.OperationCancelled, error.Code);
    }

    [Fact]
    public void The_response_overload_correlates_to_the_request()
    {
        var request = IpcRequests.Create("APP", "IMPORT");

        var response = IpcErrorMapping.ToErrorResponse(request, new InvalidOperationException(Secret));

        Assert.False(response.Success);
        Assert.Equal(request.Id, response.Id);
        Assert.Equal(IpcErrorCodes.UnknownError, response.Error?.Code);
    }
}
