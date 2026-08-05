using System.Globalization;
using System.Text;

namespace Shenora.Core;

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
    /// <summary>No insets — the desktop answer, and the correct default for a shell that has none.</summary>
    public static SafeAreaInsets None => new(0, 0, 0, 0);

    /// <summary>True when every edge is zero, which is also what a platform reports before it knows.</summary>
    public bool IsEmpty => Top == 0 && Right == 0 && Bottom == 0 && Left == 0;
}

/// <summary>
/// How a shell hands its window insets to the page, and what it does about the gap before it can.
///
/// <para>
/// <b>Why this exists at all, measured rather than assumed (2026-08-05, Android 16 / API 36).</b> The web
/// platform's own answer — <c>env(safe-area-inset-*)</c> with <c>viewport-fit=cover</c> — is not
/// sufficient on Android, in two ways a page cannot work around:
/// </para>
/// <list type="number">
///   <item>Android reports the display CUTOUT only, never the system bars. Measured
///     <c>bottom=0</c> on a device whose navigation bar is genuinely 24 CSS px tall, so content sits
///     under the gesture pill and no CSS or page script can discover it. iOS reports both.</item>
///   <item>The values are 0 for the WHOLE first page load and only appear on a later one. A page-side
///     re-read (rAF, timeout, <c>resize</c>, <c>visualViewport</c>) was written and did not help,
///     because nothing changes within that document for it to observe.</item>
/// </list>
///
/// <para>
/// So the host — which knows the insets from the platform — has to tell the page. Everything here is
/// OPT-IN and individually declinable: an app that wants none of it passes nothing and gets the plain
/// <c>env()</c> behaviour it has today (D21 — primitives and hooks, never the product).
/// </para>
/// </summary>
public sealed class SafeAreaOptions
{
    /// <summary>
    /// What to publish BEFORE the platform reports a real value, or null to publish nothing until it does.
    ///
    /// <para>
    /// This is the fix for the first-load zeros: a page that starts from the real value starts from
    /// nothing and lays out its first screen under the status bar. Starting from a sensible guess and
    /// correcting is the better trade — the common case is right immediately and the uncommon one moves
    /// once. An app that knows its device class can set the exact number here and never see a correction.
    /// </para>
    /// <para>
    /// ⚠ A measured value only replaces this once it is non-empty. Writing a platform's first-load zeros
    /// over a good default would reintroduce the very bug the default exists to prevent.
    /// </para>
    /// </summary>
    public SafeAreaInsets? Default { get; init; }

    /// <summary>
    /// A CSS colour painted behind the inset strips, or null to leave them transparent.
    ///
    /// <para>
    /// Worth having because a transparent inset shows whatever is behind the webview — on a dark page
    /// over a light shell that reads as a flash of the wrong colour at exactly the moment the user is
    /// looking. The value is passed through verbatim, so any CSS colour works.
    /// </para>
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// How long the layout takes to ease from <see cref="Default"/> to the measured value.
    /// <see cref="TimeSpan.Zero"/> (the default) snaps instead, which is what an app with its own
    /// motion language or an accessibility preference will want.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Cover the page with <see cref="SplashColor"/> until the first real inset arrives, then fade out
    /// over <see cref="Settle"/>. Off by default.
    ///
    /// <para>
    /// This is the third, independent answer to the same race, and it is the only one that hides the
    /// correction completely rather than making it small or smooth. ⚠ It is also the one that can hide a
    /// FAILURE: if the platform never reports, the page would stay covered — so the overlay always
    /// removes itself after <see cref="SplashTimeout"/> whether or not anything arrived.
    /// </para>
    /// </summary>
    public bool Splash { get; init; }

    /// <summary>The splash colour. Falls back to <see cref="Color"/>, then to transparent.</summary>
    public string? SplashColor { get; init; }

    /// <summary>
    /// The longest the splash may cover the page. Defaults to 2 seconds. A page hidden forever because a
    /// platform went quiet is a worse bug than the one the splash is covering.
    /// </summary>
    public TimeSpan SplashTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The prefix for the published CSS custom properties. Defaults to <c>--sa-</c>, giving
    /// <c>--sa-top</c>, <c>--sa-right</c>, <c>--sa-bottom</c> and <c>--sa-left</c>. Configurable because
    /// an app with an existing design-token naming scheme should not have to adopt this one.
    /// </summary>
    public string VariablePrefix { get; init; } = "--sa-";
}

/// <summary>
/// Builds the script a shell injects into the page to publish its safe-area insets.
///
/// <para>
/// A PURE function over <see cref="SafeAreaOptions"/> and an optional measurement, which is the whole
/// point: the interesting decisions — what the defaults are, whether a zero measurement may overwrite
/// them, when the splash gives up — are then testable with no device, no webview and no platform. What
/// is left for a device to prove is only that the platform's numbers are right and that the script runs.
/// </para>
/// </summary>
public static class SafeAreaScript
{
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
            // Only ever written when there is something real to write — see SafeAreaOptions.Default on
            // why a platform's first-load zeros must not overwrite a good default.
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
            // The overlay is created once, keyed by id, and torn down either when a real measurement
            // arrives or when the timeout fires — whichever is first. Both paths are needed: the first
            // is the point, the second is what stops a quiet platform from hiding the page forever.
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

        script.Append("})();");
        return script.ToString();
    }

    private static string Set(string prefix, string side, double value) =>
        SetRaw(prefix, side, value.ToString("0.###", CultureInfo.InvariantCulture) + "px");

    private static string SetRaw(string prefix, string name, string value) =>
        $"r.style.setProperty('{Escape(prefix + name)}','{Escape(value)}');";

    private static string Ms(TimeSpan span) =>
        ((int)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    /// <summary>
    /// Single-quote and backslash escaping, because every value here reaches a JS string literal and one
    /// of them (<see cref="SafeAreaOptions.Color"/>) is app-supplied. Not a security boundary — the app
    /// is the one being protected from its own typo — but a stray quote would otherwise break the whole
    /// injected script silently, which is the worst failure this could have.
    /// </summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
}
