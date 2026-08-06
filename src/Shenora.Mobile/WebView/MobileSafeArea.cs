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

        // 🔴 ROTATION MOVES AN INSET TO A DIFFERENT EDGE — it does not merely change its size. A cutout
        // that is `top` in portrait becomes `left` or `right` in landscape, and the home indicator's
        // `bottom` shrinks. So "read the insets once and publish them" is not a smaller version of the
        // right behaviour, it is the wrong SHAPE forever: the page goes on reserving a strip along the top
        // while the notch is down the side. Reported against the iOS shell, which read exactly once.
        //
        // `SizeChanged` is MAUI's own signal and fires on both platforms, so the fix is not per-platform
        // even though the bug only showed on one of them.
        _webView.SizeChanged += OnSizeChanged;
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

        // 🔴 NO "skip if unchanged" here, and that is the fix for the second delivery bug rather than an
        // oversight. CSS custom properties live on the DOCUMENT, so every navigation throws them away —
        // and the insets do not change across a navigation, so a value-equality guard meant the new
        // document never received them. Measured: the shell reported "delivered", and the page then
        // loaded and reported `color=transparent`, its own fallback.
        //
        // Publishing on every layout is cheap (one idempotent script) and the generation counter in
        // Publish collapses overlapping attempts, so a rotation storm still only lands once.
        //
        // Log the NUMBERS when they change — not every publish, which fires on every layout pass. Without
        // this the capability is unobservable from the host side: "delivered" is logged once and the page's
        // own diagnostic reports what the PAGE ended up with, which on a shell whose page also reads
        // `env()` is not proof of what the shell published. Rotation is the case that needs the
        // distinction, because it is the one where the two can disagree.
        if (_last != insets)
            Log($"safe-area measured: top={insets.Top:0.#} right={insets.Right:0.#} "
              + $"bottom={insets.Bottom:0.#} left={insets.Left:0.#}");
        _last = insets;
        Publish(insets);
    }

    /// <summary>
    /// Evaluate the script, and KEEP TRYING until the page confirms it ran.
    ///
    /// <para>
    /// 🔴 This retry is the whole delivery mechanism, and it exists because of a measured failure:
    /// publishing once from the constructor put the script into a webview that had no document yet.
    /// <c>EvaluateJavaScriptAsync</c> did not throw — it silently did nothing — so the capability
    /// reported success and the page never received a thing. The proof was <c>color=transparent</c> in
    /// the page's own diagnostic: the page's fallback, not the shell's colour.
    /// </para>
    /// <para>
    /// ⚠ The IDEAL mechanism is document-start injection (AndroidX's <c>DOCUMENT_START_SCRIPT</c>, iOS's
    /// <c>WKUserScript</c> at <c>atDocumentStart</c>), which would land before the first paint instead of
    /// shortly after it. It is not used because it costs a <c>Xamarin.AndroidX.WebKit</c>
    /// <c>PackageReference</c> on EVERY Android consumer for one call — the same "everything references
    /// this, so nothing may tax it" reasoning as D40/D48. Revisit if the shell ever needs that package
    /// for a second reason. The page's own CSS fallback covers the first frame in the meantime.
    /// </para>
    /// </summary>
    private async void Publish(SafeAreaInsets? insets)
    {
        var script = SafeAreaScript.Build(_options, insets);
        var generation = ++_generation;

        // ~2s of attempts on a rising interval. A page that never arrives stops costing anything, and a
        // newer Publish supersedes this one immediately via the generation check.
        foreach (var delay in Delays)
        {
            if (_disposed || generation != _generation) return;
            try
            {
                var result = await _webView.EvaluateJavaScriptAsync(script).ConfigureAwait(true);
                if (result is not null && result.Contains(SafeAreaScript.DeliveredMarker, StringComparison.Ordinal))
                {
                    // Logged ONCE, not per delivery: this fires on every layout pass by design, and a
                    // line per pass would bury the device log the rest of the shell writes to.
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
                // Evaluating before there is a document throws on some hosts and returns null on others.
                // Both mean the same thing here — not yet — so neither is worth faulting over.
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
        // A handler change means a NEW platform view, so this subscribes per view rather than once —
        // but guard against the same view arriving twice (the constructor and HandlerChanged can both
        // reach here), which would double every read.
        if (ReferenceEquals(view, _attachedTo)) return;
        _attachedTo = view;

        // Layout is when the window's insets become known and when they change (rotation, keyboard, a
        // resized window). Cheap to repeat: Publish's generation counter collapses a storm of these into
        // one delivery. (It is NOT deduplicated by value — see Report for why that had to be removed.)
        view.LayoutChange += (_, _) => ReadPlatformInsets();
#endif
        ReadPlatformInsets();
    }

#if ANDROID
    // Android-only: the double-subscribe guard above. Scoped by #if because an unused private field is a
    // build ERROR here (warnings-as-errors), and the iOS half has no per-view subscription to guard.
    private object? _attachedTo;
#endif

    private void OnSizeChanged(object? sender, EventArgs e) => ReadWhileSettling();

    /// <summary>
    /// Re-read the platform's insets now, and again while the rotation settles.
    /// </summary>
    /// <remarks>
    /// ⚠ The follow-up reads are not belt-and-braces. A rotation is ANIMATED, and the size change is
    /// reported at the START of it: on iOS <c>SafeAreaInsets</c> is still the OLD orientation's at that
    /// moment, so a single read on <c>SizeChanged</c> publishes the very values the rotation invalidated.
    /// Reading again across the animation is what makes the final published value the new orientation's.
    /// Cheap to repeat — <see cref="Publish"/> is idempotent and its generation counter collapses the
    /// overlapping attempts into one delivery.
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
            // `async void`: nothing is awaiting this, so an escape here is an unhandled exception on the
            // UI thread rather than a failed read. A missed inset update must never take the app down.
            Log($"safe-area re-read after a size change failed ({ex.GetType().Name})");
        }
    }

    // Across a rotation animation (~300 ms) and a little past it, so the last word belongs to the new
    // orientation even on a slow device.
    private static readonly int[] RotationSettleDelays = [50, 150, 300, 500];

    /// <summary>Ask the platform what its insets are right now, and publish them.</summary>
    private void ReadPlatformInsets()
    {
#if ANDROID
        if (_webView.Handler?.PlatformView is not global::Android.Views.View view) return;

        // 🔴 READ the insets; do NOT install an OnApplyWindowInsetsListener on the webview.
        //
        // Measured, after shipping the listener version and watching it break the very thing it exists
        // to fix: setting a listener REPLACES the view's own inset handling rather than observing it, so
        // the WebView stopped applying insets internally and `env(safe-area-inset-top)` in the page went
        // from 49 to 0. Delegating via ViewCompat.OnApplyWindowInsets did not rescue it. An A/B settled
        // it — with this capability removed from the sample, env() reported 49 again on the next load.
        //
        // GetRootWindowInsets is purely observational: it asks what the window currently has and cannot
        // consume, reorder or suppress anything. A capability must not be able to damage the platform
        // behaviour it supplements — that failure is invisible unless someone thinks to check env().
        var insets = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(view);
        if (insets is null) return;

        // systemBars() | displayCutout() — the UNION is the point. CSS gets the cutout only, which is
        // why bottom came back 0 on a device whose navigation bar is genuinely 24 CSS px tall.
        var i = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars()
                               | AndroidX.Core.View.WindowInsetsCompat.Type.DisplayCutout());
        if (i is null) return;

        // Android reports DEVICE pixels; CSS wants CSS pixels. Dividing by the display density is the
        // whole conversion, and getting it wrong is invisible on a 1x screen and wrong everywhere else.
        var density = view.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
        if (density <= 0) density = 1f;
        Report(new SafeAreaInsets(i.Top / density, i.Right / density, i.Bottom / density, i.Left / density));
#elif IOS || MACCATALYST
        if (_webView.Handler?.PlatformView is not UIKit.UIView view) return;
        // The scene's safeAreaInsets are already in points, which are CSS pixels, and iOS reports both the
        // bars and the cutout — so unlike Android there is no union to take and no conversion to do.
        // ⚠ What this must NOT go back to is being read ONCE: see the constructor. In landscape the same
        // device reports the cutout on `left` or `right` and a much smaller `bottom`.
        var insets = view.SafeAreaInsets;
        Report(new SafeAreaInsets(insets.Top, insets.Right, insets.Bottom, insets.Left));
#endif
    }


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
        _webView.SizeChanged -= OnSizeChanged;
    }
}
