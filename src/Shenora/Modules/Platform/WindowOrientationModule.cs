using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Modules.Platform;

/// <summary>
/// The page's route to holding the window at an orientation — <see cref="IWindowOrientation"/> over IPC.
/// </summary>
/// <remarks>
/// 🔴 <b>Why this is not the page's job, even though the web has an API for it.</b>
/// <c>screen.orientation.lock()</c> is only honoured while the document is FULLSCREEN — so a page can
/// hold an orientation only by taking over the display first, which is exactly wrong for the common case
/// (an app that wants to stay portrait everywhere EXCEPT its media viewer). And WKWebView does not
/// implement it at all. The platform call has neither limitation.
/// <para>
/// ⚠ <b>Two routes and no state.</b> There is no "what is the orientation now" route: the page already
/// knows (<c>screen.orientation</c>, or a CSS media query that also re-renders for it), and an IPC answer
/// would be the same fact arriving later. The kit reports nothing here — see
/// <see cref="AppLifecycle"/> for the same decision made the same way.
/// </para>
/// </remarks>
public sealed class WindowOrientationModule : ModuleBase
{
    /// <summary>The module name this facade answers on.</summary>
    public const string Module = "SHENORA.ORIENTATION";

    /// <summary>
    /// Route: hold the window at an orientation. Payload <c>{ orientation }</c>, <c>"portrait"</c> or
    /// <c>"landscape"</c>; answers nothing.
    /// </summary>
    public const string LockType = "LOCK";

    /// <summary>Route: let the platform choose again. No payload; answers nothing.</summary>
    public const string UnlockType = "UNLOCK";

    private readonly IWindowOrientation _orientation;

    /// <param name="orientation">The shell's implementation, registered by <c>UseAndroid</c>/<c>UseIOS</c>.</param>
    /// <param name="logger">Diagnostics.</param>
    public WindowOrientationModule(IWindowOrientation orientation, ILogger<WindowOrientationModule>? logger = null)
        : base(logger)
    {
        _orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case LockType:
                // ⚠ Read as the ENUM, not as a string the shell then parses: an unknown value is then a
                // wire error naming the key, at the boundary, instead of a silent no-op deeper down.
                var orientation = PayloadHelper.GetRequiredValue<WindowOrientation>(
                    request.Payload, "orientation");
                Refused(() => _orientation.Lock(orientation));
                return Done();

            case UnlockType:
                Refused(_orientation.Unlock);
                return Done();

            default:
                throw UnknownType(request);
        }
    }

    /// <summary>
    /// Turn a shell's refusal into the wire's own <see cref="IpcErrorCodes.CapabilityNotSupported"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ The KIT's own words, never <c>ex.Message</c> — raw exception text must not cross the wire
    /// (<c>.claude/knowledge/ipc-contracts.md</c>). The shell's own wording reaches the host log as the
    /// inner exception.
    /// </remarks>
    private static void Refused(Action call)
    {
        try
        {
            call();
        }
        catch (NotSupportedException ex)
        {
            throw new ShenoraException(IpcErrorCodes.CapabilityNotSupported,
                new Dictionary<string, string> { ["capability"] = ShellCapability.WindowOrientation },
                "This shell cannot hold the window at an orientation. Check the ready handshake for "
                + "'windowOrientation' before offering the control.", ex);
        }
    }
}
