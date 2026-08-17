using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Modules.Clipboard;

/// <summary>
/// The page's route to the NATIVE clipboard — <see cref="IClipboardService"/> over IPC.
/// <para>
/// 🔴 <b>This is not a replacement for <c>navigator.clipboard</c>, and reaching for it by default is a
/// mistake.</b> The page is running in a real browser: a gesture-driven "copy this text" or "copy this
/// picture" already works there, needs no host round trip, and is what the platform expects. Two things
/// it genuinely cannot do are the reason these routes exist:
/// </para>
/// <list type="number">
/// <item><b>FILES.</b> No web API can put a file list on the clipboard, so the user cannot paste into
/// Explorer, Finder or a file manager. There is no polyfill for this.</item>
/// <item><b>Access without a user gesture or focus.</b> <c>navigator.clipboard.read()</c> demands
/// transient activation, document focus and a permission; a host has none of those constraints, so a
/// hotkey, a tray action or a background mission can read and write.</item>
/// </list>
/// <para>
/// ⚠ <b>And because a clipboard set is ATOMIC, the choice is per-COPY, not per-format.</b> A clipboard
/// holds one item; whichever side writes last wins outright. So an item that includes files must be
/// written entirely through <see cref="WriteType"/> — writing the text half with
/// <c>navigator.clipboard</c> and the files here would leave only the files, silently. That is why the
/// routes carry the whole item rather than the file list alone.
/// </para>
/// <para>
/// 🔴 <b>OPT-IN, and think before opting in.</b> <see cref="ReadType"/> lets the page read the user's
/// clipboard at any moment, with no gesture and no prompt — a capability the web withholds deliberately,
/// because a clipboard routinely holds a password or a bank detail the user copied from somewhere else
/// entirely. The kit hands it to app code that is already trusted with the machine; do not mount it for
/// a page that renders third-party content.
/// </para>
/// </summary>
public sealed class ClipboardModule : ModuleBase
{
    /// <summary>The module name this facade answers on.</summary>
    public const string Module = "SHENORA.CLIPBOARD";

    /// <summary>
    /// Route: everything the clipboard is offering, no gesture required. No payload; answers a
    /// <see cref="ClipboardContent"/>.
    /// </summary>
    public const string ReadType = "READ";

    /// <summary>
    /// Route: replace the clipboard with one item. Payload <c>{ content }</c>, a
    /// <see cref="ClipboardContent"/>; answers nothing.
    /// ⚠ Refused with <see cref="IpcErrorCodes.CapabilityNotSupported"/> when the content asks for
    /// something this shell has no expression for — <see cref="ClipboardContent.Files"/> on a phone.
    /// </summary>
    public const string WriteType = "WRITE";

    /// <summary>Route: leave the clipboard holding nothing. No payload; answers nothing.</summary>
    public const string ClearType = "CLEAR";

    private readonly IClipboardService _clipboard;

    /// <param name="clipboard">The shell's clipboard. Registered by whichever shell package the app composed.</param>
    /// <param name="logger">Diagnostics, via <see cref="ModuleBase"/>.</param>
    public ClipboardModule(IClipboardService clipboard, ILogger<ClipboardModule>? logger = null)
        : base(logger)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override async Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case ReadType:
                return await RefusalGuarded(() => _clipboard.GetAsync());

            case WriteType:
                var content = PayloadHelper.GetRequiredValue<ClipboardContent>(request.Payload, "content");
                await RefusalGuarded(async () =>
                {
                    await _clipboard.SetAsync(content).ConfigureAwait(false);
                    return (object?)null;
                }).ConfigureAwait(false);
                return Done();

            case ClearType:
                await _clipboard.ClearAsync().ConfigureAwait(false);
                return Done();

            default:
                throw UnknownType(request);
        }
    }

    /// <summary>
    /// Turn a shell's capability refusal into the wire's own <see cref="IpcErrorCodes.CapabilityNotSupported"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ The message is the KIT's own words — never <c>ex.Message</c>, which crosses the wire verbatim and
    /// so bypasses the error boundary entirely (<c>.claude/knowledge/ipc-contracts.md</c>). The shell's own
    /// wording, which names the platform and the alternative, goes to the host log through the
    /// inner-exception channel instead.
    /// </remarks>
    private static async Task<object?> RefusalGuarded(Func<Task<object?>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            throw new ShenoraException(IpcErrorCodes.CapabilityNotSupported,
                new Dictionary<string, string> { ["capability"] = "clipboard" },
                "This shell cannot put that on the clipboard. Files are a desktop capability; check the "
                + "ready handshake before offering the control.", ex);
        }
    }

    /// <summary>Overload for a route whose call already answers a value.</summary>
    private static async Task<object?> RefusalGuarded(Func<Task<ClipboardContent>> call) =>
        await RefusalGuarded(async () => (object?)await call().ConfigureAwait(false)).ConfigureAwait(false);
}
