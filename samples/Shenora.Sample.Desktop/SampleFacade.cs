using Shenora.Ipc;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample backend module — the shape an app's facades take: one class per module, expected
/// failures as structured <see cref="OperationException"/>s, payload reads through
/// <see cref="PayloadHelper"/>.
/// </summary>
internal sealed class SampleFacade : BaseFacade
{
    public override string ModuleName => "SAMPLE";

    protected override Task<object?> RouteMessageAsync(IpcRequest request) => request.Type switch
    {
        // React → typed .NET handler → typed response (the e2e round-trip subject).
        "ECHO" => Task.FromResult<object?>(Echo(request)),
        // Structured-error demo: the client sees { code: "SAMPLE_FAILURE", parameters: { reason } }.
        "FAIL" => throw new OperationException("SAMPLE_FAILURE", "reason", "requested by the client"),
        _ => throw new OperationException(IpcErrorCodes.NoHandler,
            new Dictionary<string, string> { ["module"] = "SAMPLE", ["type"] = request.Type }),
    };

    private static object Echo(IpcRequest request)
    {
        var text = PayloadHelper.GetRequiredValue<string>(request.Payload, "text");
        return new { Echoed = text.ToUpperInvariant(), Length = text.Length };
    }
}
