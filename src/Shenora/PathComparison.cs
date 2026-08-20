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
/// developed on — <b>and a TEST CANNOT HOLD IT</b>, because on Windows the correct answer and the
/// hardcoded one are the same value (verified by sabotage). Sharing the decision makes the divergence
/// unrepresentable rather than merely detectable.
/// <para>
/// Both consumers (<c>Core.WebView.WebViewFiles</c> and <c>Engine.Files.PathClaims</c>) live in this
/// assembly, and the two checks are otherwise deliberately different. ⚠ NOT used by
/// <c>Shenora.Windows.WebViewResourceProvider</c>, which is Windows-only by construction.
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
