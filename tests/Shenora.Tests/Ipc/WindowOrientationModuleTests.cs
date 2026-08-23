using System.Text.Json;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Modules.Platform;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The kit's route module over <see cref="IWindowOrientation"/>. Two routes, so what is worth asserting
/// is not that they call two methods — it is the boundary: a value the shell cannot use must be refused
/// HERE rather than reaching it, and a shell's refusal must arrive as a code carrying no exception text.
/// </summary>
public class WindowOrientationModuleTests
{
    private const string SecretInTheShellsMessage = "C:/Users/somebody/Secret Plans/orientation.txt";

    /// <summary>Records what the facade asked for, and refuses on demand.</summary>
    private sealed class RecordingOrientation : IWindowOrientation
    {
        public List<string> Calls { get; } = [];
        public bool Refuse { get; init; }

        public void Lock(WindowOrientation orientation)
        {
            Refused();
            Calls.Add($"lock:{orientation}");
        }

        public void Unlock()
        {
            Refused();
            Calls.Add("unlock");
        }

        private void Refused()
        {
            if (!Refuse) return;
            // A real shell's refusal names its own alternative, which is exactly the kind of detail that
            // must not reach the page.
            throw ShellCapability.NotSupported(ShellCapability.WindowOrientation, "test-shell",
                $"Do it another way, e.g. {SecretInTheShellsMessage}.");
        }
    }

    private static IpcRequest Request(string type, object? payload = null) => new()
    {
        Id = "r1",
        Module = WindowOrientationModule.Module,
        Type = type,
        Payload = payload is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
    };

    private static async Task<IpcResponse> DispatchAsync(RecordingOrientation orientation, IpcRequest request)
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new WindowOrientationModule(orientation));
        return await dispatcher.DispatchAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task Both_routes_reach_the_shell_with_the_orientation_the_page_asked_for()
    {
        var shell = new RecordingOrientation();

        Assert.True((await DispatchAsync(shell, Request(WindowOrientationModule.LockType,
            new { orientation = "landscape" }))).Success);
        Assert.True((await DispatchAsync(shell, Request(WindowOrientationModule.UnlockType))).Success);

        Assert.Equal(["lock:Landscape", "unlock"], shell.Calls);
    }

    [Fact]
    public async Task A_value_the_enum_does_not_have_is_refused_AT_THE_BOUNDARY()
    {
        // 🔴 The page hand-writes this string. Passing it through as text and letting the shell shrug is
        // the failure mode that costs a day: nothing rotates, nothing throws, and the page has no way to
        // learn that "portrait-primary" was never a thing here.
        var shell = new RecordingOrientation();

        var response = await DispatchAsync(shell, Request(WindowOrientationModule.LockType,
            new { orientation = "portrait-primary" }));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.InvalidPayloadValue, response.Error!.Code);
        Assert.Empty(shell.Calls);
    }

    [Fact]
    public async Task A_MISSING_orientation_is_refused_too_rather_than_defaulting_to_portrait()
    {
        // An enum's default is a real value, so a missing key would otherwise LOCK the window to portrait
        // — the one outcome nobody asked for.
        var shell = new RecordingOrientation();

        var response = await DispatchAsync(shell, Request(WindowOrientationModule.LockType, new { }));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.MissingPayloadValue, response.Error!.Code);
        Assert.Empty(shell.Calls);
    }

    [Fact]
    public async Task A_shell_that_cannot_rotate_answers_a_NAMED_code_and_leaks_no_exception_text()
    {
        // Both halves matter. The code is what a page branches on; the absence of the shell's wording is
        // the error boundary — raw exception text never crosses the wire.
        var response = await DispatchAsync(new RecordingOrientation { Refuse = true },
            Request(WindowOrientationModule.LockType, new { orientation = "portrait" }));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.CapabilityNotSupported, response.Error!.Code);
        Assert.Equal(ShellCapability.WindowOrientation, response.Error.Parameters!["capability"]);

        var serialized = JsonSerializer.Serialize(response, IpcJson.Options);
        Assert.DoesNotContain(SecretInTheShellsMessage, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("test-shell", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_refuses_the_same_way_rather_than_silently_succeeding()
    {
        // The asymmetry worth pinning: a page that locked successfully and then cannot unlock must hear
        // about it, or it leaves the window stuck at an orientation with nothing to blame.
        var response = await DispatchAsync(new RecordingOrientation { Refuse = true },
            Request(WindowOrientationModule.UnlockType));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.CapabilityNotSupported, response.Error!.Code);
    }
}
