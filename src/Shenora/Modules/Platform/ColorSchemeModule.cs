using Microsoft.Extensions.Logging;
using Shenora.Core.Ipc;
using Shenora.Core.Shell;

namespace Shenora.Modules.Platform;

/// <summary>
/// The page's route to the app's colour scheme — <see cref="IColorScheme"/> over IPC, for an app whose own settings
/// offer the choice.
/// </summary>
/// <remarks>
/// ⚠ <b>Read the effect with CSS, not with this.</b> The Chromium shell applies the setting to its browser engine, and
/// the WebView2 shell to each WebView the app gave it to, so the page's <c>prefers-color-scheme</c> follows it, and a
/// stylesheet or a <c>matchMedia</c> listener re-renders for it. This answers only what the page cannot learn there:
/// whether the app follows the OS or is held at one.
/// </remarks>
public sealed class ColorSchemeModule : ModuleBase
{
    /// <summary>The module name this facade answers on.</summary>
    public const string Module = "SHENORA.COLOR_SCHEME";

    /// <summary>Route: the setting now. No payload; answers <c>{ scheme }</c>, <c>"system"</c>, <c>"light"</c> or
    /// <c>"dark"</c>.</summary>
    public const string GetSchemeType = "GET";

    /// <summary>Route: change it. Payload <c>{ scheme }</c>; answers nothing.</summary>
    public const string SetSchemeType = "SET";

    private readonly IColorScheme _scheme;

    /// <param name="scheme">The setting the desktop shells register.</param>
    /// <param name="logger">Diagnostics.</param>
    public ColorSchemeModule(IColorScheme scheme, ILogger<ColorSchemeModule>? logger = null)
        : base(logger)
    {
        _scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
    }

    /// <inheritdoc />
    public override string ModuleName => Module;

    /// <inheritdoc />
    protected override Task<object?> RouteMessageAsync(
        IpcRequest request, IModuleContext context, CancellationToken cancellationToken)
    {
        switch (request.Type.ToUpperInvariant())
        {
            case GetSchemeType:
                return Task.FromResult<object?>(new { scheme = _scheme.Scheme });

            case SetSchemeType:
                // As the enum, so an unknown value is a wire error naming the key, not a silent no-op.
                _scheme.Set(PayloadHelper.GetRequiredValue<ColorScheme>(request.Payload, "scheme"));
                return Done();

            default:
                throw UnknownType(request);
        }
    }
}
