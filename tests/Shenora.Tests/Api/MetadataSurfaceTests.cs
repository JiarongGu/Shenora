namespace Shenora.Tests.Api;

/// <summary>
/// The SemVer + genericity gate for shipped assemblies this test project cannot REFERENCE.
/// <para>
/// Today that is <c>Shenora.Android</c> and <c>Shenora.iOS</c>. <see cref="ApiSurfaceTests"/> covers
/// the five it can load; without this file those packages would be compiled by the gate and checked by
/// nothing — the same shape as the empty <c>/samples/</c> folder that once let the sample be red
/// while <c>verify</c> reported green. See <see cref="MetadataAssemblies"/> for why iOS is checked on
/// macOS only.
/// </para>
/// </summary>
public class MetadataSurfaceTests
{
    /// <summary>Baselines for assemblies rendered from METADATA — deliberately separate from
    /// <c>Api/Baselines/</c>, whose files <see cref="ApiSurfaceTests"/> turns into <c>Assembly.Load</c>
    /// calls that would fail for these.</summary>
    private static string BaselinesDir() => Path.Combine(RepoRoot(), "tests", "Shenora.Tests", "Api", "MetadataBaselines");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Shenora.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "repo root (Shenora.slnx) not found above the test output dir");
        return dir!;
    }

    /// <summary>
    /// The built assembly for a metadata-baselined project. Probes both configurations and takes the
    /// NEWEST — a stale Debug copy silently gating a Release build is the "restored file older than
    /// the assembly built from it" trap from <c>windows-dev-gotchas.md</c>, one layer up.
    /// </summary>
    private static string? FindAssembly(string project, string tfm)
    {
        var candidates = new[] { "Debug", "Release" }
            .Select(cfg => Path.Combine(RepoRoot(), "src", project, "bin", cfg, tfm, project + ".dll"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        return candidates.Length == 0 ? null : candidates[0];
    }

    /// <summary>
    /// The assemblies rendered from metadata. <c>Shenora.Android</c> only — and the omission of
    /// <c>Shenora.iOS</c> is a deliberate, load-bearing choice rather than an oversight.
    /// <para>
    /// This test project is <c>net10.0-windows</c>, so it cannot RUN on macOS, and <c>Shenora.iOS</c>
    /// cannot BUILD anywhere else. There is no host where both exist. A first attempt at this added
    /// iOS behind <c>OperatingSystem.IsMacOS()</c>, which is DEAD CODE — the branch can never execute,
    /// so it would have read as iOS coverage while providing none. Confirmed by building the test
    /// project on the Mac: 5 errors.
    /// </para>
    /// <para>
    /// <see cref="Mobile_baselines_are_identical_while_all_source_is_shared"/> is the honest
    /// substitute: it turns the Android baseline — which IS checked against a real assembly on every
    /// run — into the iOS one's guarantee, for exactly as long as the two projects share every line.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> MetadataAssemblies() =>
        new() { { "Shenora.Android", "net10.0-android" } };

    /// <summary>
    /// <c>Shenora.Android</c> and <c>Shenora.iOS</c> are built from one shared source tree
    /// (<c>src/Shenora.Mobile/</c>) and own no source of their own, so their public surfaces MUST be
    /// identical — which makes the Android baseline, verified against a real assembly above, a
    /// verification of the iOS baseline too. That is the only way iOS is gated from Windows at all.
    /// <para>
    /// The moment either project gains its own source (a <c>Platforms/</c> implementation — the save
    /// picker is the expected first one), the surfaces may legitimately diverge and this reasoning
    /// stops holding. So the check FAILS then, on purpose, saying so: the alternative is a gate that
    /// silently degrades into checking nothing at the exact moment it starts to matter.
    /// </para>
    /// </summary>
    [Fact]
    public void Mobile_baselines_are_identical_while_all_source_is_shared()
    {
        var root = RepoRoot();
        string[] projects = ["Shenora.Android", "Shenora.iOS"];

        var withOwnSource = projects
            .Where(p => Directory.Exists(Path.Combine(root, "src", p)) &&
                        Directory.EnumerateFiles(Path.Combine(root, "src", p), "*.cs", SearchOption.AllDirectories)
                            .Any(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                      !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            .ToArray();

        Assert.True(withOwnSource.Length == 0,
            $"{string.Join(" and ", withOwnSource)} now own source outside src/Shenora.Mobile/, so the two mobile " +
            "surfaces can legitimately differ and this test can no longer stand in for gating Shenora.iOS. " +
            "Regenerate its baseline ON A MAC and add that check to the release pipeline's macOS job " +
            "(TASKS.md A8), then relax this test to whatever is still true.");

        var android = File.ReadAllText(Path.Combine(BaselinesDir(), "Shenora.Android.txt")).ReplaceLineEndings();
        var ios = File.ReadAllText(Path.Combine(BaselinesDir(), "Shenora.iOS.txt")).ReplaceLineEndings();

        Assert.True(android == ios,
            "Shenora.Android and Shenora.iOS compile from identical source but their baselines differ. " +
            "One of them was edited by hand, or the iOS baseline is stale — copy the Android one over it.");
    }

    /// <summary>
    /// Every word used by a metadata-gated assembly's type names. Consumed by
    /// <see cref="SurfaceVocabularyTests.Lexicon_has_no_unused_words"/> so the two gates share one
    /// view of what the surface is. Silent when nothing is built — that case is already failed, loudly,
    /// by the tests above; making the lexicon check fail for the same reason would just double the noise.
    /// </summary>
    internal static IEnumerable<string> AllExportedTypeWords()
    {
        foreach (var row in MetadataAssemblies())
        {
            var assembly = FindAssembly((string)row[0], (string)row[1]);
            if (assembly is null) continue;
            foreach (var name in MetadataSurface.ExportedTypeNames(assembly))
                foreach (var word in SurfaceVocabularyTests.WordsOfName(name))
                    yield return word;
        }
    }

    [Theory]
    [MemberData(nameof(MetadataAssemblies))]
    public void Public_surface_matches_baseline(string project, string tfm)
    {
        var assembly = FindAssembly(project, tfm);
        // NOT skipped when missing. A gate that quietly passes because the artifact was not built is
        // a gate that fails open, which this repo has already paid for once (check-sensitive).
        Assert.True(assembly is not null,
            $"{project} has not been built, so its surface cannot be checked. Run `node devtools/dev.mjs build` " +
            "(it resolves the JDK the Android TFM needs) before the test suite.");

        var actual = MetadataSurface.Render(assembly!).ReplaceLineEndings();
        var baselinePath = Path.Combine(BaselinesDir(), project + ".txt");
        Directory.CreateDirectory(BaselinesDir());

        if (!File.Exists(baselinePath))
        {
            File.WriteAllText(baselinePath + ".actual", actual);
            Assert.Fail($"No metadata baseline for {project}. Review {baselinePath}.actual and copy it to {baselinePath}.");
        }

        var expected = File.ReadAllText(baselinePath).ReplaceLineEndings();
        if (expected != actual)
        {
            File.WriteAllText(baselinePath + ".actual", actual);
            Assert.Fail($"{project} public surface drifted from its metadata baseline. Review " +
                        $"{baselinePath}.actual, copy it over the baseline if intentional, and note the change in CHANGELOG.md. " +
                        "NOTE: this baseline is NAME-level — it cannot see a signature-only change (see MetadataSurface).");
        }
    }

    /// <summary>
    /// The genericity gate for the same assemblies — D22 applies to every shipped package, not just
    /// the loadable ones. Mirrors <see cref="SurfaceVocabularyTests"/>'s rule against the same lexicon.
    /// </summary>
    [Theory]
    [MemberData(nameof(MetadataAssemblies))]
    public void Public_type_names_use_only_mechanism_vocabulary(string project, string tfm)
    {
        var assembly = FindAssembly(project, tfm);
        Assert.True(assembly is not null, $"{project} has not been built — build before running the suite.");

        var lexicon = SurfaceVocabularyTests.Lexicon();
        var offenders = MetadataSurface.ExportedTypeNames(assembly!)
            .SelectMany(SurfaceVocabularyTests.WordsOfName)
            .Distinct(StringComparer.Ordinal)
            .Where(word => !lexicon.Contains(word))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{project} has public type names built from words that are not in the shell/platform lexicon: " +
            $"{string.Join(", ", offenders)}. Rename after the MECHANISM (D22), or add the word to " +
            "tests/Shenora.Tests/Api/surface-lexicon.txt if it really is generic.");
    }

    /// <summary>
    /// The coverage check — and the reason this file is not just two more tests. Every PACKABLE
    /// project under <c>src/</c> must be gated by one baseline or the other. Without it a sixth
    /// package could ship with no baseline at all: <see cref="ApiSurfaceTests"/>'
    /// <c>Every_shipped_assembly_has_a_baseline</c> walks THIS assembly's references, and a package it
    /// cannot reference is exactly the one it cannot notice is missing.
    /// </summary>
    [Fact]
    public void Every_packable_project_has_a_baseline_of_one_kind_or_the_other()
    {
        var root = RepoRoot();
        var runtime = Directory.EnumerateFiles(Path.Combine(root, "tests", "Shenora.Tests", "Api", "Baselines"), "*.txt")
            .Select(Path.GetFileNameWithoutExtension);
        var metadata = Directory.Exists(BaselinesDir())
            ? Directory.EnumerateFiles(BaselinesDir(), "*.txt").Select(Path.GetFileNameWithoutExtension)
            : [];
        var baselined = runtime.Concat(metadata).ToHashSet(StringComparer.Ordinal)!;

        var packable = Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Select(dir => (Name: Path.GetFileName(dir), Csproj: Path.Combine(dir, Path.GetFileName(dir) + ".csproj")))
            .Where(p => File.Exists(p.Csproj))
            .Where(p => File.ReadAllText(p.Csproj).Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToArray();

        Assert.NotEmpty(packable);   // self-check: a glob that matched nothing must not pass for the wrong reason

        var missing = packable.Except(baselined).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            $"These packable projects have no API baseline of either kind, so their surface is ungated: " +
            $"{string.Join(", ", missing)}. Add a runtime baseline (Api/Baselines) if the test project can " +
            "reference it, or a metadata one (Api/MetadataBaselines) if it cannot.");
    }
}
