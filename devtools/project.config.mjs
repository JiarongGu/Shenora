// project.config.mjs — the ONLY project-specific inputs for the devtools dispatcher.
//
// The dispatcher (dev.mjs) and the scripts under scripts/ are otherwise generic (pattern shared
// with the sibling projects). To reuse this toolkit on another repo, copy devtools/ and edit
// THIS file: names, paths, ports.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

// Single version source: src/Directory.Build.props <VersionPrefix>. Parsed here so the node
// tooling (pack/doctor/release workflow) can never drift from what the assemblies report.
function readVersion() {
  const props = fs.readFileSync(path.join(repo, 'src', 'Directory.Build.props'), 'utf8');
  const m = props.match(/<VersionPrefix>([^<]+)<\/VersionPrefix>/);
  if (!m) throw new Error('VersionPrefix not found in src/Directory.Build.props');
  return m[1].trim();
}

export default {
  name: 'Shenora',
  version: readVersion(),
  solution: 'Shenora.slnx',
  /** Packable .NET projects (lockstep version; `pack` runs dotnet pack on each). */
  packableProjects: [
    'src/Shenora',
    // ⚠ Shenora.Ipc is NOT here either: it folded into Core on 2026-08-07 (D65). IPC is a CORE — the
    // contract both sides agree on — and a separate package id said 'optional', the claim D53/D55 killed.
    // ⚠ Shenora.IO and Shenora.IO.Compression are NOT here: they folded into Core on 2026-08-07
    // (D55), the same call as Media. Their namespaces live on inside Core, so nothing here needs a
    // rename — the packages simply stopped existing.
    // The native launcher's packaging project. Listed here because it IS shipped and the
    // coverage check reads IsPackable as the definition of "shipped" — but it packs DOWNLOADED CI
    // artifacts, not anything built on this machine, so `pack` skips it unless they are staged. See
    // artifactPackableProjects below.
    'src/Shenora.Launcher',
    'src/Shenora.Windows',
    // The two mobile faces. Both are listed because the API-baseline coverage check reads IsPackable
    // as the definition of "shipped", so a project claiming it while the tooling skips it is the two
    // halves disagreeing. WHERE each can be packed is the separate question below.
    'src/Shenora.Android',
    'src/Shenora.iOS',
  ],
  /**
   * Projects that need a macOS host to pack. **EMPTY, and that is the finding** (owner, 2026-08-03).
   *
   * A `net10.0-ios` LIBRARY builds anywhere the `maui-ios` workload is installed — Windows included.
   * Only an iOS APP needs a Mac, because Xcode is what produces the `.app` bundle and runs it; the
   * MSBuild target that blocked the sample (`_ValidateXcodeVersion`) is conditioned on
   * `_CanOutputAppBundle` and never fires for a library. Verified by packing `Shenora.iOS` on Windows
   * with no Mac, no Xcode and none of the override flags: byte-for-byte the same `lib/` layout and
   * nuspec as the Mac-built one.
   *
   * The list stays (rather than the concept being deleted) because it is the honest place to put the
   * next project that genuinely does need a Mac, and because an empty list with this note is a
   * better answer to "why does the release only run on Windows?" than silence.
   */
  macOnlyPackableProjects: [],
  /**
   * Packable projects whose CONTENT comes from CI artifacts rather than from a build on this machine.
   * `pack` skips one when `<project>/artifacts/runtimes/` is absent and says why, instead of failing a
   * routine dev-box pack — while the csproj itself still errors if pack is forced without them, so the
   * only way to ship an empty native package is to work at it.
   */
  artifactPackableProjects: ['src/Shenora.Launcher'],
  /**
   * The npm package that has a BUILD and TESTS — `verify` builds and vitests this one, and the sample
   * web app consumes it. Kept singular because those steps are genuinely about the React client.
   */
  npmDir: 'src/Shenora.React',
  /**
   * EVERY npm package, for the checks that must not miss one: version lockstep against
   * `<VersionPrefix>`, the shipped LICENSE copy, and `pack`. ⚠ Adding a package and forgetting this list
   * is how one gets published outside the lockstep — the failure that consumed 0.2.0 without ever
   * shipping. `@shenora/cli` is pure Node with no build step, which is why it is here but not above.
   */
  npmPackages: ['src/Shenora.React', 'src/Shenora.Cli'],
  /**
   * 🔴 EVERY TRACKED FILE `doctor --fix` MAY REWRITE — the ONE list the release workflow stages and
   * diffs, so its commit cannot miss one. It used to hardcode its own copy, and that copy went stale
   * the moment a SECOND npm package arrived: v0.11.0 published `@shenora/cli` at the right version
   * (the runner's `doctor --fix` synced it) while committing only the React package.json, so the
   * tracked file stayed a release behind. A second list of the same thing is the defect, not the
   * symptom — the workflow now asks for this one.
   * ⚠ `docs/getting-started.md` is here because it shows a `PackageReference`: the snippet a new
   * adopter pastes first, and it sat at 0.10.0 through the 0.11.0 release.
   */
  derivedVersionFiles: [
    'src/Directory.Build.props',
    'CHANGELOG.md',
    'README.md',
    'docs/ARCHITECTURE.md',
    'docs/getting-started.md',
    'src/Shenora.React/package.json',
    'src/Shenora.Cli/package.json',
  ],
  /** Pack output (gitignored). */
  packagesDir: 'publish/packages',

  // ---- Sample-app desktop loop ----
  /** Sample desktop project — `sample` runs it; capture/input tools target its process. */
  sampleProject: 'samples/Shenora.Sample.Desktop',
  processName: 'Shenora.Sample.Desktop',
  shotPrefix: 'shenora',
  /**
   * How many captures `shot`/`wgc` keep in devtools/screenshots (newest first; older ones are
   * pruned BEFORE each capture, so the new one is never the one evicted).
   *
   * They are gitignored, transient verification artifacts and capture is cheap, so without a cap
   * they only ever grow — 53 files / 7.5 MB by the first release, and no doc referenced any of
   * them, because evidence here is recorded as NUMBERS and prose (commit messages), never as a
   * PNG. Raise it if you are mid-investigation and want a longer trail; `--keep N` overrides once.
   */
  shotRetention: 24,
  // ---- The MAUI sample's device loop (`dev.mjs android …`) ----
  /** The MAUI sample project — `android deploy|run` builds and installs it. */
  androidSampleProject: 'samples/Shenora.Sample.Maui',
  androidTfm: 'net10.0-android',
  /**
   * NOT optional for an emulator: most are x86_64 while a default build can produce arm64 only, and
   * the install then fails INSTALL_FAILED_NO_MATCHING_ABIS — which reads like a packaging fault
   * rather than the wrong architecture. Change it for an arm64 phone.
   */
  androidRuntimeIdentifier: 'android-x64',
  androidPackageId: 'com.shenora.sample.maui',
  /** The sample logs everything under one tag, so `android log` reads as a story. */
  androidLogTag: 'SHENORA',

  /** Sample web (vite) dev server dir + port. */
  sampleWebDir: 'samples/Shenora.Sample.Web',
  vitePort: 3900,
  /** Base CDP port for the sample's --dev mode (randomized + persisted to devtools/.cdp-port). */
  cdpPortBase: 9222,
};
