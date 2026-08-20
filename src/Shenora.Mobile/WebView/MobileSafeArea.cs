using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls;
using Shenora;
using Shenora.Modules.Platform;

namespace Shenora.Mobile;

/// <summary>
/// Publishes the platform's window insets to the page, because the web platform's own answer does not on
/// Android — see <see cref="SafeAreaOptions"/> for the two measured reasons. Thin by design: read the
/// numbers, hand them to <see cref="SafeAreaScript"/>, evaluate the result.
/// </summary>
public sealed class MobileSafeArea : IDisposable
{
    private readonly HybridWebView _webView;
    private readonly SafeAreaOptions _options;
    private readonly ILogger? _log;
    private SafeAreaInsets? _last;
    private bool _disposed;

    /// <param name="webView">The webview whose page receives the insets.</param>
    /// <param name="options">What the app asked for. Everything in it is individually declinable.</param>
    /// <param name="log">Optional diagnostics. Guarded — a throwing sink must not break the page.</param>
    public MobileSafeArea(HybridWebView webView, SafeAreaOptions options, ILogger? log = null)
    {
        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;

        // The DEFAULT (and the splash, if asked for) goes out immediately, with no measurement — waiting
        // for the platform here is the bug it exists to cover.
        Publish(null);

        // 🔴 ROTATION MOVES AN INSET TO A DIFFERENT EDGE, it does not merely resize it: a cutout that is
        // `top` in portrait is `left` or `right` in landscape, so reading ONCE publishes the wrong SHAPE for
        // ever. `SizeChanged` is MAUI's own signal and fires on both platforms.
        _webView.SizeChanged += OnSizeChanged;
        _webView.HandlerChanged += OnHandlerChanged;
        if (_webView.Handler is not null) Attach();
    }

    /// <summary>Push a measurement to the page. Safe to call repeatedly, and every call publishes.</summary>
    public void Report(SafeAreaInsets insets)
    {
        if (_disposed) return;

        // 🔴 NEVER skip an unchanged value here. CSS custom properties live on the DOCUMENT, so every
        // navigation throws them away — and the insets do NOT change across one, so a value-equality guard
        // leaves the new document with nothing while the shell still reports "delivered". Republishing is
        // cheap: the script is idempotent and Publish's generation counter collapses a storm.
        // ⚠ The NUMBERS are logged only when they change; this runs on every layout pass. The page's own
        // diagnostic reports what the PAGE ended up with, which on a shell whose page also reads `env()` is
        // not proof of what the shell published.
        if (_last != insets)
            Log($"safe-area measured: top={insets.Top:0.#} right={insets.Right:0.#} "
              + $"bottom={insets.Bottom:0.#} left={insets.Left:0.#}");
        _last = insets;
        Publish(insets);
    }

