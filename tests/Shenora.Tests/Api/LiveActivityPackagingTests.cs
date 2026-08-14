using System.Text.RegularExpressions;

namespace Shenora.Tests.Api;

/// <summary>
/// 🔴 <b>THE SWIFT IS SHIPPED AS SOURCE, SO A FILE THE PACKAGE FORGETS IS A BUILD FAILURE IN EVERY
/// ADOPTER'S APP — and nothing in this repo's own builds can see it.</b>
/// <para>
/// <c>Shenora.iOS.targets</c> names its Swift by path inside the package
/// (<c>$(MSBuildThisFileDirectory)swift/…</c>) and hands those paths to <c>swiftc</c>.
/// <c>ShenoraBuildLiveActivityShim</c> runs for EVERY iOS app that references the package — unconditional
/// since the 0.9.0 link defect — so a file the nupkg does not carry is
/// <c>swiftc: error: no such file or directory</c> at the far end of a long build, for an app that never
/// opted into the feature.
/// </para>
/// <para>
/// ⚠ <b>This shipped.</b> The csproj listed <c>ShenoraLiveActivity.swift</c> alone, which was right at
/// v0.10.0 when it was the only Swift file, and was never extended when <c>ShenoraLayout.swift</c> and
/// <c>ShenoraDefaultViews.swift</c> landed in the same release band. The repo stayed green throughout
/// because the sample and every gate reach the SOURCE tree; only a package consumer resolves
/// <c>buildTransitive/</c> at all. That is the 0.9.0 lesson exactly, one layer up.
/// </para>
/// <para>
/// The csproj now globs the folder, so a NEW file cannot be missed. This test is the guard on that shape:
/// it fails by NAME if the glob is ever replaced by a list that has fallen behind, and it fails if the
/// targets name a Swift file that does not exist at all.
/// </para>
/// </summary>
public class LiveActivityPackagingTests
{
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(LiveActivityPackagingTests).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Shenora.slnx not found above the test assembly.");
    }

    private static string IosDir() => Path.Combine(RepoRoot(), "src", "Shenora.iOS");

    /// <summary>Every <c>swift/NAME.swift</c> the targets file resolves through the package directory.</summary>
    private static string[] SwiftNamedByTargets()
    {
        var targets = File.ReadAllText(Path.Combine(IosDir(), "buildTransitive", "Shenora.iOS.targets"));
        return Regex.Matches(targets, @"\$\(MSBuildThisFileDirectory\)swift/([A-Za-z0-9_.]+\.swift)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void Every_Swift_file_the_targets_name_exists_in_the_repo()
    {
        var named = SwiftNamedByTargets();
        Assert.NotEmpty(named);   // self-check: a regex that matched nothing must not pass

        var missing = named
            .Where(n => !File.Exists(Path.Combine(IosDir(), "buildTransitive", "swift", n)))
            .ToArray();

        Assert.True(missing.Length == 0,
            $"Shenora.iOS.targets compiles Swift file(s) that do not exist: {string.Join(", ", missing)}. "
            + "swiftc fails at the end of a full iOS build, and the message names a path inside the "
            + "package rather than anything in this repo.");
    }

    [Fact]
    public void Every_Swift_file_the_targets_name_is_PACKED_into_the_nupkg()
    {
        var csproj = File.ReadAllText(Path.Combine(IosDir(), "Shenora.iOS.csproj"));

        // The pack entries for that folder: either the glob (which covers everything, and is what the
        // csproj should carry) or explicit per-file includes.
        var packed = Regex.Matches(csproj, @"<None\s+Include=""buildTransitive\\swift\\([^""]+)""[^>]*Pack=""true""",
                RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .ToArray();
        Assert.NotEmpty(packed);   // self-check: no pack entry at all means the whole folder is missing

        // ⚠ `*.swift` is matched as a GLOB, not compared as a name. Treating it as a literal filename is
        // how this check would silently cover nothing the day the csproj is tidied into a glob.
        var globbed = packed.Any(p => p is "*.swift" or "**\\*.swift" or "**");
        if (globbed) return;

        var unpacked = SwiftNamedByTargets().Except(packed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(unpacked.Length == 0,
            $"Shenora.iOS.csproj does not pack Swift file(s) the targets compile: {string.Join(", ", unpacked)}. "
            + "The package would install, restore and import cleanly, then fail every consuming iOS build at "
            + "swiftc. Prefer the glob (`buildTransitive\\swift\\*.swift`) over a list — a list is what fell "
            + "behind last time.");
    }
}
