using Microsoft.Extensions.Logging;
using Shenora.Modules.Requests;
using Shenora.Core.Shell;
using Shenora.Engine.Files;
using Shenora.Core.Ipc;

namespace Shenora.Modules.FileDialog;

/// <summary>
/// The page's route to the shell's native file dialogs — <see cref="IFileDialogs"/> over IPC. Every
/// route below answers with a <see cref="FileDialogResult"/>, and none moves file CONTENT except
/// <see cref="SaveTextType"/>; reading a picked file is <see cref="IFileDialogs.OpenReadAsync"/>
/// host-side, or the resource interceptor when the page wants to RENDER it.
/// <para>
/// ⚠ <b>Every field of every options object here is PAGE-SUPPLIED.</b> Most are inert hints, but
/// <see cref="FileDialogOptions.RememberPathKey"/> is handed to the app's
/// <see cref="IFileDialogPathStore"/> — the kit ships no store, so the kit cannot sanitise it for you.
/// </para>
/// </summary>
public sealed class FileDialogModule : ModuleBase
{
    /// <summary>
    /// The module name this facade answers on. Fixed, unlike <see cref="IpcRequestsModule"/>'s: this
    /// facade publishes no events that would have to match it.
    /// </summary>
    public const string Module = "SHENORA.DIALOGS";

    /// <summary>Route: pick an existing file. Payload <c>{ options? }</c>.</summary>
    public const string OpenFileType = "OPEN_FILE";

    /// <summary>
    /// Route: pick a folder. Payload <c>{ options? }</c>.
    /// ⚠ DESKTOP capability (D35) — gate on <see cref="ShellCapability.FolderPicker"/>, or a shell with
    /// no expression of it refuses with <see cref="IpcErrorCodes.CapabilityNotSupported"/>.
    /// </summary>
    public const string OpenFolderType = "OPEN_FOLDER";

    /// <summary>
    /// Route: pick a save destination and get the PATH back. Payload <c>{ options? }</c>.
    /// ⚠ DESKTOP capability — portable logic wants <see cref="SaveTextType"/>. Gate on
    /// <see cref="ShellCapability.SavePicker"/>.
    /// </summary>
    public const string SaveFileType = "SAVE_FILE";

    /// <summary>
    /// Route: pick a destination AND write text to it, in one call — the PORTABLE save, working on every
    /// shell because the HOST does the writing. Payload <c>{ text, options? }</c>; anything large or
    /// binary should be produced host-side through <see cref="IFileDialogs.SaveAsync"/> instead, where it
    /// never enters a message.
    /// <para>
    /// ⚠ The result's <see cref="FileDialogResult.FilePath"/> is null on mobile BY CONTRACT (a revocable
    /// grant, not an address) — a page must not read that as failure.
    /// </para>
    /// </summary>
    public const string SaveTextType = "SAVE_TEXT";

    private readonly IFileDialogs _dialogs;

    /// <param name="dialogs">The shell's dialogs, registered by whichever shell package the app composed.</param>
    /// <param name="logger">Diagnostics.</param>
    public FileDialogModule(IFileDialogs dialogs, ILogger<FileDialogModule>? logger = null)
        : base(logger)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override async Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case OpenFileType:
                return await RefusalGuarded(ShellCapability.FilePicker,
                    () => _dialogs.OpenFileAsync(Options<OpenFileOptions>(request)));

            case OpenFolderType:
                return await RefusalGuarded(ShellCapability.FolderPicker,
                    () => _dialogs.OpenFolderAsync(Options<OpenFolderOptions>(request)));

            case SaveFileType:
                return await RefusalGuarded(ShellCapability.SavePicker,
                    () => _dialogs.SaveFileAsync(Options<SaveFileOptions>(request)));

            case SaveTextType:
                var text = PayloadHelper.GetRequiredValue<string>(request.Payload, "text");
                var saveOptions = Options<SaveFileOptions>(request);
                return await RefusalGuarded(ShellCapability.SavePicker, () => _dialogs.SaveAsync(
                    saveOptions,
                    // The write runs while the host holds the destination open.
                    async (stream, ct) =>
                    {
                        var writer = new StreamWriter(stream, Files.DefaultEncoding, leaveOpen: true);
                        await using (writer.ConfigureAwait(false))
                        {
                            await writer.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
                        }
                    },
                    cancellationToken));

            default:
                throw UnknownType(request);
        }
    }

    /// <summary>
    /// Read the optional <c>options</c> object. Absent = null, which every dialog treats as "your defaults".
    /// </summary>
    private static TOptions? Options<TOptions>(IpcRequest request) where TOptions : FileDialogOptions =>
        PayloadHelper.GetOptionalValue<TOptions>(request.Payload, "options");

    /// <summary>
    /// Turn a shell's capability refusal into the wire's own <see cref="IpcErrorCodes.CapabilityNotSupported"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ The KIT's own words plus the capability NAME, never <c>ex.Message</c> — raw exception text must
    /// not cross the wire (<c>.claude/knowledge/ipc-contracts.md</c>). The real exception reaches the
    /// host log as the inner exception.
    /// </remarks>
    private static async Task<object?> RefusalGuarded(string capability, Func<Task<FileDialogResult>> call)
    {
        try
        {
            return await call();
        }
        catch (NotSupportedException ex)
        {
            throw new ShenoraException(IpcErrorCodes.CapabilityNotSupported,
                new Dictionary<string, string> { ["capability"] = capability },
                $"This shell has no '{capability}'. Read ShellInfo.Capabilities from the ready handshake "
                + "and hide the control instead of calling it.", ex);
        }
    }
}