    /// <summary>
    /// Evaluate the script, and KEEP TRYING until the page confirms it ran.
    /// <para>
    /// 🔴 The retry IS the delivery mechanism: evaluating against a webview that has no document yet does
    /// not throw, it silently does nothing — so a single publish reports success and delivers nothing.
    /// </para>
    /// </summary>
    private async void Publish(SafeAreaInsets? insets)
    {
        var script = SafeAreaScript.Build(_options, insets);
        var generation = ++_generation;

        // ~2s of attempts on a rising interval; a newer Publish supersedes this one via the generation.
        foreach (var delay in Delays)
        {
            if (_disposed || generation != _generation) return;
            try
            {
                var result = await _webView.EvaluateJavaScriptAsync(script).ConfigureAwait(true);
                if (result is not null && result.Contains(SafeAreaScript.DeliveredMarker, StringComparison.Ordinal))
                {
                    // Logged ONCE — this fires on every layout pass.
                    if (!_delivered)
                    {
                        _delivered = true;
                        Log($"safe-area delivered to the page ({(insets is null ? "default" : "measured")})");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                // Evaluating before there is a document throws on some hosts and returns null on others;
                // both mean "not yet".
                Log($"safe-area not delivered yet ({ex.GetType().Name}); retrying");
            }

            if (delay > 0) await Task.Delay(delay).ConfigureAwait(true);
        }

        Log("safe-area was never delivered — the page kept no document. "
          + "The page's own CSS fallback is what it is laying out with.");
    }

    // 0 first so a webview that IS ready pays nothing, then a short ramp.
    private static readonly int[] Delays = [0, 50, 100, 200, 300, 500, 800];
    private int _generation;
    private bool _delivered;

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (_webView.Handler is null) return;
        Attach();
    }

    /// <summary>
    /// Subscribe to the platform's inset changes and take a first reading. ⚠ Per-platform, and a platform
    /// with no arm falls through in SILENCE — "no insets to report" is a legitimate answer here.
    /// </summary>
    private void Attach()
    {
#if ANDROID
        if (_webView.Handler?.PlatformView is not global::Android.Views.View view) return;
        // The constructor and HandlerChanged can both reach here with the same view, which would double
        // every read.
        if (ReferenceEquals(view, _attachedTo)) return;

        // ⚠ DETACH THE PREVIOUS VIEW FIRST. A handler change means a NEW platform view, and a stale
        // subscription leaves Android holding this object through the closure while a dead view still fires
        // reads. The handler lives in a field because an anonymous lambda cannot be unsubscribed later.
        DetachPlatformView();
        _attachedTo = view;

        // Layout is when the insets become known and when they change (rotation, keyboard, a resized
        // window). Cheap to repeat, and NOT deduplicated by value — see Report.
        _layoutChanged = (_, _) => ReadPlatformInsets();
        view.LayoutChange += _layoutChanged;
#endif
        ReadPlatformInsets();
    }

#if ANDROID
    // Scoped by #if because an unused private field is a build ERROR here (warnings-as-errors).
    private object? _attachedTo;

    /// <summary>The live <c>LayoutChange</c> handler, held so it can be removed from the view it is on.</summary>
    private EventHandler<global::Android.Views.View.LayoutChangeEventArgs>? _layoutChanged;

    /// <summary>Unsubscribe from whichever platform view we are currently attached to. Safe to call twice.</summary>
    private void DetachPlatformView()
    {
        if (_attachedTo is global::Android.Views.View previous && _layoutChanged is not null)
        {
            // The view may already be torn down, and a dispose path must not throw.
            try { previous.LayoutChange -= _layoutChanged; }
            catch (Exception) { /* already gone */ }
        }
        _attachedTo = null;
        _layoutChanged = null;
    }
#endif

    private void OnSizeChanged(object? sender, EventArgs e) => ReadWhileSettling();

    /// <summary>Re-read the platform's insets now, and again while the rotation settles.</summary>
    /// <remarks>
    /// ⚠ The follow-up reads are load-bearing. A rotation is ANIMATED and the size change is reported at its
    /// START, when iOS still reports the OLD orientation's <c>SafeAreaInsets</c> — so a single read on
    /// <c>SizeChanged</c> publishes the very values the rotation invalidated.
    /// </remarks>
    private async void ReadWhileSettling()
    {
        try
        {
            ReadPlatformInsets();
            foreach (var delay in RotationSettleDelays)
            {
                await Task.Delay(delay).ConfigureAwait(true);
                if (_disposed) return;
                ReadPlatformInsets();
            }
        }
        catch (Exception ex)
        {
            // `async void`: an escape here is an unhandled exception on the UI thread, not a failed read.
            Log($"safe-area re-read after a size change failed ({ex.GetType().Name})");
        }
    }

    // Across a rotation animation (~300 ms) and past it, so the last word is the new orientation's.
    private static readonly int[] RotationSettleDelays = [50, 150, 300, 500];

    /// <summary>Ask the platform what its insets are right now, and publish them.</summary>
    private void ReadPlatformInsets()
    {
#if ANDROID
        if (_webView.Handler?.PlatformView is not global::Android.Views.View view) return;

        // 🔴 READ the insets; do NOT install an OnApplyWindowInsetsListener on the webview. Setting one
        // REPLACES the view's own inset handling rather than observing it, so the WebView stops applying
        // insets internally and `env(safe-area-inset-top)` in the page drops to 0 — invisible unless someone
        // checks env(). GetRootWindowInsets is purely observational and cannot consume or suppress anything.
        var insets = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(view);
        if (insets is null) return;

        // systemBars() | displayCutout(): the UNION. CSS gets the cutout only, which is why bottom
        // reads 0 on a device whose navigation bar is genuinely 24 CSS px tall.
        var i = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars()
                               | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());
        if (i is null) return;

        // Android reports DEVICE pixels; CSS wants CSS pixels. Getting the density wrong is invisible on
        // a 1x screen and wrong everywhere else.
        var density = view.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
        if (density <= 0) density = 1f;
        Report(new SafeAreaInsets(i.Top / density, i.Right / density, i.Bottom / density, i.Left / density));
#elif IOS || MACCATALYST
        if (_webView.Handler?.PlatformView is not UIKit.UIView view) return;
        // Already in points, which are CSS pixels, and iOS reports both the bars and the cutout — no
        // union to take and no conversion to do. ⚠ Must never be read ONCE: see the constructor.
        var insets = view.SafeAreaInsets;
        Report(new SafeAreaInsets(insets.Top, insets.Right, insets.Bottom, insets.Left));
#endif
    }


    private void Log(string message) =>
        Shenora.AppCallback.Log(_log, () => $"[Shenora.Mobile] {message}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _webView.HandlerChanged -= OnHandlerChanged;
        _webView.SizeChanged -= OnSizeChanged;
#if ANDROID
        // The platform view outlives this object; without this Android holds it through the closure.
        DetachPlatformView();
#endif
    }
}
