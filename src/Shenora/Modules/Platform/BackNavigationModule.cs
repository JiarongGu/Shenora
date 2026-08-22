using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;

namespace Shenora.Modules.Platform;

/// <summary>
/// The page's two routes into <see cref="BackNavigation"/>: take responsibility for the system back
/// gesture, and answer one press.
/// <para>
/// ⚠ <b>The event goes the other way and is not a route.</b> A press is published on the bus
/// (<see cref="BackNavigation.PressedType"/>) and reaches the page as an ordinary notification, because
/// the kit has page→host REQUESTS and host→page NOTIFICATIONS and nothing that is both. That is why a
/// press carries a token: the answer arrives as a separate request and has to name which press it is
/// answering.
/// </para>
/// </summary>
public sealed class BackNavigationModule : ModuleBase
{
    /// <summary>The module name this facade answers on — the same one a press is published under.</summary>
    public const string Module = BackNavigation.Module;

    private readonly BackNavigation _back;

    /// <param name="back">The coordinator this facade speaks for.</param>
    /// <param name="logger">Diagnostics.</param>
    public BackNavigationModule(BackNavigation back, ILogger<BackNavigationModule>? logger = null)
        : base(logger)
    {
        _back = back ?? throw new ArgumentNullException(nameof(back));
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case BackNavigation.InterceptType:
                _back.SetIntercepting(PayloadHelper.GetRequiredValue<bool>(request.Payload, "enabled"));
                return Task.FromResult<object?>(Done());

            case BackNavigation.ResolveType:
            {
                var token = PayloadHelper.GetRequiredValue<string>(request.Payload, "token");
                var handled = PayloadHelper.GetRequiredValue<bool>(request.Payload, "handled");

                // 🔴 An answer nobody is waiting for is REPORTED, not dropped. It means this page's
                // answers are arriving after the press already fell through to the platform — so back
                // "works" while the page's own handling never runs, and every symptom is on the device.
                // The page gets the same fact back so it can log it without a device attached.
                var accepted = _back.Resolve(token, handled);
                if (!accepted)
                    context.Logger.LogWarning(
                        "back: an answer arrived for press {Token}, which is no longer waiting — it timed "
                      + "out or was already answered. This page's back handling is not taking effect.",
                        token);
                return Task.FromResult<object?>(new BackNavigationResult(accepted));
            }

            default:
                throw UnknownType(request);
        }
    }
}

/// <summary>
/// What <see cref="BackNavigation.ResolveType"/> answers with.
/// </summary>
/// <param name="Accepted">
/// False when the press was no longer waiting. ⚠ Not an error — the platform has already taken the
/// press — but a page seeing it repeatedly is a page whose back handling never runs.
/// </param>
public sealed record BackNavigationResult(bool Accepted);
