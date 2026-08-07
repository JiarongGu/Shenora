using Microsoft.Extensions.Logging;
using Shenora;
using Shenora.Modules.Operations;
using Shenora.Core.WebView;
using Shenora.Core.Shell;
using Shenora.Engine.Files;
using Shenora.Core.Ipc;

namespace Shenora.Modules.FileDialog;

/// <summary>
/// The page's route to the shell's native file dialogs — <see cref="IFileDialogs"/> over IPC.
///
/// <para>
/// <b>Why the kit ships this rather than leaving it to each app.</b> The kit already had
/// <see cref="ShellCapability.FilePicker"/>, <see cref="ShellCapability.FolderPicker"/> and
/// <see cref="ShellCapability.SavePicker"/> in its vocabulary — three capabilities a shell advertises in
/// the ready handshake — and shipped no way to USE them, so every app wrote the same four routes and then
/// claimed the capability itself. Both of this repo's own samples had done exactly that, independently.
/// A facade the kit owns is also what lets <c>@shenora/react</c> ship the client half, which is the point:
/// one web bundle asks <c>shell.capabilities</c> and adapts, instead of sniffing the platform (D36).
/// </para>
///
/// <para>
/// ⚠ <b>Every field of every options object here is PAGE-SUPPLIED.</b> Most are inert hints the system
/// dialog interprets, and the user still confirms the actual selection — but
/// <see cref="FileDialogOptions.RememberPathKey"/> is not inert: it is handed to the app's
/// <see cref="IFileDialogPathStore"/>, which before these routes existed only ever saw app-authored keys.
/// An implementation that composes the key into a filename is now reachable from the page. See that
/// interface's remarks; the kit ships no store, so the kit cannot fix it for you.
/// </para>
///
/// <para>
/// <b>What it deliberately does NOT do.</b> It never moves file CONTENT except for
/// <see cref="SaveTextType"/>, and that one is bounded on purpose — see its remarks. Reading a picked file
/// is <see cref="IFileDialogs.OpenReadAsync"/> host-side, or the resource interceptor
/// (<c>interceptor.UseFiles</c> + <c>mediaUrl()</c>) when the page wants to RENDER it. Streaming bytes
/// through a JSON envelope would be the kit growing a file-transfer product.
/// </para>
///
/// <list type="table">
///   <listheader><term>Route</term><description>Payload → response</description></listheader>
///   <item><term><see cref="OpenFileType"/></term><description><c>{ options? }</c> → <see cref="FileDialogResult"/></description></item>
///   <item><term><see cref="OpenFolderType"/></term><description><c>{ options? }</c> → <see cref="FileDialogResult"/>. DESKTOP only</description></item>
///   <item><term><see cref="SaveFileType"/></term><description><c>{ options? }</c> → <see cref="FileDialogResult"/>. DESKTOP only</description></item>
///   <item><term><see cref="SaveTextType"/></term><description><c>{ text, options? }</c> → <see cref="FileDialogResult"/>. Every shell</description></item>
/// </list>
/// </summary>
public sealed class FileDialogModule : ModuleBase
{
    /// <summary>The module name this facade answers on.</summary>
    /// <remarks>
    /// Fixed rather than configurable, unlike <see cref="OperationsModule"/>'s. That one takes its name
    /// from options because the registry EMITS events under the same module and the two must not drift;
    /// this facade publishes nothing, so a knob would be a public member earning nothing.
    /// </remarks>
    public const string Module = "SHENORA.DIALOGS";

    /// <summary>Route: pick an existing file. Payload <c>{ options? }</c>.</summary>
    public const string OpenFileType = "OPEN_FILE";

    /// <summary>
    /// Route: pick a folder. Payload <c>{ options? }</c>.
    /// <para>
    /// ⚠ DESKTOP capability (D35) — refused with <see cref="IpcErrorCodes.CapabilityNotSupported"/> on a
    /// shell that has no expression of it. Gate on <see cref="ShellCapability.FolderPicker"/> first.
    /// </para>
    /// </summary>
    public const string OpenFolderType = "OPEN_FOLDER";

    /// <summary>
    /// Route: pick a save destination and get the PATH back. Payload <c>{ options? }</c>.
    /// <para>
    /// ⚠ DESKTOP capability — "give me somewhere to write later" has no mobile expression. Portable logic
    /// wants <see cref="SaveTextType"/>. Gate on <see cref="ShellCapability.SavePicker"/>.
    /// </para>
    /// </summary>
    public const string SaveFileType = "SAVE_FILE";

    /// <summary>
    /// Route: pick a destination AND write text to it, in one call — the PORTABLE save, working on every
    /// shell because the HOST does the writing. Payload <c>{ text, options? }</c>.
    /// <para>
    /// ⚠ <b>TEXT, and bounded on purpose.</b> The content crosses the IPC envelope as JSON, so this is for
    /// what a page legitimately holds in memory and can name — an export, a report, a config — not for
    /// arbitrary or binary payloads. Anything large or binary should be produced host-side and saved
    /// through <see cref="IFileDialogs.SaveAsync"/> directly, where it never enters a message at all.
    /// </para>
    /// <para>
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
                    // The write runs while the host holds the destination open. Encoding is the kit's
                    // default rather than a wire option: a page that needs a specific one is past what a
                    // generic text save should decide for it, and should write host-side.
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
    /// Read the optional <c>options</c> object. Absent = null, which every dialog treats as "your defaults" —
    /// so a page asking for a plain picker sends no payload at all.
    /// </summary>
    private static TOptions? Options<TOptions>(IpcRequest request) where TOptions : FileDialogOptions =>
        PayloadHelper.GetOptionalValue<TOptions>(request.Payload, "options");

    /// <summary>
    /// Turn a shell's capability refusal into the wire's own <see cref="IpcErrorCodes.CapabilityNotSupported"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ The message is the KIT's own words plus the capability NAME — never
    /// <c>ex.Message</c>. Wrapping a caught exception's text in an <see cref="OperationException"/> is a
    /// complete bypass of the error boundary, because that message crosses the wire verbatim
    /// (<c>.claude/knowledge/ipc-contracts.md</c>); the real exception goes to the host log through the
    /// inner-exception channel instead. A refusal is an EXPECTED outcome here — the shell genuinely has no
    /// expression of the capability (D33/D35) — so it gets a named code rather than reaching the boundary
    /// as <see cref="IpcErrorCodes.UnknownError"/> plus a type name.
    /// </remarks>
    private static async Task<object?> RefusalGuarded(string capability, Func<Task<FileDialogResult>> call)
    {
        try
        {
            return await call();
        }
        catch (NotSupportedException ex)
        {
            throw new OperationException(IpcErrorCodes.CapabilityNotSupported,
                new Dictionary<string, string> { ["capability"] = capability },
                $"This shell has no '{capability}'. Read ShellInfo.Capabilities from the ready handshake "
                + "and hide the control instead of calling it.", ex);
        }
    }
}
