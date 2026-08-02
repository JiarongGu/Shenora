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
    'src/Shenora.Core',
    'src/Shenora.Ipc',
    'src/Shenora.Windows',
    // The two mobile faces. Both are listed because the API-baseline coverage check reads IsPackable
    // as the definition of "shipped", so a project claiming it while the tooling skips it is the two
    // halves disagreeing. WHERE each can be packed is the separate question below.
    'src/Shenora.Android',
    'src/Shenora.iOS',
  ],
  /**
   * Packable only on macOS, and therefore skipped by a default `pack` elsewhere. `pack --mac` selects
   * exactly this set.
   *
   * Note what is NOT here: `Shenora.Android` packs perfectly well on Windows. Splitting the old
   * multi-targeted `Shenora.Maui` into two single-TFM packages removed the hazard this list was
   * originally written for — there is no longer any package whose Windows build is a HALF-COMPLETE
   * artifact carrying the same id and version as the real one. Each package now either builds
   * completely on this host or cannot build at all, which is a much easier thing to be correct about.
   *
   * The consequence for the release pipeline is that the macOS job is tiny: one project, and it needs
   * only the `maui-ios` workload. See `docs/2026-08-02-ios-release-design.md`.
   */
  macOnlyPackableProjects: [
    'src/Shenora.iOS',
  ],
  /** The npm package dir (version synced from VersionPrefix by pack/doctor). */
  npmDir: 'src/Shenora.React',
  /** Pack output (gitignored). */
  packagesDir: 'publish/packages',

  // ---- Sample-app desktop loop (wired in Phase 2; see docs/ROADMAP.md) ----
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
   * them, because evidence here is recorded as NUMBERS and prose (ROADMAP, FIX-LOG), never as a
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
