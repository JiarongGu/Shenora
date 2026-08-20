using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Modules.Clipboard;

/// <summary>
/// The page's route to the NATIVE clipboard — <see cref="IClipboardService"/> over IPC.
/// <para>
/// 🔴 <b>This is not a replacement for <c>navigator.clipboard</c>, and reaching for it by default is a
/// mistake.</b> A gesture-driven copy already works in the page. Two things it cannot do are why these
/// routes exist: putting FILES on the clipboard, which no web API expresses, so the user cannot paste
/// into Explorer or Finder; and access without a user gesture, focus or permission — from a hotkey, a
/// tray action or a background mission.
/// </para>
/// <para>
/// ⚠ <b>A clipboard set is ATOMIC, so the choice is per-COPY, not per-format.</b> Whichever side writes
/// last wins outright: writing the text half with <c>navigator.clipboard</c> and the files through
/// <see cref="WriteType"/> leaves only the files, silently. Write the whole item through one of them.
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
    /// <see cref="ClipboardContent"/>; answers nothing. Refused with
    /// <see cref="IpcErrorCodes.CapabilityNotSupported"/> when the shell has no expression for the
    /// content — <see cref="ClipboardContent.Files"/> on a phone.
    /// </summary>
    public const string WriteType = "WRITE";

    /// <summary>Route: leave the clipboard holding nothing. No payload; answers nothing.</summary>
    public const string ClearType = "CLEAR";

    private readonly IClipboardService _clipboard;

    /// <param name="clipboard">The shell's clipboard, registered by whichever shell package the app composed.</param>
    /// <param name="logger">Diagnostics.</param>
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
    /// ⚠ The KIT's own words, never <c>ex.Message</c> — raw exception text must not cross the wire
    /// (<c>.claude/knowledge/ipc-contracts.md</c>). The shell's own wording reaches the host log as the
    /// inner exception.
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
