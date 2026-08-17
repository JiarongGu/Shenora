using System.Text;
using System.Text.Json;
using Shenora;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;
using Shenora.Modules.Clipboard;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The kit's route module over <see cref="IClipboardService"/>. What matters is not that three routes
/// call three methods — it is that BYTES survive the wire intact, and that a shell's refusal arrives as
/// a named code carrying no exception text.
/// </summary>
public class ClipboardModuleTests
{
    private const string SecretInTheShellsMessage = "C:/Users/somebody/Secret Plans/roots.txt";

    /// <summary>Records what the facade asked for, and answers whatever the test told it to.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public ClipboardContent? Written { get; private set; }
        public bool Cleared { get; private set; }
        public ClipboardContent Offering { get; init; } = new();
        public bool RefuseFiles { get; init; }

        public Task SetTextAsync(string text) => SetAsync(new ClipboardContent { Text = text });
        public Task<string?> GetTextAsync() => Task.FromResult(Offering.Text);
        public Task<ClipboardContent> GetAsync() => Task.FromResult(Offering);
        public Task ClearAsync() { Cleared = true; return Task.CompletedTask; }

        public Task SetAsync(ClipboardContent content)
        {
            if (RefuseFiles && content.Files.Count > 0)
            {
                // The shell's real refusal names a PATH-shaped alternative, which is exactly the kind of
                // detail that must not reach the page.
                throw ShellCapability.NotSupported("Putting FILES on the clipboard", "test-shell",
                    $"Share them instead, e.g. {SecretInTheShellsMessage}.");
            }
            Written = content;
            return Task.CompletedTask;
        }
    }

    private static IpcRequest Request(string type, object? payload = null) => new()
    {
        Id = "r1",
        Module = ClipboardModule.Module,
        Type = type,
        Payload = payload is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
    };

    private static async Task<IpcResponse> DispatchAsync(RecordingClipboard clipboard, IpcRequest request)
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new ClipboardModule(clipboard));
        return await dispatcher.DispatchAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task Bytes_survive_the_wire_as_base64_in_BOTH_directions()
    {
        // 🔴 The one thing this module cannot get away with being approximately right about. The wire is
        // JSON, so every byte payload is base64 — a mangled round trip yields a picture that is still a
        // valid message and simply will not decode.
        var payload = new byte[] { 0x00, 0xFF, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A };
        var clipboard = new RecordingClipboard();

        var write = await DispatchAsync(clipboard, Request(ClipboardModule.WriteType, new
        {
            content = new
            {
                text = "beside the bytes",
                formats = new Dictionary<string, byte[]> { ["application/x-test"] = payload },
            },
        }));

        Assert.True(write.Success);
        Assert.Equal("beside the bytes", clipboard.Written!.Text);
        Assert.Equal(payload, clipboard.Written.Formats["application/x-test"].ToArray());

        // And back out again, through the response the page actually receives.
        var reading = new RecordingClipboard
        {
            Offering = new ClipboardContent
            {
                Text = "read me",
                Formats = new Dictionary<string, ReadOnlyMemory<byte>> { ["application/x-test"] = payload },
            },
        };
        var read = await DispatchAsync(reading, Request(ClipboardModule.ReadType));

        Assert.True(read.Success);
        var json = JsonSerializer.SerializeToElement(read.Data, IpcJson.Options);
        Assert.Equal("read me", json.GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String(payload),
            json.GetProperty("formats").GetProperty("application/x-test").GetString());
    }

    [Fact]
    public async Task A_refused_file_copy_is_a_NAMED_code_and_leaks_no_exception_text()
    {
        // Both halves matter. The code is what a page can branch on; the absence of the shell's wording
        // is the error boundary — that message names a filesystem path here, and design §5 says raw
        // exception text never crosses.
        var response = await DispatchAsync(
            new RecordingClipboard { RefuseFiles = true },
            Request(ClipboardModule.WriteType, new { content = new { files = new[] { @"C:\a.txt" } } }));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.CapabilityNotSupported, response.Error!.Code);
        Assert.Equal("clipboard", response.Error.Parameters!["capability"]);

        var wire = JsonSerializer.Serialize(response, IpcJson.Options);
        Assert.DoesNotContain(SecretInTheShellsMessage, wire, StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("test-shell", wire, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_without_content_is_a_named_payload_error()
    {
        var response = await DispatchAsync(new RecordingClipboard(), Request(ClipboardModule.WriteType));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.MissingPayloadValue, response.Error!.Code);
    }

    [Fact]
    public async Task Clear_reaches_the_shell()
    {
        var clipboard = new RecordingClipboard();

        var response = await DispatchAsync(clipboard, Request(ClipboardModule.ClearType));

        Assert.True(response.Success);
        Assert.True(clipboard.Cleared);
    }

    [Fact]
    public async Task An_unknown_route_is_refused_rather_than_silently_answered()
    {
        var response = await DispatchAsync(new RecordingClipboard(), Request("PASTE_MAYBE"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoRoute, response.Error!.Code);
    }

    [Fact]
    public async Task HTML_crosses_as_the_media_type_both_sides_name()
    {
        // The constants are mirrored by WireMirrorTests; this proves the KEY survives the round trip as
        // written, since a dictionary key that changes case or shape files the bytes where nobody looks.
        var clipboard = new RecordingClipboard();

        await DispatchAsync(clipboard, Request(ClipboardModule.WriteType, new
        {
            content = new
            {
                formats = new Dictionary<string, byte[]>
                {
                    [ClipboardContent.Html] = Encoding.UTF8.GetBytes("<b>hi</b>"),
                },
            },
        }));

        Assert.Equal("<b>hi</b>",
            Encoding.UTF8.GetString(clipboard.Written!.Formats[ClipboardContent.Html].Span));
    }
}
