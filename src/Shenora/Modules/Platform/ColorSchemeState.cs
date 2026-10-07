using Microsoft.Extensions.Logging;
using Shenora.Core.Shell;

namespace Shenora.Modules.Platform;

/// <summary>
/// The app's colour scheme held in memory: the <see cref="IColorScheme"/> both desktop shells register, seeded from
/// their host options.
/// </summary>
public sealed class ColorSchemeState : IColorScheme
{
    private readonly Lock _gate = new();
    private readonly ILogger? _log;
    private ColorScheme _scheme;

    /// <param name="scheme">Where it starts: the app's saved choice, or <see cref="ColorScheme.System"/>.</param>
    /// <param name="log">Where a <see cref="Changed"/> handler's failure is reported.</param>
    public ColorSchemeState(ColorScheme scheme = ColorScheme.System, ILogger? log = null)
    {
        _scheme = Defined(scheme);
        _log = log;
    }

    /// <inheritdoc />
    public ColorScheme Scheme
    {
        get
        {
            lock (_gate) return _scheme;
        }
    }

    /// <inheritdoc />
    public event Action<ColorScheme>? Changed;

    /// <inheritdoc />
    public void Set(ColorScheme scheme)
    {
        Defined(scheme);
        lock (_gate)
        {
            if (_scheme == scheme) return;
            _scheme = scheme;
        }
        // Each handler on its own: the shells apply the setting from here, and an app's failing handler must not keep
        // it from them.
        if (Changed is not { } changed) return;
        foreach (var handler in changed.GetInvocationList().Cast<Action<ColorScheme>>())
            AppCallback.Run(() => handler(scheme), ex => AppCallback.Log(_log, () => "[Shenora] A colour scheme handler failed", LogLevel.Error, ex));
    }

    private static ColorScheme Defined(ColorScheme scheme) => Enum.IsDefined(scheme)
        ? scheme
        : throw new ArgumentOutOfRangeException(nameof(scheme), scheme, "Not a colour scheme.");
}
