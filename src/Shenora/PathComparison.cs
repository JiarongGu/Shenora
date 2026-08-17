namespace Shenora;

/// <summary>
/// How two filesystem paths compare on THIS platform — stated once, because more than one containment
/// check needs the answer and they must never disagree about it.
/// </summary>
/// <remarks>
/// 🔴 <b>The failure this removes is silent and one-directional.</b> A check hardcoding
/// <see cref="StringComparison.OrdinalIgnoreCase"/> is WIDER than a case-sensitive filesystem: with an
/// allowed root of <c>…/files/public</c>, a request for <c>…/files/Public/secret</c> passes containment and
/// is served out of a directory the app never allowed. Android's ext4/f2fs is case-sensitive; NTFS and
/// APFS are case-insensitive by default, so the mistake is invisible on the two shells most likely to be
/// developed on.
/// <para>
/// ⚠ <b>A TEST CANNOT HOLD THIS, which is why it is a shared member instead.</b> On Windows the correct
/// answer and the hardcoded one are the same value, so a test asserting either agreement between two
/// checks or a fixed outcome passes with the defect present — verified by sabotage. Sharing the decision
/// makes the divergence unrepresentable rather than merely detectable.
/// </para>
/// <para>
/// Internal: it is one line of policy, not surface an adopter should depend on. Both consumers
/// (<c>Core.WebView.WebViewFiles</c> and <c>Engine.Files.PathClaims</c>) live in this assembly.
/// ⚠ It is deliberately NOT used by <c>Shenora.Windows.WebViewResourceProvider</c>, which is Windows-only
/// by construction and correct with the fixed value.
/// </para>
/// </remarks>
internal static class PathComparison
{
    /// <summary>Whether two paths differing only in case name the same file here.</summary>
    internal static bool IgnoresCase => OperatingSystem.IsWindows();

    /// <summary>The comparison a path containment check must use here.</summary>
    internal static StringComparison ForPaths =>
        IgnoresCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
