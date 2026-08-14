namespace Shenora.Tests.Api;

/// <summary>
/// The SemVer + genericity gate for shipped assemblies this test project cannot REFERENCE.
/// <para>
/// Today that is <c>Shenora.Android</c> and <c>Shenora.iOS</c>. <see cref="ApiSurfaceTests"/> covers
/// the three it can load; without this file those packages would be compiled by the gate and checked
/// by nothing — the same shape as the empty <c>/samples/</c> folder that once let the sample be red
/// while <c>verify</c> reported green. Both are gated on every run, from a real built assembly.
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
    /// The assemblies rendered from metadata — BOTH mobile faces, gated directly on every run.
    /// <para>
    /// iOS is here because a <c>net10.0-ios</c> LIBRARY builds on Windows with the <c>maui-ios</c>
    /// workload; only an iOS APP needs a Mac. That correction (owner, 2026-08-03) deleted two
    /// workarounds this file used to carry: an <c>OperatingSystem.IsMacOS()</c> branch that was DEAD
    /// CODE (the test project is <c>net10.0-windows</c> and cannot run there, so it read as coverage
    /// while providing none), and a surrogate test that inferred the iOS surface from the Android one
    /// because the two share source. Neither is needed when the real assembly is right here.
    /// </para>
    /// </summary>
    /// <para>
    /// ⚠ This list is HAND-MAINTAINED, and forgetting a project here is a silent fail-open: the coverage
    /// test below only checks that a baseline FILE exists, so a new platform package with an empty
    /// baseline passed both gates while its surface was never rendered. Demonstrated live on 2026-08-04
    /// by the two media faces. The coverage test now rejects an empty baseline as well as a missing one,
    /// which is what turns "I forgot this row" back into a failure.
    /// </para>
    public static TheoryData<string, string> MetadataAssemblies() =>
        new()
        {
            { "Shenora.Android", "net10.0-android" },
            { "Shenora.iOS", "net10.0-ios" },
            // Shenora.Windows' PLAIN-TFM variant. Unlike the two above, the test project CAN reference this
            // package — but only one of its two TFMs, whichever it targets itself (the versioned one), so
            // `ApiSurfaceTests` gates the WinRT implementation and would never see this one. Both variants are
            // hand-written and must expose the SAME public shape, because they are one type name in one
            // package differing only by TFM: a consumer that retargets has to find the same members. Two
            // hand-written shapes with only one gated is precisely the drift this file exists to catch.
            { "Shenora.Windows", "net10.0-windows" },
            // Shenora.Modules.Media.Android/.iOS were here for one commit and are gone (D45): interception moved to
            // the shells and generic serving to Core, leaving them nothing to hold.
        };

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
        // ⚠ NON-EMPTY, not merely present. An empty baseline file satisfied this test while the surface it
        // was supposed to gate went unrendered — demonstrated live on 2026-08-04, when two new platform
        // packages were seeded with empty baselines, were missing from `MetadataAssemblies()` above, and
        // passed every gate with zero coverage. "A file exists" is not the property this test means.
        static IEnumerable<string?> NonEmpty(string dir) =>
            Directory.EnumerateFiles(dir, "*.txt")
                .Where(f => new FileInfo(f).Length > 0)
                .Select(Path.GetFileNameWithoutExtension);

        var runtime = NonEmpty(Path.Combine(root, "tests", "Shenora.Tests", "Api", "Baselines"));
        var metadata = Directory.Exists(BaselinesDir()) ? NonEmpty(BaselinesDir()) : [];
        var baselined = runtime.Concat(metadata).ToHashSet(StringComparer.Ordinal)!;

        // A package can legitimately ship NO managed assembly — `Shenora.Launcher` carries
        // per-RID native binaries and C++ sources and nothing else, so there is no surface to reflect
        // over and neither baseline kind can exist for it. The exemption is opt-in BY THE PROJECT
        // (`<NoManagedSurface>true</NoManagedSurface>`) rather than a name hard-coded here, for the
        // reason this whole file exists: a special case living in the test is one nobody sees when they
        // add the next package. A project that later grows an assembly has to DELETE that line to stay
        // exempt, so the failure direction is "gate turns back on", not "gate silently stays off".
        var packable = Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Select(dir => (Name: Path.GetFileName(dir), Csproj: Path.Combine(dir, Path.GetFileName(dir) + ".csproj")))
            .Where(p => File.Exists(p.Csproj))
            .Select(p => (p.Name, Text: File.ReadAllText(p.Csproj)))
            .Where(p => p.Text.Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal))
            .Where(p => !p.Text.Contains("<NoManagedSurface>true</NoManagedSurface>", StringComparison.Ordinal))
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
