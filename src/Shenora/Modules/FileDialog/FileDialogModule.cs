using Microsoft.Extensions.Logging;
using Shenora.Modules.Requests;
using Shenora.Core.Shell;
using Shenora.Engine.Files;
using Shenora.Core.Ipc;

namespace Shenora.Modules.FileDialog;

/// <summary>
/// The page's route to the shell's native file dialogs — <see cref="IFileDialogs"/> over IPC. Every
/// route below answers with a <see cref="FileDialogResult"/>.
/// <para>
/// ⚠ <b>Every field of every options object here is PAGE-SUPPLIED.</b> Most are inert hints the system
/// dialog interprets, and the user still confirms the actual selection — but
/// <see cref="FileDialogOptions.RememberPathKey"/> is not inert: it is handed to the app's
/// <see cref="IFileDialogPathStore"/>, which before these routes existed only ever saw app-authored keys.
/// See that interface's remarks; the kit ships no store, so the kit cannot fix it for you.
/// </para>
/// <para>
/// It never moves file CONTENT except for <see cref="SaveTextType"/>. Reading a picked file is
/// <see cref="IFileDialogs.OpenReadAsync"/> host-side, or the resource interceptor
/// (<c>interceptor.UseFiles</c> + <c>mediaUrl()</c>) when the page wants to RENDER it.
/// </para>
/// </summary>
public sealed class FileDialogModule : ModuleBase
{
    /// <summary>
    /// The module name this facade answers on. Fixed, unlike <see cref="IpcRequestsModule"/>'s — this
    /// facade publishes no events that would have to match it.
    /// </summary>
    public const string Module = "SHENORA.DIALOGS";

    /// <summary>Route: pick an existing file. Payload <c>{ options? }</c>.</summary>
    public const string OpenFileType = "OPEN_FILE";

    /// <summary>
    /// Route: pick a folder. Payload <c>{ options? }</c>.
    /// ⚠ DESKTOP capability (D35) — refused with <see cref="IpcErrorCodes.CapabilityNotSupported"/> on a
    /// shell that has no expression of it. Gate on <see cref="ShellCapability.FolderPicker"/> first.
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
    /// shell because the HOST does the writing. Payload <c>{ text, options? }</c>.
    /// <para>
    /// ⚠ <b>TEXT</b>, crossing the IPC envelope as JSON — anything large or binary should be produced
    /// host-side and saved through <see cref="IFileDialogs.SaveAsync"/>, where it never enters a message.
    /// The result's <see cref="FileDialogResult.FilePath"/> is null on mobile BY CONTRACT (a revocable
    /// grant, not an address) — a page must not read that as failure.
    /// </para>
    /// </summary>
    public const string SaveTextType = "SAVE_TEXT";

    private readonly IFileDialogs _dialogs;

    /// <param name="dialogs">The shell's dialogs. Registered by whichever shell package the app composed.</param>
    /// <param name="logger">Diagnostics, via <see cref="ModuleBase"/>.</param>
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
    /// ⚠ The message is the KIT's own words plus the capability NAME — never <c>ex.Message</c>, which
    /// crosses the wire verbatim and so bypasses the error boundary entirely
    /// (<c>.claude/knowledge/ipc-contracts.md</c>); the real exception goes to the host log through the
    /// inner-exception channel instead.
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
