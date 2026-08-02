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
