using System.Text.Json;
using Shenora;
using Shenora.Ipc;

namespace Shenora.Tests.Ipc;

/// <summary>
/// The kit's route module over <see cref="IFileDialogs"/>. What matters here is not that the four routes
/// call four methods — it is that the OPTIONS survive the wire as the right per-method type, and that a
/// shell's capability refusal arrives as a NAMED code rather than as an unknown fault.
/// </summary>
public class FileDialogFacadeTests
{
    /// <summary>Records what the facade asked for, and answers whatever the test told it to.</summary>
    private sealed class RecordingDialogs : IFileDialogs
    {
        public OpenFileOptions? OpenFileSeen { get; private set; }
        public OpenFolderOptions? OpenFolderSeen { get; private set; }
        public SaveFileOptions? SaveSeen { get; private set; }
        public string? Written { get; private set; }
        public bool RefuseFolder { get; init; }
        public bool RefuseSave { get; init; }

        public Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null)
        {
            OpenFileSeen = options;
            return Task.FromResult(FileDialogResult.Selected(@"C:\picked\file.txt"));
        }

        public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null)
        {
            OpenFolderSeen = options;
            return RefuseFolder
                ? throw ShellCapability.NotSupported(ShellCapability.FolderPicker, "test-shell")
                : Task.FromResult(FileDialogResult.Selected(@"C:\picked"));
        }

        public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null)
        {
            SaveSeen = options;
            return RefuseSave
                ? throw ShellCapability.NotSupported(ShellCapability.SavePicker, "test-shell")
                : Task.FromResult(FileDialogResult.Selected(@"C:\saved\out.txt"));
        }

        public async Task<FileDialogResult> SaveAsync(SaveFileOptions? options,
            Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
        {
            SaveSeen = options;
            if (RefuseSave) throw ShellCapability.NotSupported(ShellCapability.SavePicker, "test-shell");
            var buffer = new MemoryStream();
            await write(buffer, cancellationToken);
            Written = Files.DefaultEncoding.GetString(buffer.ToArray());
            // The grant-only outcome, so the test exercises the shape a mobile shell really returns.
            return FileDialogResult.Completed();
        }
    }

    private static IpcRequest Request(string type, object? payload = null) => new()
    {
        Id = "r1",
        Module = FileDialogFacade.Module,
        Type = type,
        Payload = payload is null
            ? null
            : JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, IpcJson.Options)),
    };

    private static async Task<IpcResponse> DispatchAsync(RecordingDialogs dialogs, IpcRequest request)
    {
        var dispatcher = new MessageDispatcher();
        dispatcher.MapModule(new FileDialogFacade(dialogs));
        return await dispatcher.DispatchAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task Open_file_carries_the_page_s_options_through_as_the_per_method_type()
    {
        // The point of the split, proven across the wire: a page sends one `options` object and the host
        // materialises the type that method actually accepts.
        var dialogs = new RecordingDialogs();

        var response = await DispatchAsync(dialogs, Request(FileDialogFacade.OpenFileType,
            new { options = new { title = "Pick one", fileName = "seed.txt", rememberPathKey = "k" } }));

        Assert.True(response.Success);
        Assert.NotNull(dialogs.OpenFileSeen);
        Assert.Equal("Pick one", dialogs.OpenFileSeen!.Title);
        Assert.Equal("seed.txt", dialogs.OpenFileSeen.FileName);
        Assert.Equal("k", dialogs.OpenFileSeen.RememberPathKey);
    }

    [Fact]
    public async Task A_route_with_no_payload_at_all_still_works()
    {
        // `options` is optional and a plain picker is the common case — a page must not have to send an
        // empty object to open a file dialog.
        var dialogs = new RecordingDialogs();

        var response = await DispatchAsync(dialogs, Request(FileDialogFacade.OpenFileType));

        Assert.True(response.Success);
        Assert.Null(dialogs.OpenFileSeen);
    }

    [Fact]
    public async Task Save_text_writes_the_page_s_content_and_reports_the_grant_only_outcome()
    {
        var dialogs = new RecordingDialogs();

        var response = await DispatchAsync(dialogs, Request(FileDialogFacade.SaveTextType,
            new { text = "hello wire", options = new { fileName = "note", defaultExtension = "txt" } }));

        Assert.True(response.Success);
        Assert.Equal("hello wire", dialogs.Written);
        Assert.Equal("note", dialogs.SaveSeen!.FileName);
        Assert.Equal("txt", dialogs.SaveSeen.DefaultExtension);
    }

    [Fact]
    public async Task Save_text_without_text_is_a_named_payload_error()
    {
        var response = await DispatchAsync(new RecordingDialogs(), Request(FileDialogFacade.SaveTextType));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.MissingPayloadValue, response.Error!.Code);
    }

    [Theory]
    [InlineData(FileDialogFacade.OpenFolderType, ShellCapability.FolderPicker)]
    [InlineData(FileDialogFacade.SaveFileType, ShellCapability.SavePicker)]
    public async Task A_shell_refusal_arrives_as_a_NAMED_capability_code(string route, string capability)
    {
        // Not UNKNOWN_ERROR. A client must be able to tell "this shell cannot" from "something broke",
        // because the right UI is different — hide the control rather than show a fault (D33/D36).
        var dialogs = new RecordingDialogs { RefuseFolder = true, RefuseSave = true };

        var response = await DispatchAsync(dialogs, Request(route));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.CapabilityNotSupported, response.Error!.Code);
        Assert.Equal(capability, response.Error.Parameters!["capability"]);
    }

    [Fact]
    public async Task A_refusal_never_carries_the_exception_s_own_text_across_the_wire()
    {
        // The standing boundary rule, and the one an OperationException can bypass: its message crosses
        // VERBATIM, so building one from ex.Message would leak whatever the shell put there. The refusal
        // that the shell throws is planted with a marker the response must not contain.
        var dialogs = new RecordingDialogs { RefuseSave = true };

        var response = await DispatchAsync(dialogs, Request(FileDialogFacade.SaveFileType));

        Assert.False(response.Success);
        Assert.DoesNotContain("test-shell", response.Error!.Message ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("NotSupportedException", response.Error.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_route_is_refused_by_the_base()
    {
        var response = await DispatchAsync(new RecordingDialogs(), Request("NOPE"));

        Assert.False(response.Success);
        Assert.Equal(IpcErrorCodes.NoHandler, response.Error!.Code);
    }
}
