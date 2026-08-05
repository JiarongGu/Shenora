using System.Diagnostics;
using System.Security.Cryptography;
using Shenora.IO;

namespace Shenora.UpdateProbe;

/// <summary>
/// Runs `Shenora.IO`'s staged updater over a REAL directory tree and reports what it found.
///
/// <para>
/// The unit suite covers this engine well and every one of its trees is a FIXTURE the test author
/// wrote — which is exactly the shape that cannot catch the defect this probe exists for. Nothing
/// synthetic contains a `.deps.json`, a satellite assembly, a `runtimes/win-x64/native/` subtree, a
/// `.pdb` nobody meant to ship, or a `wwwroot` with 200 hashed asset names. Whether the DEFAULT
/// intrusion policy is livable is a question only a real build tree can answer.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("""
                update-probe <release-dir> [--install <dir>] [--keep]

                  <release-dir>   a REAL build/publish output, or an adopter's unpacked release.
                  --install       an existing install tree to update IN PLACE (default: a fresh temp one).
                  --keep          leave the sandbox behind for inspection.

                Runs manifest -> stage -> CommitAsync -> ApplyAsync and reports the numbers. Exit 0 only
                if every phase passed AND the user-data check below held.
                """);
            return 2;
        }

        var releaseDir = Path.GetFullPath(args[0]);
        if (!Directory.Exists(releaseDir))
        {
            Console.Error.WriteLine($"update-probe: '{releaseDir}' is not a directory.");
            return 2;
        }

        var keep = args.Contains("--keep");
        var installIndex = Array.IndexOf(args, "--install");
        var sandbox = Path.Combine(Path.GetTempPath(), "shenora-update-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var installRoot = installIndex >= 0 && installIndex + 1 < args.Length
            ? Path.GetFullPath(args[installIndex + 1])
            : Path.Combine(sandbox, "install");
        var stageRoot = Path.Combine(sandbox, "stage");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(stageRoot);

        var failures = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            Console.WriteLine($"release : {releaseDir}");
            Console.WriteLine($"install : {installRoot}");
            Console.WriteLine($"stage   : {stageRoot}");
            Console.WriteLine();

            // ── 1. Build a manifest the way a release pipeline would ─────────────────────────────
            var manifest = BuildManifest(releaseDir, "probe-1.0.0");
            var totalBytes = manifest.Files.Sum(f => f.Size);
            Console.WriteLine($"[1] manifest      {manifest.Files.Count} file(s), "
                + $"{totalBytes / 1024.0 / 1024.0:F1} MB");
            if (manifest.Files.Count == 0)
            {
                // A self-check, not a formality: an empty manifest makes every phase below pass for
                // the wrong reason, and an EMPTY release manifest legitimately removes everything.
                Console.Error.WriteLine("    FAIL  the release directory produced no files — nothing was tested.");
                return 1;
            }

            // ── 2. Stage it, exactly as an app's downloader would ────────────────────────────────
            var stage = new UpdateStage(new UpdateStageOptions { Root = stageRoot, Log = Console.Error.WriteLine });
            CopyTree(releaseDir, stage.StagedDirectory);
            // The FULL release manifest goes into the stage, exactly as FetchAsync writes it: ApplyAsync
            // computes REMOVALS from this file, and CommitAsync refuses to publish a marker without it.
            // ⚠ Omitting this is what the probe's first run did, and it found a real gap — CommitAsync
            // used to publish a marker anyway, so the failure surfaced at APPLY time, in the launcher,
            // after the app had exited. See UpdateStageTests' three "no release manifest" cases.
            File.WriteAllText(Path.Combine(stage.StagedDirectory, "manifest.json"), manifest.ToJson());
            Console.WriteLine($"[2] staged        into {stage.StagedDirectory} (+ manifest.json)");

            // ── 3. CommitAsync — verify every hash AND run the intrusion check ───────────────────
            //
            // THE HEADLINE MEASUREMENT. With no IsUnindexed predicate, every staged file must be
            // listed. A real tree that trips this is telling you the default is too strict, which is
            // the failure that breaks for every user at once rather than for an attacker.
            var committed = stage.CommitAsync(manifest).GetAwaiter().GetResult();
            if (committed.Pending)
            {
                Console.WriteLine($"[3] commit        OK — {manifest.Files.Count} verified, "
                    + "0 would-be intrusions, marker written");
            }
            else
            {
                failures++;
                Console.Error.WriteLine("[3] commit        FAIL — the stage did not become pending.");
                Console.Error.WriteLine("    A REAL tree was rejected by the DEFAULT policy. Read the message above:");
                Console.Error.WriteLine("    if it names honest build output, the default is too strict and that is");
                Console.Error.WriteLine("    the inverted-cost failure this probe exists to find.");
            }

            // ── 4. Plant user data, then apply ───────────────────────────────────────────────────
            //
            // Removals are TRACKED PATHS ONLY, never a directory sweep, because user data lives in
            // the same tree. This plants exactly that and checks it survives — the guard is stated in
            // D30/§4 and is the one whose failure destroys data rather than merely failing.
            var userData = Path.Combine(installRoot, "data", "user-settings.db");
            Directory.CreateDirectory(Path.GetDirectoryName(userData)!);
            File.WriteAllText(userData, "the user's own file — must survive every apply");

            var outcome = stage.ApplyAsync(installRoot).GetAwaiter().GetResult();
            if (outcome.Applied)
            {
                Console.WriteLine($"[4] apply         OK — {outcome.Written.Count} written, "
                    + $"{outcome.Removed.Count} removed");
            }
            else
            {
                failures++;
                Console.Error.WriteLine($"[4] apply         FAIL — {outcome.Failure}");
            }

            if (File.Exists(userData))
            {
                Console.WriteLine("[5] user data     OK — untracked file survived the apply");
            }
            else
            {
                failures++;
                Console.Error.WriteLine("[5] user data     FAIL — an untracked file was DELETED by the apply.");
                Console.Error.WriteLine("    Removals must be tracked paths only; a directory sweep destroys user data.");
            }

            // ── 6. The second release — the diff path, which only a real tree exercises ──────────
            //
            // Re-manifest the SAME tree at a new version. Every hash matches, so the diff must be
            // empty: a release that changed nothing must download nothing. This is where a path
            // normalisation bug shows up — get separators or case wrong and every file reports as
            // "added" on every check, forever, and the updater never converges.
            var second = BuildManifest(releaseDir, "probe-1.0.1");
            var diff = ManifestDiff.Compute(manifest, second);
            if (diff.Added.Count == 0 && diff.Updated.Count == 0 && diff.Removed.Count == 0)
            {
                Console.WriteLine("[6] idempotent    OK — re-manifesting the same tree diffs to nothing");
            }
            else
            {
                failures++;
                Console.Error.WriteLine($"[6] idempotent    FAIL — same tree diffed to "
                    + $"+{diff.Added.Count} ~{diff.Updated.Count} -{diff.Removed.Count}, so an updater "
                    + "would re-download unchanged files forever.");
                foreach (var f in diff.Added.Take(5)) Console.Error.WriteLine($"      added: {f.Path}");
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? $"update-probe: PASSED against a real tree ({manifest.Files.Count} files, {sw.Elapsed.TotalSeconds:F1}s)"
                : $"update-probe: {failures} FAILURE(S) against a real tree");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            if (!keep && Directory.Exists(sandbox))
            {
                try { Directory.Delete(sandbox, recursive: true); }
                catch (IOException) { /* a probe that cannot clean up must not fail the run */ }
            }
            else if (keep)
            {
                Console.WriteLine($"sandbox kept: {sandbox}");
            }
        }
    }

    /// <summary>
    /// What a release pipeline does: walk the tree, hash every file, record manifest-RELATIVE paths
    /// with forward slashes. The separator choice is deliberate and is half of what step 6 checks —
    /// a Windows-built manifest full of backslashes matches nothing on any other host.
    /// </summary>
    private static UpdateManifest BuildManifest(string root, string version)
    {
        var files = new List<ManifestFile>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(full);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            files.Add(new ManifestFile
            {
                Path = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/'),
                Size = new FileInfo(full).Length,
                Sha256 = hash,
            });
        }
        return new UpdateManifest { Version = version, Files = files };
    }

    private static void CopyTree(string from, string to)
    {
        // Manual recursion, never fs-level bulk copy helpers: the family rule is that a bulk copy
        // hides which file failed, and this probe's whole value is naming the file.
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
