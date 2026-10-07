using Shenora.Chromium.Interop;
using Shenora.Core.Shell;

namespace Shenora.Chromium.Host;

/// <summary>
/// The app's colour scheme (<see cref="IColorScheme"/>) applied to every request context the shell owns, which is what
/// Chromium takes its colour mode from: the page's <c>prefers-color-scheme</c>, Chrome's own UI and, on Windows, the
/// frame's dark mode. A target gets the setting as it is added, on CEF's UI thread, and again there after each change.
/// </summary>
internal sealed class ChromiumColorSchemes : IDisposable
{
    private readonly IColorScheme _setting;
    private readonly Func<Action, bool> _postToUi;
    private readonly Lock _gate = new();
    // Copy-on-write, so a pass reads one array whatever thread adds, removes or disposes meanwhile.
    private Action<cef_color_variant_t>[] _targets = [];
    private int _posted;
    private int _disposed;

    /// <param name="setting">The app's setting.</param>
    /// <param name="postToUi">Runs work on CEF's UI thread; false when it cannot.</param>
    public ChromiumColorSchemes(IColorScheme setting, Func<Action, bool> postToUi)
    {
        _setting = setting;
        _postToUi = postToUi;
        _setting.Changed += OnChanged;
    }

    /// <summary>Apply the setting to a context now and after each change, until disposed. CEF's UI thread, where each
    /// apply runs.</summary>
    public IDisposable Add(Action<cef_color_variant_t> apply)
    {
        lock (_gate) _targets = [.. _targets, apply];
        AppCallback.Run(() => apply(Variant(_setting.Scheme)));
        return new Target(this, apply);
    }

    /// <summary>Chromium's own request context, which the main windows' pages and Chrome's UI take their colour mode
    /// from. CEF's UI thread, once CEF runs.</summary>
    public static unsafe void ApplyToGlobalContext(cef_color_variant_t variant)
    {
        var context = Cef.cef_request_context_get_global_context();
        if (context == null) return;
        context->set_chrome_color_scheme(context, variant, 0);
        ((_cef_base_ref_counted_t*)context)->release((_cef_base_ref_counted_t*)context);
    }

    /// <summary>The setting now.</summary>
    public ColorScheme Scheme => _setting.Scheme;

    /// <summary>The setting as Chromium takes it, for a context made after it changed. Any thread.</summary>
    public cef_color_variant_t Current => Variant(_setting.Scheme);

    /// <summary>The setting as light or dark: held at one, or else the system's (null when that is unknown).</summary>
    public static bool? Dark(ColorScheme scheme, bool? systemDark) => scheme switch
    {
        ColorScheme.Light => false,
        ColorScheme.Dark => true,
        _ => systemDark,
    };

    private static cef_color_variant_t Variant(ColorScheme scheme) => scheme switch
    {
        ColorScheme.Light => cef_color_variant_t.CEF_COLOR_VARIANT_LIGHT,
        ColorScheme.Dark => cef_color_variant_t.CEF_COLOR_VARIANT_DARK,
        _ => cef_color_variant_t.CEF_COLOR_VARIANT_SYSTEM,
    };

    // Any thread. One pass at a time queued: it reads the setting as it runs, so changes faster than the UI thread
    // apply the latest.
    private void OnChanged(ColorScheme _)
    {
        if (Interlocked.Exchange(ref _posted, 1) == 1) return;
        if (!_postToUi(ApplyAll)) Volatile.Write(ref _posted, 0);
    }

    private void ApplyAll()
    {
        Volatile.Write(ref _posted, 0);
        if (Volatile.Read(ref _disposed) != 0) return;
        var variant = Variant(_setting.Scheme);
        foreach (var apply in Volatile.Read(ref _targets)) AppCallback.Run(() => apply(variant));
    }

    // Any thread: DI's disposal, or the engine's stop. The setting may be the app's own, so letting go of it is guarded.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        AppCallback.Run(() => _setting.Changed -= OnChanged);
        lock (_gate) _targets = [];
    }

    private void Remove(Action<cef_color_variant_t> apply)
    {
        lock (_gate)
        {
            var at = Array.IndexOf(_targets, apply);
            if (at >= 0) _targets = [.. _targets[..at], .. _targets[(at + 1)..]];
        }
    }

    private sealed class Target(ChromiumColorSchemes owner, Action<cef_color_variant_t> apply) : IDisposable
    {
        public void Dispose() => owner.Remove(apply);
    }
}
