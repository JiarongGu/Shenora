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
    'src/Shenora.WebView2',
    'src/Shenora.WinForms',
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
  /** Sample web (vite) dev server dir + port. */
  sampleWebDir: 'samples/Shenora.Sample.Web',
  vitePort: 3900,
  /** Base CDP port for the sample's --dev mode (randomized + persisted to devtools/.cdp-port). */
  cdpPortBase: 9222,
};
