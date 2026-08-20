using System.Globalization;
using System.Text;

namespace Shenora.Modules.Platform;

/// <summary>
/// The window insets a page must not draw under — status bar, navigation bar, camera cutout, home
/// indicator — in CSS pixels.
/// </summary>
/// <param name="Top">Top inset.</param>
/// <param name="Right">Right inset.</param>
/// <param name="Bottom">Bottom inset.</param>
/// <param name="Left">Left inset.</param>
public readonly record struct SafeAreaInsets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>No insets — the desktop answer, and the default for a shell that has none.</summary>
    public static SafeAreaInsets None => new(0, 0, 0, 0);

    /// <summary>True when every edge is zero, which is also what a platform reports before it knows.</summary>
    public bool IsEmpty => Top == 0 && Right == 0 && Bottom == 0 && Left == 0;
}

/// <summary>
/// How a shell hands its window insets to the page, and what it does about the gap before it can.
///
/// <para>
/// ⚠ <c>env(safe-area-inset-*)</c> is not sufficient on Android, in two ways a page cannot work around:
/// it reports the display CUTOUT only and never the system bars (so content sits under the gesture pill),
/// and it is 0 for the WHOLE first page load. iOS reports both. So the host, which knows the insets from
/// the platform, tells the page instead. Every option here is opt-in and individually declinable.
/// </para>
/// </summary>
public sealed class SafeAreaOptions
{
    /// <summary>
    /// What to publish BEFORE the platform reports a real value, or null to publish nothing until it does.
    /// This is the answer to the first-load zeros: an app that knows its device class sets the exact number
    /// here and never sees a correction.
    ///
    /// <para>
    /// ⚠ A measurement only replaces this once it is NON-EMPTY — first-load zeros must not overwrite a
    /// good default.
    /// </para>
    /// </summary>
    public SafeAreaInsets? Default { get; init; }

    /// <summary>
    /// A CSS colour painted behind the inset strips, or null to leave them transparent — in which case they
    /// show whatever is behind the webview. Passed through verbatim, so any CSS colour works.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// How long the layout takes to ease from <see cref="Default"/> to the measured value.
    /// <see cref="TimeSpan.Zero"/> (the default) snaps instead.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Cover the page with <see cref="SplashColor"/> until the first real inset arrives, then fade out
    /// over <see cref="Settle"/>. Off by default.
    ///
    /// <para>
    /// ⚠ This one can hide a FAILURE: if the platform never reports, the page would stay covered — so the
    /// overlay always removes itself after <see cref="SplashTimeout"/> whether or not anything arrived.
    /// </para>
    /// </summary>
    public bool Splash { get; init; }

    /// <summary>The splash colour. Falls back to <see cref="Color"/>, then to transparent.</summary>
    public string? SplashColor { get; init; }

    /// <summary>The longest the splash may cover the page. Defaults to 2 seconds.</summary>
    public TimeSpan SplashTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The prefix for the published CSS custom properties. Defaults to <c>--sa-</c>, giving
    /// <c>--sa-top</c>, <c>--sa-right</c>, <c>--sa-bottom</c> and <c>--sa-left</c>.
    /// </summary>
    public string VariablePrefix { get; init; } = "--sa-";
}

/// <summary>
/// Builds the script a shell injects into the page to publish its safe-area insets — a pure function over
/// <see cref="SafeAreaOptions"/> and an optional measurement, so the decisions are testable with no device
/// and no webview.
/// </summary>
public static class SafeAreaScript
{
    /// <summary>
    /// What <see cref="Build"/>'s script evaluates to when it actually ran in a document. A shell should
    /// treat any other result as NOT DELIVERED and try again.
    ///
    /// <para>
    /// ⚠ Evaluating script against a webview that has no document yet does not throw — it silently does
    /// nothing — so without this marker "the call succeeded" and "the page received it" are the same
    /// observation.
    /// </para>
    /// </summary>
    public const string DeliveredMarker = "shenora-safe-area";

    /// <summary>
    /// The document-start script. Idempotent, so a shell may inject it once and then call it again with
    /// fresh insets on every change; safe to run before <c>&lt;body&gt;</c> exists.
    /// </summary>
    /// <param name="options">What the app asked for.</param>
    /// <param name="insets">A measurement, or null when the platform has not reported yet.</param>
    public static string Build(SafeAreaOptions options, SafeAreaInsets? insets = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prefix = string.IsNullOrWhiteSpace(options.VariablePrefix) ? "--sa-" : options.VariablePrefix;
        var effective = insets is { IsEmpty: false } measured ? measured : options.Default;

        var script = new StringBuilder();
        script.Append("(function(){var r=document.documentElement;");

        if (effective is { } v)
        {
            // Only written when there is something real: first-load zeros must not overwrite a good default.
            script.Append(Set(prefix, "top", v.Top))
                  .Append(Set(prefix, "right", v.Right))
                  .Append(Set(prefix, "bottom", v.Bottom))
                  .Append(Set(prefix, "left", v.Left));
        }

        if (options.Settle > TimeSpan.Zero)
            script.Append(SetRaw(prefix, "settle", Ms(options.Settle)));

        if (!string.IsNullOrWhiteSpace(options.Color))
            script.Append(SetRaw(prefix, "color", options.Color!));

        if (options.Splash)
        {
            var colour = options.SplashColor ?? options.Color ?? "transparent";
            // Created once, keyed by id; torn down on the first real measurement or on the timeout,
            // whichever comes first. The timeout is what stops a quiet platform hiding the page forever.
            script.Append("var s=document.getElementById('shenora-safe-splash');")
                  .Append("if(!s&&!window.__shenoraSplashDone){s=document.createElement('div');")
                  .Append("s.id='shenora-safe-splash';")
                  .Append("s.style.cssText='position:fixed;inset:0;z-index:2147483647;pointer-events:none;")
                  .Append("transition:opacity ").Append(Ms(options.Settle)).Append(" ease-out;background:")
                  .Append(Escape(colour)).Append("';")
                  .Append("(document.body||r).appendChild(s);")
                  .Append("setTimeout(function(){window.__shenoraDismissSafeSplash&&window.__shenoraDismissSafeSplash();},")
                  .Append(((int)options.SplashTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture))
                  .Append(");}")
                  .Append("window.__shenoraDismissSafeSplash=function(){var e=document.getElementById('shenora-safe-splash');")
                  .Append("window.__shenoraSplashDone=true;if(!e)return;e.style.opacity='0';")
                  .Append("setTimeout(function(){e.remove();},")
                  .Append(((int)Math.Max(1, options.Settle.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture))
                  .Append(");};");

            if (insets is { IsEmpty: false })
                script.Append("window.__shenoraDismissSafeSplash();");
        }

        // A truthy marker, so a caller can tell DELIVERED from EVALUATED-AGAINST-NOTHING — see
        // DeliveredMarker.
        script.Append("return '").Append(DeliveredMarker).Append("';})();");
        return script.ToString();
    }

    private static string Set(string prefix, string side, double value) =>
        SetRaw(prefix, side, value.ToString("0.###", CultureInfo.InvariantCulture) + "px");

    private static string SetRaw(string prefix, string name, string value) =>
        $"r.style.setProperty('{Escape(prefix + name)}','{Escape(value)}');";

    private static string Ms(TimeSpan span) =>
        ((int)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    /// <summary>
    /// Single-quote and backslash escaping: every value here reaches a JS string literal and one of them
    /// (<see cref="SafeAreaOptions.Color"/>) is app-supplied, where a stray quote would break the whole
    /// injected script silently. Not a security boundary.
    /// </summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
