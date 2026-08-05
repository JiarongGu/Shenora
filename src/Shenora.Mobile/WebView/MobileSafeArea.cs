using Microsoft.Maui.Controls;
using Shenora.Core;

namespace Shenora.Mobile;

/// <summary>
/// Publishes the platform's window insets to the page, because the web platform's own answer does not
/// on Android — see <see cref="SafeAreaOptions"/> for the two measured reasons.
///
/// <para>
/// The DECISIONS are all in <see cref="SafeAreaScript"/>, which is portable and unit-tested. This type is
/// deliberately thin: read the platform's numbers, hand them to that builder, evaluate the result. That
/// split is what keeps the untestable half — a real webview on a real device — down to "the numbers are
/// right and the script ran".
/// </para>
/// </summary>
public sealed class MobileSafeArea : IDisposable
{
    private readonly HybridWebView _webView;
    private readonly SafeAreaOptions _options;
    private readonly Action<string>? _log;
    private SafeAreaInsets? _last;
    private bool _disposed;

    /// <param name="webView">The webview whose page receives the insets.</param>
    /// <param name="options">What the app asked for. Everything in it is individually declinable.</param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break the page.</param>
    public MobileSafeArea(HybridWebView webView, SafeAreaOptions options, Action<string>? log = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;

        // The DEFAULT (and the splash, if asked for) goes out immediately, with no measurement — that is
        // the entire point of having one. Waiting for the platform here would reproduce the bug.
        Publish(null);

        _webView.HandlerChanged += OnHandlerChanged;
        if (_webView.Handler is not null) Attach();
    }

    /// <summary>
    /// Push a measurement to the page. Safe to call repeatedly; the script is idempotent and an
    /// unchanged value is skipped so a rotation storm does not become a script storm.
    /// </summary>
    public void Report(SafeAreaInsets insets)
    {
        if (_disposed) return;
        if (_last is { } previous && previous == insets) return;
        _last = insets;
        Publish(insets);
    }

    private void Publish(SafeAreaInsets? insets)
    {
        var script = SafeAreaScript.Build(_options, insets);
        try
        {
            // Fire-and-forget with a GUARDED continuation, never bare — the same rule the rest of the
            // mobile shell follows. A page that has not finished loading rejects the evaluation, and that
            // is not worth faulting anything over: the next report will land.
            _ = _webView.EvaluateJavaScriptAsync(script)
                .ContinueWith(t => Log($"safe-area script failed: {t.Exception?.GetType().Name}"),
                              TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Log($"safe-area script could not be evaluated ({ex.GetType().Name})");
        }
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (_webView.Handler is null) return;
        Attach();
    }

    /// <summary>
    /// Subscribe to the platform's inset changes and take a first reading.
    /// <para>
    /// ⚠ Per-platform by necessity and NOT behind a partial method, unlike <c>SaveAsync</c>: a shell that
    /// cannot report insets is not broken, it simply has none to report, so the fallback here is silence
    /// rather than a compile error. That is the opposite call from the save path and the difference is
    /// whether "nothing" is a legitimate answer. Here it is — a tablet in a window has no cutout.
    /// </para>
    /// </summary>
    private void Attach()
    {
#if ANDROID
        if (_webView.Handler?.PlatformView is not global::Android.Views.View view) return;

        // WindowInsetsCompat, not the raw WindowInsets: it is the one that reports consistently across
        // API levels, and it carries systemBars() — the half Android does NOT expose to CSS at all.
        AndroidX.Core.View.ViewCompat.SetOnApplyWindowInsetsListener(view, new InsetListener(this));
        view.RequestApplyInsets();
#elif IOS || MACCATALYST
        if (_webView.Handler?.PlatformView is not UIKit.UIView view) return;
        // iOS reports both bars and the cutout, so a single read after layout is enough; the scene's
        // safeAreaInsets are already in points, which are CSS pixels.
        var insets = view.SafeAreaInsets;
        Report(new SafeAreaInsets(insets.Top, insets.Right, insets.Bottom, insets.Left));
#endif
    }

#if ANDROID
    // The interface is declared without nullable annotations (it is a Java binding), so the override's
    // parameters must match it exactly — nullable-annotated ones are CS8767, not a warning.
    private sealed class InsetListener(MobileSafeArea owner)
        : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        public AndroidX.Core.View.WindowInsetsCompat? OnApplyWindowInsets(
            global::Android.Views.View? v, AndroidX.Core.View.WindowInsetsCompat? insets)
        {
            if (v is null || insets is null) return insets;
            // systemBars() | displayCutout() — the UNION is the point. CSS gets the cutout only, which is
            // why bottom came back 0 on a device whose navigation bar is genuinely 24 CSS px tall.
            var type = AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars()
                     | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout();
            var i = insets.GetInsets(type);
            if (i is null) return insets;

            // Android reports DEVICE pixels; CSS wants CSS pixels. Dividing by the display density is the
            // whole conversion, and getting it wrong is invisible on a 1x screen and wrong everywhere else.
            var density = v.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
            if (density <= 0) density = 1f;

            owner.Report(new SafeAreaInsets(i.Top / density, i.Right / density,
                                            i.Bottom / density, i.Left / density));
            return insets;
        }
    }
#endif

    private void Log(string message)
    {
        try { _log?.Invoke($"[Shenora.Mobile] {message}"); }
        catch { /* a throwing diagnostic sink must never break the page */ }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _webView.HandlerChanged -= OnHandlerChanged;
    }
}
