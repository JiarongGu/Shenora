// Shenora devtools dispatcher (family pattern: one entry, allow-listed once).
//   node devtools/dev.mjs build            - dotnet build the solution + npm build the react package
//   node devtools/dev.mjs test [dotnet|npm|clipboard] - dotnet test + vitest (or just one side).
//                                             `clipboard` runs the REAL-clipboard suite the gate holds
//                                             out — it drives the machine's one system clipboard.
//   node devtools/dev.mjs verify           - build · test · check-sensitive --tree · knowledge check + footprint (the "am I done?" gate)
//   node devtools/dev.mjs pack             - nupkgs + npm tarball -> publish/packages (lockstep version, sha256 printed)
//   node devtools/dev.mjs doctor [--fix]   - version/readme drift check (npm package.json + README headline vs VersionPrefix)
//   node devtools/dev.mjs changelog [--fix] [--version X.Y.Z] [--date YYYY-MM-DD] - stamp "## Unreleased" for the release
//   node devtools/dev.mjs sample [--dev]   - run the sample desktop app (Phase 2+)
//   node devtools/dev.mjs vite             - run the sample web dev server (Phase 2+)
//   node devtools/dev.mjs shot|wgc [name]  - capture the sample window (PrintWindow / occlusion-immune WGC)
//   node devtools/dev.mjs click|rclick|move <fx> <fy>       - background mouse at client fractions (no CDP, no focus steal)
//   node devtools/dev.mjs drag <fx1> <fy1> <fx2> <fy2>      - background press-move-release between two fractions
//   node devtools/dev.mjs input <args…>    - raw win-input passthrough (list | click | rclick | move | drag)
//   node devtools/dev.mjs responsiveness <fx> <fy> [--label name] [--duration|--interval|--timeout ms]
//                                           - click + SendMessageTimeout(WM_NULL) UI-thread stall probe
//   node devtools/dev.mjs update-probe [dir] - drive the staged updater over a REAL tree (default: publish the desktop sample)
//   node devtools/dev.mjs knowledge <…>    - rule-base + skills doctor (check | footprint | new <name> [--core])
//   node devtools/dev.mjs clean [--all]     - drop devtools/_* build output (--all: sources + publish/ too)
//   node devtools/dev.mjs check-sensitive [--tree|--history] - leak scan (--history = one-off audit)
//   node devtools/dev.mjs reserved-paths   - a path Windows cannot check out (a stray `nul`, `com1`, …)
//   node devtools/dev.mjs install-hooks    - point core.hooksPath at devtools/hooks (once per clone)
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import config from './project.config.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const [cmd, ...args] = process.argv.slice(2);

const run = (exe, argv, opts = {}) => {
  const r = spawnSync(exe, argv, { stdio: 'inherit', cwd: repo, shell: false, ...opts });
  process.exitCode = r.status ?? 1;
  return r.status === 0;
};
// npm on Windows is npm.cmd — needs a shell, and Node 24 deprecates shell+args-array (DEP0190),
// so npm invocations go through a single command string.
const runNpm = (script, opts = {}) => {
  const r = spawnSync(`npm ${script}`, { stdio: 'inherit', cwd: repo, shell: true, ...opts });
  process.exitCode = r.status ?? 1;
  return r.status === 0;
};
// A fresh clone / CI runner has no node_modules — auto-install once so build/test/pack/verify
// work without a documented manual step (npm ci = exact lockfile versions).
const ensureNpmDeps = (dir) => {
  if (fs.existsSync(path.join(dir, 'node_modules'))) return true;
  return step('npm ci (first run — installing dev dependencies)', () => runNpm('ci', { cwd: dir }));
};
const step = (label, fn) => {
  console.log(`\n=== ${label} ===`);
  const ok = fn();
  if (!ok) console.error(`  ${label} FAILED`);
  return ok;
};
const npmDirAbs = path.join(repo, ...config.npmDir.split('/'));
const readNpmPackage = () => JSON.parse(fs.readFileSync(path.join(npmDirAbs, 'package.json'), 'utf8'));

// `@shenora/cli` is TypeScript with a real build, like the React package — so it is BUILT and
// TYPE-CHECKED by the gate rather than trusted. A CLI that ships broken output is worse than one that
// does not ship: the adopter meets it at the moment they are trying to reach a device for the first time.
const cliDirAbs = path.join(repo, 'src', 'Shenora.Cli');

// ---- The Android TFM needs a JDK, and the failure without one is unhelpful.
//
// Shenora.Mobile targets net10.0-android, so `dotnet build` of the solution shells out to the Android
// SDK, which needs JAVA_HOME. Unset, MSBuild fails with a bare `error XA5300: The Java SDK directory
// could not be found` pointing at an install page — on a machine that already HAS a JDK, because
// Android Studio ships one and nothing exported the variable.
//
// So probe for it (the same candidate list the server-backed sibling's APK build uses, proven on this
// machine) and export it for the child process only. Every candidate is derived from an environment
// variable, never a literal path — a real install root must not appear in a tracked file
// (.claude/rules/sensitive-info.md). Returns null when there is genuinely none, and the caller says
// so with the fix rather than letting XA5300 speak for it.
function resolveJdk() {
  const usable = (dir) => Boolean(dir) && fs.existsSync(path.join(dir, 'bin', 'java.exe'));
  if (usable(process.env.JAVA_HOME)) return process.env.JAVA_HOME;
  const candidates = [
    [process.env.ProgramFiles, 'Android', 'Android Studio', 'jbr'],
    [process.env['ProgramFiles(x86)'], 'Android', 'Android Studio', 'jbr'],
    [process.env.LOCALAPPDATA, 'Programs', 'Android Studio', 'jbr'],
    [process.env.ProgramFiles, 'Android', 'Android Studio', 'jre'],
  ];
  for (const [root, ...rest] of candidates) {
    if (!root) continue;
    const dir = path.join(root, ...rest);
    if (usable(dir)) return dir;
  }
  return null;
}

/** Environment for a build that includes the Android TFM: JAVA_HOME resolved, or an actionable stop. */
function androidBuildEnv() {
  if (process.env.JAVA_HOME && fs.existsSync(path.join(process.env.JAVA_HOME, 'bin', 'java.exe'))) {
    return process.env;
  }
  const jdk = resolveJdk();
  if (!jdk) {
    console.error(
      '\n  No JDK found, and Shenora.Mobile (net10.0-android) cannot build without one.\n' +
      '  Set JAVA_HOME to a JDK 17+ — Android Studio ships one in its `jbr` folder — or install one.\n' +
      '  This is a machine prerequisite, not a repo setting; see devtools/README.md.');
    return null;
  }
  console.log(`  (JAVA_HOME not set — using the JDK found beside Android Studio)`);
  return { ...process.env, JAVA_HOME: jdk };
}

// ---- A platform TFM needs its WORKLOAD, and the failure without one does not name the prerequisite.
//
// Same shape as the JDK probe above, for the same reason: `dotnet build` of the solution stops with
// `NETSDK1147: To build this project, the following workloads must be installed: ios`, repeated per
// target, and nothing says whether that is a broken repo or a missing install. It cost a session
// discovering that a RED gate was environmental — and worse, `verify` reads as FAILED on commits that
// are fine, which is the one thing a gate must never do quietly.
//
// ⚠ Platforms are READ FROM THE CSPROJ FILES so a TFM added later cannot go unchecked, but which
// platforms need a workload at all is a fact about the .NET SDK and is stated here: `net10.0-windows`
// needs none, and treating it like the others reported a missing `windows` workload that does not exist.
const WORKLOAD_PLATFORMS = new Set(['android', 'ios', 'maccatalyst', 'macos', 'tvos']);

/** Platform workloads the solution needs and this machine does not have. Empty when it cannot ask. */
function missingWorkloads() {
  const platforms = new Set();
  const scan = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (entry.name !== 'bin' && entry.name !== 'obj' && entry.name !== 'node_modules') scan(abs);
      } else if (entry.name.endsWith('.csproj')) {
        for (const m of fs.readFileSync(abs, 'utf8').matchAll(/net\d+\.\d+-([a-z]+)/g)) {
          if (WORKLOAD_PLATFORMS.has(m[1])) platforms.add(m[1]);
        }
      }
    }
  };
  scan(path.join(repo, 'src'));
  if (platforms.size === 0) return [];

  const listed = spawnSync('dotnet', ['workload', 'list'], { encoding: 'utf8' });
  // Cannot ask — let the build speak for itself rather than inventing a diagnosis from a failed probe.
  if (listed.status !== 0 || !listed.stdout) return [];

  // ⚠ The ID COLUMN only. Matching anywhere in the output would let a platform named in a header or a
  // hint line read as INSTALLED, which fails in the dangerous direction — silently restoring the
  // confusing build error this exists to replace.
  const ids = listed.stdout.split('\n')
    .map((line) => line.trim().split(/\s+/)[0] ?? '')
    .filter((id) => /^[a-z][a-z0-9-]*$/.test(id));
  return [...platforms].filter((p) => !ids.some((id) => id === p || id.endsWith(`-${p}`)));
}

// ---- Evict this repo's packages from the NuGet GLOBAL cache after packing.
//
// NuGet keys the global folder (~/.nuget/packages) on id+VERSION and it wins over every source, so
// re-packing the same pre-release version leaves consumers restoring the OLD copy — silently, with
// no warning and no restore error. Found in P6.1: a consumer resolved a Shenora.Windows packed
// before the D19 re-layer, so `Shenora.Windows` was simply absent from its dependency graph and the
// build failed with "namespace does not exist" while the freshly packed nupkg on disk was correct.
// `--no-cache` does NOT help (that is HTTP caching). Since a fresh pack makes any cached copy of
// these exact ids stale by definition, evicting them here removes the trap instead of documenting
// it. Scoped strictly to the ids this repo produces.
/**
 * Entry NAMES inside a zip, without inflating anything — enough to ask "is this file in the package?".
 *
 * Hand-rolled because devtools has no dependencies and listing names needs no decompression: walk the
 * central directory and read each header's file name. ⚠ Zip64 is not handled; it announces itself with
 * 0xffff entries and we say so rather than silently reporting a short list, because a check that quietly
 * inspects less than it claims is the failure mode this whole gate exists to prevent.
 */
function zipEntryNames(file) {
  const buf = fs.readFileSync(file);
  let eocd = -1;
  for (let i = buf.length - 22; i >= 0 && i > buf.length - 66_000; i--) {
    if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  }
  if (eocd < 0) throw new Error(`${path.basename(file)}: no zip end-of-central-directory record`);
  const count = buf.readUInt16LE(eocd + 10);
  if (count === 0xffff) throw new Error(`${path.basename(file)}: Zip64, which this reader does not handle`);

  const names = [];
  let at = buf.readUInt32LE(eocd + 16);
  for (let i = 0; i < count; i++) {
    if (buf.readUInt32LE(at) !== 0x02014b50) throw new Error(`${path.basename(file)}: bad central header at ${at}`);
    const nameLen = buf.readUInt16LE(at + 28);
    const extraLen = buf.readUInt16LE(at + 30);
    const commentLen = buf.readUInt16LE(at + 32);
    names.push(buf.toString('utf8', at + 46, at + 46 + nameLen).replace(/\\/g, '/'));
    at += 46 + nameLen + extraLen + commentLen;
  }
  return names;
}

/**
 * 🔴 EVERY FILE A SHIPPED `.targets` NAMES MUST BE INSIDE THE SHIPPED PACKAGE.
 *
 * This exists because that invariant broke and nothing noticed: `Shenora.iOS.csproj` packed
 * `ShenoraLiveActivity.swift` by NAME — correct when it was the only Swift file — and was never extended
 * when two more landed. `ShenoraBuildLiveActivityShim` compiles them for EVERY consuming iOS app, so the
 * next release would have failed every adopter's build at `swiftc: no such file or directory`, naming a
 * path inside the nupkg. Found 2026-08-10, one release band after it appeared.
 *
 * ⚠ **NO PROJECT-REFERENCE CHECK CAN SEE THIS**, which is the whole point of putting it here rather than in
 * a unit test: this repo's own builds resolve `buildTransitive/` from the SOURCE tree, so the sample and
 * every gate stayed green. It is the same layer as the 0.9.0 defect (five undefined symbols in a published
 * package), and the same lesson — read the artifact, do not trust "Build succeeded".
 *
 * The referenced paths come from the SOURCE targets rather than the packed copy, so no inflate is needed;
 * what is asserted is that the package CARRIES them.
 */
function checkPackagedBuildAssets(outDir) {
  const packages = fs.readdirSync(outDir).filter((f) => f.endsWith('.nupkg'));
  let checked = 0;
  let ok = true;

  for (const pkg of packages) {
    // `Shenora.iOS.0.10.0.nupkg` -> `Shenora.iOS`. The version is always the trailing three numeric parts.
    const id = pkg.replace(/\.nupkg$/, '').replace(/\.\d+\.\d+\.\d+$/, '');
    const targetsDir = path.join(repo, 'src', id, 'buildTransitive');
    if (!fs.existsSync(targetsDir)) continue;

    const referenced = new Set();
    for (const entry of fs.readdirSync(targetsDir)) {
      if (!entry.endsWith('.targets') && !entry.endsWith('.props')) continue;
      const text = fs.readFileSync(path.join(targetsDir, entry), 'utf8');
      for (const m of text.matchAll(/\$\(MSBuildThisFileDirectory\)([A-Za-z0-9_./\\-]+)/g)) {
        referenced.add(m[1].replace(/\\/g, '/'));
      }
      referenced.add(entry);
    }
    if (referenced.size === 0) continue;

    const names = new Set(zipEntryNames(path.join(outDir, pkg)));
    const missing = [...referenced].filter((rel) => !names.has(`buildTransitive/${rel}`)).sort();
    checked++;

    if (missing.length > 0) {
      ok = false;
      console.error(`  ✖ ${pkg} is missing ${missing.length} file(s) its own targets name:`);
      for (const rel of missing) console.error(`      buildTransitive/${rel}`);
      console.error('    A consumer resolves these from the PACKAGE, so their build fails where ours cannot.');
    } else {
      // Printed, not silent: this gate's clean answer would otherwise be indistinguishable from a gate
      // that found no packages to inspect.
      console.log(`  ok  ${pkg} carries all ${referenced.size} file(s) its targets name`);
    }
  }

  if (checked === 0) console.log('  ok  no package ships a buildTransitive/ targets file — nothing to check');
  return ok;
}

function evictGlobalCache() {
  const home = process.env.USERPROFILE || process.env.HOME;
  if (!home) { console.log('  (no home dir — skipped)'); return true; }
  const root = path.join(home, '.nuget', 'packages');
  if (!fs.existsSync(root)) { console.log('  (no global packages folder — nothing to evict)'); return true; }
  let evicted = 0;
  for (const proj of config.packableProjects) {
    const id = path.basename(proj).replace(/\.csproj$/i, '');
    const dir = path.join(root, id.toLowerCase(), config.version);
    if (!fs.existsSync(dir)) continue;
    try {
      fs.rmSync(dir, { recursive: true, force: true });
      evicted++;
    } catch (error) {
      // A locked file means some other process is mid-restore; say so rather than failing the pack.
      console.log(`  could not evict ${id} ${config.version}: ${error.message}`);
    }
  }
  console.log(`  evicted ${evicted} cached package(s) for version ${config.version}`);
  return true;
}

// ---- doctor: the version story has ONE source (src/Directory.Build.props VersionPrefix).
// changelog-doctor: stamp the CHANGELOG's `## Unreleased` heading with the version being released, the
// way the pipeline already stamps VersionPrefix, the npm version and the README `## Status` headline.
// PORTED FROM LYNTAI, where it was earned the expensive way: cutting a release was otherwise the one
// place a human had to remember a manual edit, and a version shipped with its section still titled
// "Unreleased" because of it. Shenora is one release away from the same mistake — its heading reads
// `## Unreleased (0.1.0)` right now.
//
// Three heading shapes are accepted, covering what this file and Lyntai's both use:
//   `## Unreleased`                → `## X.Y.Z — 2026-07-31`
//   `## Unreleased (0.1.0)`        → `## X.Y.Z — 2026-07-31`   (the parenthesised hint is a placeholder;
//                                                               the stamp replaces it, never keeps it)
//   `## Unreleased — <title>`      → `## X.Y.Z — <title> (2026-07-31)`
// so an author who wants a titled release writes the title on the Unreleased heading in advance.
// Nothing is ever invented here. IDEMPOTENT: a heading for the version already present means the
// release was already stamped (a pipeline re-run), and the file is left untouched.
const unreleasedHeading =
  /^## Unreleased(?:[ \t]*\([^)]*\))?[ \t]*(?:[—–-][ \t]*(.+?))?[ \t]*$/m;

// THE STALE-RELEASE GATE, earned by v0.6.0 — which published 0.5.1's CODE. The work was committed
// locally and never pushed, so the workflow released the remote's tree: it bumped the version
// correctly, found nothing under `## Unreleased` to stamp, and shipped a version with no changelog
// entry at all. **The empty section was the signal, and it was there and unused.** A release whose
// changelog says nothing is a release nobody wrote anything for, which is very nearly a proof that the
// tree is not the one the author was working in.
//
// It FAILS rather than warns, deliberately, and that is a different judgement from the size/style
// budgets this repo keeps non-fatal (`RULES_INDEX.md`: "correctness stops a release; style warns").
// Publishing the wrong code is correctness. The asymmetry decides it: a false stop costs one changelog
// line and always has an obvious fix, while a miss burns a version number — which this repo has now
// done twice (0.2.0 consumed without shipping, 0.6.0 released stale). There is no override flag on
// purpose: the escape hatch IS writing the line, and any other one would get used.
//
// "Content" means at least one BULLET, not merely a non-blank line. `### Added` with nothing under it
// is the exact artefact a half-finished release leaves behind, and it would satisfy any looser test.
function unreleasedBody(changelog, headingMatch) {
  const from = headingMatch.index + headingMatch[0].length;
  const rest = changelog.slice(from);
  const next = rest.search(/^## /m);
  return next < 0 ? rest : rest.slice(0, next);
}

function changelogDoctor({ fix = false, version = config.version, date } = {}) {
  const file = path.join(repo, 'CHANGELOG.md');
  const changelog = fs.readFileSync(file, 'utf8');
  const stamped = new RegExp(`^## ${version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:[ \t]|$)`, 'm');

  if (stamped.test(changelog)) {
    // Already stamped — a pipeline re-run of a release that got this far once. Its content was gated
    // on the first pass, so re-checking here would only be able to fail a release that has already
    // published packages.
    console.log(`  ok  CHANGELOG already has a "## ${version}" heading`);
    return true;
  }

  const match = changelog.match(unreleasedHeading);
  if (!match) {
    // Neither a section for this version nor an Unreleased one to promote: nothing DOCUMENTS what is
    // being shipped. THIS IS THE 0.6.0 PATH, and it used to warn and carry on.
    console.error(`  FAIL no "## ${version}" and no "## Unreleased" heading in CHANGELOG.md, so this `
      + 'release documents nothing. That is how v0.6.0 shipped 0.5.1\'s code: the work had never been '
      + 'pushed, so the workflow released the remote tree and found nothing to stamp. **Check that the '
      + 'commits you mean to release are actually on the remote**, then add a `## Unreleased` section '
      + 'describing them. See CHANGELOG.md under `## 0.6.0`.');
    return false;
  }

  const body = unreleasedBody(changelog, match);
  if (!/^[ \t]*[-*][ \t]+\S/m.test(body)) {
    console.error(`  FAIL CHANGELOG "## Unreleased" has no entries, so releasing ${version} would ship `
      + 'an empty section. An empty Unreleased is the signal that the tree is not the one the work was '
      + 'done in — v0.6.0 published the previous release\'s code exactly this way. **Check that the '
      + 'commits you mean to release are on the remote**, then write at least one bullet. There is no '
      + 'override: the fix is one line, and a burned version number is not recoverable.');
    return false;
  }

  if (!fix) {
    console.error(`  FAIL CHANGELOG "## Unreleased" is not stamped for ${version} — `
      + 'run `node devtools/dev.mjs changelog --fix` (the release workflow does this for you).');
    return false;
  }

  const title = match[1]?.trim();
  const on = date ?? new Date().toISOString().slice(0, 10); // UTC — releases run on a UTC runner
  const heading = title ? `## ${version} — ${title} (${on})` : `## ${version} — ${on}`;
  fs.writeFileSync(file, changelog.replace(unreleasedHeading, heading));
  console.log(`  fixed CHANGELOG "${match[0].trim()}" -> "${heading}"`);
  return true;
}

// ONE owner for "where does a capture go" — `shot` and `wgc` both had their own copy of the
// default-name + mkdir + join, which is how the two drifted apart in the first place.
//
// It also PRUNES, because nothing else ever did: screenshots are gitignored, transient verification
// artifacts and capturing is a keystroke, so the folder only grows (53 files / 7.5 MB by v0.1.0,
// with no doc referring to any of them — evidence in this repo is numbers and prose, not PNGs).
// Pruning happens BEFORE the capture writes, so the newest file can never be the one evicted, and
// every deletion is printed: a cleanup that removes work silently is worse than one that never runs.
function shotTarget(name, argv) {
  const dir = path.join(repo, 'devtools', 'screenshots');
  fs.mkdirSync(dir, { recursive: true });

  const keepAt = argv.indexOf('--keep');
  const keep = keepAt >= 0 ? Number(argv[keepAt + 1]) : (config.shotRetention ?? 24);
  if (Number.isFinite(keep) && keep >= 0) {
    const shots = fs.readdirSync(dir)
      .filter((f) => f.toLowerCase().endsWith('.png'))
      .map((f) => ({ f, at: fs.statSync(path.join(dir, f)).mtimeMs }))
      .sort((a, b) => b.at - a.at);          // newest first
    // keep-1: leave room for the capture about to be taken, so the steady state is exactly `keep`.
    const stale = shots.slice(Math.max(0, keep - 1));
    for (const { f } of stale) {
      try { fs.rmSync(path.join(dir, f)); } catch { /* held open by a viewer — skip, try next run */ }
    }
    if (stale.length) console.log(`screenshots: pruned ${stale.length} older capture(s), keeping ${keep}`);
  }

  const stamp = new Date().toISOString().slice(11, 19).replaceAll(':', '');
  return path.join(dir, `${name ?? `${config.shotPrefix}-${stamp}`}.png`);
}

/**
 * Tracked markdown that could carry a version-bearing snippet: the root README and everything under
 * `docs/`. Deliberately not the whole tree — `CHANGELOG.md` is history by definition (a 0.10.0
 * snippet in the 0.10.0 section is CORRECT), and `local/` is private and archival.
 */
function trackedDocsWithSnippets() {
  const found = ['README.md'];
  const walkDocs = (dir) => {
    for (const entry of fs.readdirSync(path.join(repo, dir), { withFileTypes: true })) {
      const rel = `${dir}/${entry.name}`;
      if (entry.isDirectory()) walkDocs(rel);
      else if (entry.name.endsWith('.md') && rel !== 'docs/DECISIONS.md') found.push(rel);
    }
  };
  walkDocs('docs');
  return found;
}

// The npm package.json version and the README "## Status" headline are derived; `doctor` fails
// on drift, `doctor --fix` (also run by `pack`) rewrites them.
/**
 * Is a JDK reachable? The ANDROID head needs one and says so from deep inside MSBuild.
 *
 * 🔴 **This is a PREREQUISITE that hides behind incremental builds.** `verify` builds the whole solution,
 * so `Shenora.Android` needs a JDK — but only when it actually rebuilds. It therefore stays green for
 * days on a machine with no `JAVA_HOME` and fails the moment someone touches a mobile source, with
 * `error XA5300: The Java SDK directory could not be found` from a targets file nobody has opened. Hit
 * exactly that way on 2026-08-20: verify had passed all day and broke on a one-line comment edit.
 *
 * ⚠ Android Studio ships a JDK in `jbr/` and sets NO variable, so the common case is a machine that HAS
 * one and cannot say where — which is the same reason `@shenora/cli`'s android half hunts for it.
 */
function reportJdk() {
  const fromEnv = process.env.JAVA_HOME;
  const candidates = [
    fromEnv,
    path.join(process.env.LOCALAPPDATA ?? '', 'Programs', 'Android Studio', 'jbr'),
    path.join(process.env.ProgramFiles ?? '', 'Android', 'Android Studio', 'jbr'),
  ].filter(Boolean);

  const found = candidates.find((c) => fs.existsSync(path.join(c, 'bin', 'java.exe'))
    || fs.existsSync(path.join(c, 'bin', 'java')));

  if (found && fromEnv) { console.log(`  ok      JDK                  ${found}`); return true; }
  if (found) {
    console.log(`  ok      JDK                  ${found}  (found, but JAVA_HOME is unset)`);
    console.log('          Set JAVA_HOME to it, or a mobile-source change makes `verify` fail with');
    console.log('          `XA5300: The Java SDK directory could not be found` from inside MSBuild.');
    return true;
  }
  console.error('  MISSING JDK                  the Android head cannot build without one');
  console.error('          It fails as `XA5300` from a targets file, which reads as a broken SDK.');
  console.error('          Android Studio ships one in `jbr/` and sets no variable; point JAVA_HOME at it.');
  return false;
}

function doctor({ fix = false } = {}) {
  let problems = 0;
  const fail = (msg) => { problems++; console.error('  FAIL ' + msg); };

  // EVERY npm package, not just the first. `@shenora/cli` was added on 2026-08-08 and the version check
  // read one hardcoded directory — so the new package would have been born outside the lockstep this
  // repo treats as load-bearing (a hand-bump consumed 0.2.0 outright). One list, checked in a loop.
  for (const dir of config.npmPackages) {
    const pkgPath = path.join(repo, ...dir.split('/'), 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
    if (pkg.version === config.version) continue;
    if (fix) {
      fs.writeFileSync(pkgPath, fs.readFileSync(pkgPath, 'utf8')
        .replace(/"version":\s*"[^"]+"/, `"version": "${config.version}"`));
      console.log(`  fixed ${dir}/package.json version -> ${config.version}`);
    } else fail(`${dir}/package.json version ${pkg.version} != VersionPrefix ${config.version}`);
  }

  const readmePath = path.join(repo, 'README.md');
  const readme = fs.readFileSync(readmePath, 'utf8');
  const headline = readme.match(/\*\*v(\d+\.\d+\.\d+[^ ]*) —/);
  if (!headline) fail('README.md: no "**vX.Y.Z —" status headline found under ## Status');
  else if (headline[1] !== config.version) {
    if (fix) {
      fs.writeFileSync(readmePath, readme.replace(/\*\*v\d+\.\d+\.\d+[^ ]* —/, `**v${config.version} —`));
      console.log(`  fixed README.md status headline -> v${config.version}`);
    } else fail(`README.md status headline v${headline[1]} != VersionPrefix ${config.version}`);
  }

  // ARCHITECTURE.md's status line, synced the same way and for the same reason (owner, 2026-08-02):
  // the release workflow owns the version, so ANY hand-written one goes stale the moment a release
  // cuts — and this file had "0.3.0 PUBLISHED (2026-08-01)" hand-typed into a heading. Docs date
  // their claims; the ONE line that needs a version gets it from the same source as everything else.
  const architecturePath = path.join(repo, 'docs', 'ARCHITECTURE.md');
  const architecture = fs.readFileSync(architecturePath, 'utf8');
  const state = architecture.match(/## Current state — \*\*v(\d+\.\d+\.\d+[^ *]*) published\*\*/);
  if (!state) fail('docs/ARCHITECTURE.md: no "## Current state — **vX.Y.Z published**" line found');
  else if (state[1] !== config.version) {
    if (fix) {
      fs.writeFileSync(architecturePath, architecture.replace(
        /(## Current state — \*\*v)\d+\.\d+\.\d+[^ *]*( published\*\*)/, `$1${config.version}$2`));
      console.log(`  fixed docs/ARCHITECTURE.md status line -> v${config.version}`);
    } else fail(`docs/ARCHITECTURE.md status line v${state[1]} != VersionPrefix ${config.version}`);
  }

  // 🔴 A `PackageReference` shown in a DOC is a copy of the version too, and the least forgiving one:
  // it is the first thing a new adopter pastes. `docs/getting-started.md` sat at 0.10.0 through the
  // 0.11.0 release — step 1 of the guide installed the PREVIOUS release — because the sync list knew
  // about headlines and package.json and not about snippets. Generalised rather than pinned to that
  // file: any tracked doc that shows a Shenora PackageReference is checked, so the NEXT doc to carry
  // one is covered the day it is written.
  for (const rel of trackedDocsWithSnippets()) {
    const docPath = path.join(repo, rel);
    const text = fs.readFileSync(docPath, 'utf8');
    const RE = /(<PackageReference\s+Include="Shenora[^"]*"\s+Version=")(\d+\.\d+\.\d+[^"]*)(")/g;
    const stale = [...text.matchAll(RE)].filter((m) => m[2] !== config.version);
    if (stale.length === 0) continue;
    if (fix) {
      fs.writeFileSync(docPath, text.replace(RE, `$1${config.version}$3`));
      console.log(`  fixed ${rel} PackageReference version(s) -> ${config.version}`);
    } else {
      fail(`${rel} shows PackageReference Version="${stale[0][2]}" != VersionPrefix ${config.version}`);
    }
  }

  // The npm tarball must SHIP the license text, not just declare MIT in the manifest (P5.5 H6). The
  // package needs its own copy because npm packs only files under the package directory — so the root
  // LICENSE is the source and this is checked against it, rather than trusting two files to stay equal.
  const rootLicense = path.join(repo, 'LICENSE');
  if (fs.existsSync(rootLicense)) {
    const expected = fs.readFileSync(rootLicense, 'utf8');
    for (const dir of config.npmPackages) {
      const npmLicense = path.join(repo, ...dir.split('/'), 'LICENSE');
      const actual = fs.existsSync(npmLicense) ? fs.readFileSync(npmLicense, 'utf8') : null;
      if (actual === expected) continue;
      if (fix) {
        // readFileSync/writeFileSync, never fs.cpSync — it hard-crashes Node 24 on this machine
        // (see .claude/rules/windows-dev-gotchas.md).
        fs.writeFileSync(npmLicense, expected);
        console.log(`  fixed ${dir}/LICENSE (copied from the root LICENSE)`);
      } else fail(`${dir}/LICENSE ${actual === null ? 'is missing' : 'differs from'} the root LICENSE`);
    }
  }

  // Test-support code must never reach the published tarball (P5.5 H7). `src/testing/` holds the
  // shared FakeTransport; tsconfig.build.json's `exclude` is the only thing keeping it out of dist/,
  // that exclusion is easy to drop while editing an unrelated pattern, and package.json ships
  // `files: ["dist"]` wholesale — so the leak would be silent.
  //
  // Checked against the SOURCE, not against dist/. Inspecting dist/ alone would fail OPEN in the one
  // path that matters: `pack` calls doctor FIRST and only then runs `npm run build`, whose `clean`
  // step deletes dist/ and rebuilds it — so the artifact doctor looked at is never the artifact that
  // ships, and on a fresh clone there is no dist/ to look at at all. (Same shape as the H5 finding
  // where `verify` scanned pre-sync files because `pack` had already run `doctor --fix`.) The
  // dist/ check below is kept as belt-and-braces for when a build DOES precede doctor.
  const testingSrc = path.join(npmDirAbs, 'src', 'testing');
  if (fs.existsSync(testingSrc)) {
    const buildTsconfig = path.join(npmDirAbs, 'tsconfig.build.json');
    const tsconfigText = fs.existsSync(buildTsconfig) ? fs.readFileSync(buildTsconfig, 'utf8') : '';
    // Match inside the "exclude" ARRAY, not the whole file. Scoping matters and was found the hard
    // way: a plain `text.includes('src/testing')` passed because the COMMENT above that array also
    // names the path — the guard was satisfied by the prose explaining it. The file is JSONC
    // (comments), so it cannot be JSON.parse'd; the array's contents are what the compiler reads.
    const excluded = /"exclude"\s*:\s*\[([^\]]*)\]/.exec(tsconfigText)?.[1] ?? '';
    if (!excluded.includes('src/testing'))
      fail(`${config.npmDir}/src/testing/ exists but tsconfig.build.json's "exclude" does not list it — `
        + `test-support code would compile into dist/ and be PUBLISHED. Add "src/testing/**" to "exclude".`);
  }
  const distDir = path.join(npmDirAbs, 'dist');
  if (fs.existsSync(path.join(distDir, 'testing')))
    fail(`${config.npmDir}/dist/testing/ exists — test-support code is staged for PUBLISH; `
      + `restore the "src/testing/**" entry in tsconfig.build.json "exclude" and rebuild`);

  // VERSION AUTHORSHIP — VersionPrefix must equal the newest RELEASE TAG.
  //
  // The four checks above prove the version is CONSISTENT across props/npm/README/LICENSE. That is a
  // different property from being CORRECT, and the gap cost a whole version number on 2026-08-01: a
  // session hand-bumped VersionPrefix 0.1.2 -> 0.2.0, which kept all four files perfectly consistent
  // — so doctor stayed green — while silently moving the baseline the release workflow bumps FROM.
  // The next run bumped 0.2.0 -> 0.3.0 and published that; 0.2.0 went from unreleased to skipped
  // without anyone choosing to skip it, and the registries read 0.1.2 -> 0.3.0.
  //
  // The invariant, confirmed to hold in the sibling template repo too: between releases VersionPrefix
  // sits at the LAST RELEASED version, because the workflow bumps it as part of releasing. So
  // VersionPrefix != newest tag means someone edited it by hand, whatever their reason.
  //
  // A state check, deliberately, rather than only the pre-commit diff guard: this catches the drift
  // however it arrived — a hand-edit, a bad merge, a rebase that resurrected an old props file.
  //
  // EXCEPT DURING A RELEASE, which is the one time the mismatch is CORRECT and expected. The workflow
  // bumps VersionPrefix in step 1 and creates the tag in step 6, so between those the props are
  // legitimately ahead of the newest tag — and this guard failed the 0.4.0 run inside `doctor --fix`,
  // before anything was published (2026-08-02). `SHENORA_RELEASE=1` is the same signal the pre-commit
  // version guard already honours, set job-wide by release.yml; it says "the bump you are looking at
  // is the pipeline's own".
  //
  // Why this was not caught when the guard was written: it was sabotage-verified for the hand-bump it
  // exists to stop, but no release had run since, so the one path where the invariant is meant NOT to
  // hold was never exercised. A guard needs testing on the paths it should stay quiet on, too.
  const releasing = process.env.SHENORA_RELEASE === '1';
  if (releasing) {
    console.log('  ..  version/tag match check skipped (SHENORA_RELEASE=1 — the pipeline owns this bump)');
  }
  if (problems === 0 && !releasing) {
    const tags = spawnSync('git', ['tag', '--list', 'v*'], { encoding: 'utf8', cwd: repo });
    const versions = (tags.stdout ?? '')
      .split(/\r?\n/)
      .map((t) => t.trim().replace(/^v/, ''))
      .filter((t) => /^\d+\.\d+\.\d+$/.test(t))
      .sort((a, b) => {
        const [aM, aN, aP] = a.split('.').map(Number);
        const [bM, bN, bP] = b.split('.').map(Number);
        return aM - bM || aN - bN || aP - bP;
      });
    const newest = versions.at(-1);
    // No tags at all = a fresh clone with no fetched tags, or a repo before its first release.
    // Silence is right here: failing would break `verify` on a shallow CI checkout.
    if (newest && newest !== config.version) {
      fail(`<VersionPrefix> is ${config.version} but the newest release tag is v${newest}. `
        + `Between releases these MUST match — the release workflow owns the bump, and an empty `
        + `\`version\` input bumps from whatever VersionPrefix says. A hand-edit here moves that `
        + `baseline and SKIPS a version (0.2.0 was lost exactly this way). Restore ${newest}, and cut `
        + `the release from the Actions tab; pass an explicit \`version\` if you want a specific number. `
        + `See docs/RELEASING.md.`);
    }
  }

  // THE TWO HALVES OF "SHIPPED" MUST AGREE. `packableProjects` here is what `pack` iterates; the API
  // surface gate uses `<IsPackable>true</IsPackable>` in the csproj as its definition of shipped
  // (MetadataSurfaceTests.Every_packable_project_has_a_baseline_of_one_kind_or_the_other). Both are
  // hand-maintained and NOTHING compared them — project.config.mjs's own comment already says a project
  // "claiming it while the tooling skips it is the two halves disagreeing", which is an invariant stated
  // in prose and enforced nowhere.
  //
  // The failure is silent in the direction that matters most: a new package with IsPackable=true and no
  // entry here has its SURFACE gated correctly and then simply never gets packed, so the release ships
  // without it and every gate is green. Found by asking the general question the empty-baseline incident
  // raised — which gates are satisfied by the presence of a thing rather than its content — and this one
  // was satisfied by nothing at all.
  const srcDir = path.join(repo, 'src');
  if (fs.existsSync(srcDir)) {
    const declared = fs.readdirSync(srcDir, { withFileTypes: true })
      .filter(e => e.isDirectory())
      .map(e => ({ name: e.name, csproj: path.join(srcDir, e.name, `${e.name}.csproj`) }))
      .filter(p => fs.existsSync(p.csproj))
      .filter(p => fs.readFileSync(p.csproj, 'utf8').includes('<IsPackable>true</IsPackable>'))
      .map(p => `src/${p.name}`);
    const listed = new Set(config.packableProjects);
    const declaredSet = new Set(declared);

    for (const p of declared.filter(p => !listed.has(p))) {
      fail(`${p} declares <IsPackable>true</IsPackable> but is NOT in project.config.mjs `
        + `packableProjects, so \`dev.mjs pack\` never builds a nupkg for it — a release would silently `
        + `ship without it while every other gate stayed green. Add it to the list.`);
    }
    for (const p of config.packableProjects.filter(p => !declaredSet.has(p))) {
      fail(`project.config.mjs lists ${p} in packableProjects, but its csproj does not declare `
        + `<IsPackable>true</IsPackable> (or does not exist), so packing it produces nothing. Remove it `
        + `from the list, or make the project packable.`);
    }
  }

  // STRAY TRACKED FILES. Earned by a real one: a 0-byte file whose name was two Private-Use-Area
  // characters then "This" — a mangled shell redirect — was committed, reached the public repo and rode
  // in the 0.6.0 tree. Harmless (no csproj referenced it, so it never entered a package) but junk in a
  // public repo, and NOTHING was looking: not doctor, not the sensitive scan, not CI. Deleting the one
  // file would have left the next one just as invisible.
  //
  // The rule is deliberately narrow — printable ASCII in tracked PATHS — because that is what this repo
  // actually uses and a narrow check cannot cry wolf. It is not a ban on non-ASCII content: source files
  // here are UTF-8 with CJK in comments and strings. A legitimate non-ASCII FILENAME would be a real
  // decision, so failing and making someone widen this deliberately is the right cost.
  const tracked = spawnSync('git', ['ls-files', '-z'], { cwd: repo, encoding: 'utf8' });
  if (tracked.status === 0) {
    // eslint-disable-next-line no-control-regex
    const odd = tracked.stdout.split('\0').filter(Boolean).filter(p => /[^\x20-\x7e]/.test(p));
    for (const p of odd) {
      // Escaped, or the message itself carries the unprintable characters into whatever reads the log.
      const escaped = [...p].map(c => (/[\x20-\x7e]/.test(c) ? c
        : `\\u${c.codePointAt(0).toString(16).padStart(4, '0')}`)).join('');
      fail(`stray tracked file with a non-printable-ASCII name: "${escaped}" — almost certainly not `
        + `intended (the known case was a mangled shell redirect). Remove it with \`git rm\`, or widen `
        + `this check in devtools/dev.mjs if the name is genuinely wanted.`);
    }
  } else {
    // Not a git checkout (a source archive, say). Say so rather than reporting a clean sweep — the
    // convention this file already applies to the skipped tag check.
    console.log('  ..  stray-file sweep skipped (not a git checkout)');
  }

  // GIT HOOKS — WHERE THEY ACTUALLY LIVE, because looking in the obvious place gives the wrong answer.
  //
  // 🔴 `.git/hooks/` IS NOT WHERE THIS REPO'S HOOKS ARE. `install-hooks` sets `core.hooksPath` to the
  // TRACKED `devtools/hooks/`, so `.git/hooks/` keeps only git's `.sample` files forever — and that
  // reads as "the sensitive guard is not installed" to anyone who checks. TWO separate agents concluded
  // exactly that in ONE session (2026-08-12/13), and one of them re-ran `install-hooks` to fix a thing that
  // was never broken. The next reader will look in the same wrong directory, so the answer belongs one
  // command away rather than in a rule nobody re-reads.
  //
  // ⚠ REPORTED, NEVER FAILED, and that is about WHERE THIS RUNS. `doctor` runs inside `verify`, which
  // runs in CI — where hooks are meaningless (nothing commits) and `core.hooksPath` is never set. A FAIL
  // would break every CI run to state a fact that is only actionable on a developer's clone. The real
  // protection is the hook itself; this is the line that tells you whether you have it.
  //
  // ⚠ "IS THIS A CHECKOUT?" IS THE `git ls-files` RESULT ABOVE, NOT THIS COMMAND'S EXIT CODE. `git config
  // --get` answers from the GLOBAL config outside a repo and exits 1 for "unset" there exactly as it does
  // inside one — so a status check here cannot tell the two apart, and a source archive would be told to
  // run `install-hooks` on a repo it does not have. Reusing the sweep's signal keeps the two lines saying
  // the same thing about the same tree.
  const hooksPath = spawnSync('git', ['config', '--get', 'core.hooksPath'], { cwd: repo, encoding: 'utf8' });
  if (tracked.status !== 0 || hooksPath.error) {
    console.log('  ..  git-hooks check skipped (not a git checkout)');
  } else {
    const configured = (hooksPath.stdout ?? '').trim();
    // The two the repo ships: staged content+paths on commit, the MESSAGE (and history) on commit-msg.
    const present = ['pre-commit', 'commit-msg']
      .filter((h) => configured && fs.existsSync(path.join(repo, ...configured.split('/'), h)));
    if (!configured) {
      console.log('  !!  git hooks: core.hooksPath is NOT set, so the sensitive-info guard does NOT run on '
        + 'commit (a fresh clone starts this way, and .git/hooks/ holds only git\'s .sample files either '
        + 'way). Install once: node devtools/dev.mjs install-hooks');
    } else if (present.length === 2) {
      console.log(`  ok  git hooks: core.hooksPath = ${configured} (pre-commit + commit-msg present) — `
        + 'NOT .git/hooks/, which keeps only git\'s .sample files in this repo');
    } else {
      console.log(`  !!  git hooks: core.hooksPath = ${configured}, but ${['pre-commit', 'commit-msg']
        .filter((h) => !present.includes(h)).join(' + ')} is missing there — the guard cannot run. `
        + 'Re-run: node devtools/dev.mjs install-hooks');
    }
  }

  // 🔴 A SCANNER THAT ROOTS AT `process.cwd()` SILENTLY UNDER-SCANS FROM ANYWHERE ELSE, and this is a
  // gate rather than a rule because the rule version already failed: three scanners were fixed in one
  // commit and the fourth was missed the same day, because the population was "the files I happened to
  // open". Run from `devtools/`, `cite-scan` discovered ONE doc instead of thirty-six and reported the
  // rest of the repo's prose as clean — a partial scan is indistinguishable from a clean one when the
  // clean answer is silence. Deriving the set from a query is the whole fix, so the query lives here.
  for (const entry of fs.readdirSync(path.join(repo, 'devtools', 'scripts'))) {
    if (!entry.endsWith('.mjs')) continue;
    const src = fs.readFileSync(path.join(repo, 'devtools', 'scripts', entry), 'utf8');
    if (!/^\s*const\s+repo\s*=/m.test(src)) continue;          // does not resolve a repo root at all
    if (/const\s+repo\s*=\s*process\.cwd\(\)/.test(src)) {
      fail(`devtools/scripts/${entry} roots at process.cwd(), so it silently scans less (or nothing) `
        + 'when run from any other directory. Use the script\'s own location: '
        + 'path.resolve(path.dirname(fileURLToPath(import.meta.url)), \'..\', \'..\').');
    }
  }

  // 🔴 CLAUDE.md IS THE AGENT'S STARTING PROMPT, NOT A README — it orients a session, and where the
  // project has got to is README.md's job. A version there is auto-loaded into every session as a fact
  // nothing syncs: the consistency check above owns props/npm/README/ARCHITECTURE/LICENSE, and this file
  // is not one of them. Measured: its status line sat a whole release behind and no gate could see it.
  // A rule already said not to; the rule is what failed, so this is the mechanism.
  // ⚠ Deliberately ANY x.y.z, not just the current version: the failure is naming a version at all, and
  // matching only the current one would go quiet the moment it drifted — passing precisely when wrong.
  const alwaysLoaded = path.join(repo, 'CLAUDE.md');
  if (fs.existsSync(alwaysLoaded)) {
    for (const [i, line] of fs.readFileSync(alwaysLoaded, 'utf8').split(/\r?\n/).entries()) {
      const named = line.match(/v?\d+\.\d+\.\d+/);
      if (named) {
        fail(`CLAUDE.md:${i + 1} names a version (${named[0]}). That file is the AGENT'S STARTING PROMPT, `
          + 'not a README: where the project has got to belongs in README.md, and the number itself in '
          + 'src/Directory.Build.props. Nothing syncs it here, so every session is auto-loaded a fact that '
          + 'goes stale the day the next release ships.');
      }
    }
  }

  if (problems === 0)
    // Don't claim the tag matched when the check was skipped — a success line that overstates what
    // ran is the same defect class as a doc that overstates what the code does.
    console.log(`  ok  version ${config.version} consistent (props · npm · README · ARCHITECTURE · LICENSE)`
      + (releasing ? ' — tag check skipped, this is the release' : ' and matches the newest tag')
      + `; ${config.packableProjects.length} packable project(s) agree with their csprojs`
      + '; no stray tracked filenames; every scanner roots at its own location'
      + '; CLAUDE.md names no version');
  return problems === 0;
}

// ONE owner for "where a native devtool's exe lives and whether it needs building" — win-input,
// wgc-shot and ui-responsiveness each had (or would have had) their own copy of this, which is
// exactly the kind of drift the `shotTarget` comment above already warns about. Built on demand
// into their gitignored bin/ (never shipped in the app build).
const TOOL_TFM = {
  'win-input': 'net10.0-windows',
  'wgc-shot': 'net10.0-windows10.0.22621.0',
  'ui-responsiveness': 'net10.0-windows',
  // net10.0, not -windows: Shenora.IO is portable and a probe that could only run on Windows could
  // not check the Linux half of a claim this kit intends to keep making.
  'update-probe': 'net10.0',
};
// CMake is not on PATH on a stock Visual Studio box — VS bundles its own and never adds it. Looking
// there before giving up is the difference between "works" and "install CMake first" for anyone whose
// C++ toolchain came with VS, which on this family's machines is everyone.
function resolveCmake() {
  const onPath = spawnSync('cmake', ['--version'], { stdio: 'ignore', shell: false });
  if (onPath.status === 0) return 'cmake';
  const roots = [process.env['ProgramFiles'], process.env['ProgramFiles(x86)']].filter(Boolean);
  for (const root of roots) {
    const vs = path.join(root, 'Microsoft Visual Studio');
    if (!fs.existsSync(vs)) continue;
    for (const year of fs.readdirSync(vs)) {
      for (const edition of ['Community', 'Professional', 'Enterprise', 'BuildTools']) {
        const candidate = path.join(vs, year, edition,
          'Common7', 'IDE', 'CommonExtensions', 'Microsoft', 'CMake', 'CMake', 'bin', 'cmake.exe');
        if (fs.existsSync(candidate)) return candidate;
      }
    }
  }
  return null;
}

// Build the launcher's POSIX half with gcc, in a container, so a Windows box can prove the platform it
// cannot compile. This exists because the alternative was learning it from a failed release: MSVC drags
// most of the standard library in through other headers and gcc does not, so `platform_posix.cpp` built
// clean here for days while missing two includes it used.
//
// It runs the REAL CMakeLists rather than a hand-rolled g++ line, because a check with its own copy of
// the compiler flags is a check that drifts away from the thing it is checking. Cost: the image pull
// once, then ~20s per run for cmake (gcc:13 has no cmake, and --rm means it is installed each time).
// The conformance harness is NOT run here — it drives the binary through the C# update-probe, and there
// is no .NET in the container. Cross-compilation checks the code; conformance is the release's job.
function runPosixLauncherBuild() {
  const docker = spawnSync('docker', ['version', '--format', '{{.Server.Os}}'], { encoding: 'utf8', shell: false });
  if (docker.status !== 0) {
    console.error('docker not found (or the daemon is not running). `launcher --posix` cross-builds the\n'
      + 'POSIX half in a gcc container so a Windows box can prove it; without Docker the only thing that\n'
      + 'compiles that half is the release workflow, which is a slow place to find a missing #include.');
    process.exitCode = 1;
    return;
  }
  // Forward slashes: Docker Desktop parses `-v` on the colon, and a bare `D:\...` host path is ambiguous.
  const mount = `${repo.replace(/\\/g, '/')}:/src`;
  const script = [
    'set -e',
    'command -v cmake >/dev/null 2>&1 || { apt-get update -qq && apt-get install -y -qq cmake >/dev/null; }',
    // Build OUT of the mount: build artefacts written back through the bind mount would land in the
    // working tree, and CMake caches absolute container paths that mean nothing on the host.
    'cmake -S src/Shenora.Launcher -B /tmp/launcher-build -DCMAKE_BUILD_TYPE=Release >/dev/null',
    'cmake --build /tmp/launcher-build',
    // Prove it RUNS, not just links: with no stage pending it must report nothing applied and exit 0.
    'cd /tmp && /tmp/launcher-build/shenora-launcher --apply-and-exit',
    'stat -c "%n %s" /tmp/launcher-build/shenora-launcher',
  ].join('\n');

  const ok = step('gcc:13 cross-build (POSIX half)', () => run('docker',
    ['run', '--rm', '-v', mount, '-w', '/src', 'gcc:13', 'bash', '-c', script]));
  if (!ok) { process.exitCode = 1; return; }
  console.log('\nlauncher --posix: the POSIX half compiles, links and runs under gcc.\n'
    + 'Conformance is Windows-only locally — run `dev.mjs launcher` for that.');
}

function ensureTool(toolName) {
  const exe = path.join(repo, 'devtools', toolName, 'bin', 'Release', TOOL_TFM[toolName], `${toolName}.exe`);
  // ALWAYS an (incremental) build, never gated on the exe existing: a tool whose source drifted kept
  // running as its stale binary — the launcher conformance passed locally over an update-probe that
  // no longer COMPILED, and the release workflow's fresh build was the first thing to say so.
  // Incremental is seconds when nothing changed, which is cheap next to a verdict from a corpse.
  const b = spawnSync('dotnet', ['build', path.join(repo, 'devtools', toolName, `${toolName}.csproj`),
    '-c', 'Release', '-v', 'quiet'], { stdio: 'inherit', cwd: repo });
  if (b.status !== 0) return null;
  return fs.existsSync(exe) ? exe : null;
}

switch (cmd) {
  case 'build': {
    // No -clp:ErrorsOnly: warnings must be VISIBLE (they are errors under TreatWarningsAsErrors,
    // but a suppressed-warning build is how invisible problems accumulated — P5.5 H5).
    const buildEnv = androidBuildEnv();
    // Asked BEFORE the build, so a missing prerequisite is named in one line instead of arriving as
    // NETSDK1147 after a restore — see `missingWorkloads`.
    const absent = buildEnv === null ? [] : missingWorkloads();
    if (absent.length > 0) {
      console.error(
        `\n  This machine is missing the .NET workload(s) the solution needs: ${absent.join(', ')}.\n` +
        `  Install with:  dotnet workload install ${absent.join(' ')}\n` +
        '  Until then `dotnet build` of the solution stops on NETSDK1147 and NOTHING here has run —\n' +
        '  a RED gate that says nothing about the working tree. This is a machine prerequisite, not a\n' +
        '  repo setting; see devtools/README.md.');
    }
    const ok = buildEnv !== null && absent.length === 0
      && step('dotnet build', () => run('dotnet', ['build', config.solution, '-v', 'minimal'], { env: buildEnv }))
      // The update-probe is OUTSIDE the solution but INSIDE the release path — the launcher job
      // compiles it fresh on every release, so the gate must too. It drifted against the ILogger
      // standardisation and the first compiler to see it was CI's, mid-release.
      && step('dotnet build (update-probe)', () => run('dotnet',
        ['build', path.join('devtools', 'update-probe', 'update-probe.csproj'), '-c', 'Release', '-v', 'minimal']))
      // EVERY npm package, from the config — see `pack` for the release this shape cost.
      && config.npmPackages.every((dir) => {
        const abs = path.join(repo, ...dir.split('/'));
        return ensureNpmDeps(abs) && step(`npm build (${dir.split('/').pop()})`, () => runNpm('run build', { cwd: abs }));
      });
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'test': {
    const which = args[0] ?? 'all';
    // Fail loudly on a typo: this used to fall through both ifs and exit 0 having run NOTHING,
    // i.e. `dev.mjs test dotnett` reported success (P5.5 H5).
    if (!['all', 'dotnet', 'npm', 'clipboard'].includes(which)) {
      console.error(`dev.mjs test: unknown target "${which}" — expected all | dotnet | npm | clipboard`);
      process.exitCode = 1;
      break;
    }
    // 🔴 THE ONLY SUITE HELD OUT OF THE GATE, and it is deliberate rather than a concession to
    // flakiness. `Category=RealClipboard` drives the machine's ONE system clipboard, which any other
    // process can take at any moment — measured 2026-08-16, PowerShell's own Set-Clipboard failed 13 of
    // 15 on this box while nothing of ours was running. A gate that can go red because a background
    // service is misbehaving is a gate people learn to ignore. Run it deliberately, on demand.
    if (which === 'clipboard') {
      const clipEnv = androidBuildEnv();
      process.exitCode = clipEnv !== null
        && step('dotnet test (real clipboard)', () => run('dotnet',
          ['test', config.solution, '-v', 'minimal', '--nologo', '--filter', 'Category=RealClipboard'],
          { env: clipEnv })) ? 0 : 1;
      break;
    }
    let ok = true;
    if (which === 'all' || which === 'dotnet') {
      // The SAME env `build` needs, for the same reason: `dotnet test <solution>` BUILDS the solution,
      // and that includes the Android TFM of Shenora.Mobile, which cannot compile without a JDK. This
      // was missing and latent — it only surfaced when Shenora.Mobile started multi-targeting and the
      // outer build stopped being a no-op, so `dev.mjs test` on a clean tree failed XA5300 while
      // `dev.mjs build` right before it had succeeded. Anything that builds the solution needs this.
      const testEnv = androidBuildEnv();
      // 🔴 SAY WHAT WAS HELD OUT, every run. A gate that quietly covers less than it appears to is worse
      // than one that covers less openly — "1,642 passed" reads as everything unless this line is there.
      console.log('  (holding out Category=RealClipboard — run `dev.mjs test clipboard` for it)');
      ok = testEnv !== null
        // The RealClipboard category is excluded here and run by `dev.mjs test clipboard` — see the
        // block above for why a shared OS resource has no business gating a release.
        && step('dotnet test', () => run('dotnet',
          ['test', config.solution, '-v', 'minimal', '--nologo', '--filter', 'Category!=RealClipboard'],
          { env: testEnv }))
        && ok;
    }
    if (which === 'all' || which === 'npm') {
      ok = (ensureNpmDeps(npmDirAbs) && step('vitest (react package)', () => runNpm('test', { cwd: npmDirAbs }))) && ok;
      // BOTH npm packages have suites now. `@shenora/cli`'s covers the decisions that fail SILENTLY —
      // pipefail (a rejected install reported as success) and the `--` split (a build property read as a
      // simulator name). Both sabotage-verified in each direction on 2026-08-09.
      ok = (ensureNpmDeps(cliDirAbs) && step('vitest (cli package)', () => runNpm('test', { cwd: cliDirAbs }))) && ok;
    }
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'verify': {
    // The "am I done?" gate — run everything and stop at the first failure.
    //
    // `--release` runs the SUBSET THAT PROTECTS THE ARTIFACT, and release.yml uses it. The split
    // exists because a release gate answers a narrower question than a dev gate: could this harm a
    // CONSUMER? Build, tests, typechecks (the shipped code), the sensitive scan (publishing a leak is
    // irreversible), doc-drift (the README it checks ships inside every nupkg) and doctor (version
    // consistency is exactly what a release must not get wrong) all qualify. The rule-base checks do
    // not: `knowledge check`/`footprint` police THIS repo's assistant rules, ship nothing, and can
    // harm nobody downstream — and on 2026-08-02 the footprint check blocked a release twice while
    // the packages themselves were perfect.
    //
    // Keeping them in the local gate is right; letting them stop a publish was not.
    const releaseOnly = args.includes('--release');
    const devOnly = new Set(['knowledge check', 'knowledge footprint']);
    const steps = [
      ['build', () => spawnSync('node', [import.meta.filename, 'build'], { stdio: 'inherit', cwd: repo }).status === 0],
      ['test', () => spawnSync('node', [import.meta.filename, 'test'], { stdio: 'inherit', cwd: repo }).status === 0],
      ['react typecheck (incl. tests)', () => {
        // `build` uses tsconfig.build.json, which EXCLUDES the tests, and vitest transpiles without
        // type-checking — so nothing checked the test files at all, and the tsconfig that was written to
        // do it had never been run (it was red on a lib version). That matters beyond tidiness: the
        // typed-service generic is pinned by `@ts-expect-error` assertions, which are inert unless
        // something type-checks them (P5.5 H6).
        return runNpm('run typecheck', { cwd: path.join(repo, ...config.npmDir.split('/')) });
      }],
      ['react typecheck (peer FLOOR — React 18)', () => {
        // 🔴 `peerDependencies: { react: ">=18" }` was a claim nothing had ever run. Everything here
        // builds against React 19, so an API that only exists in 19 would compile clean and break every
        // React 18 consumer at THEIR build. This type-checks the shipped sources against React 18's
        // types (an aliased `@types/react18` devDependency), and is sabotage-verified: importing
        // `useActionState` fails it and passes the ordinary typecheck.
        return runNpm('run typecheck:floor', { cwd: path.join(repo, ...config.npmDir.split('/')) });
      }],
      // The CLI's own strict pass, and it now DOES cover tests (added 2026-08-09) — `tsconfig.json`
      // includes them while `tsconfig.build.json` excludes them, so this is the only thing type-checking
      // the suite. Kept as its own step for exactly the reason it earned: the React package's equivalent
      // was inert for five phases because nothing ran it.
      ['cli typecheck', () => ensureNpmDeps(cliDirAbs) && runNpm('run typecheck', { cwd: cliDirAbs })],
      ['sample web typecheck', () => {
        // The e2e subject's TS was never type-checked by any gate (P5.5 H5). Skipped only when the
        // sample web app doesn't exist yet.
        const webDir = path.join(repo, ...config.sampleWebDir.split('/'));
        if (!fs.existsSync(webDir)) return true;
        return ensureNpmDeps(webDir) && runNpm('run typecheck', { cwd: webDir });
      }],
      ['check-sensitive --tree', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'check-sensitive.mjs'), '--tree'], { stdio: 'inherit', cwd: repo }).status === 0],
      // A path Windows cannot check out. Separate from check-sensitive on purpose: that one hunts LEAKS
      // and its failure text ("move the value to local/") makes no sense for a filename. This is created
      // by accident, never by a decision — a `> nul` redirect written in Git Bash creates a real file,
      // because that spelling is cmd's null device and not the shell's. It reaches `git status` as an
      // ordinary untracked file and `git add -A` stages it; committed, it breaks `git checkout` for
      // every future clone on Windows.
      ['reserved-paths', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'reserved-paths.mjs')], { stdio: 'inherit', cwd: repo }).status === 0],
      ['knowledge check', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'knowledge.mjs'), 'check'], { stdio: 'inherit', cwd: repo }).status === 0],
      // The always-loaded budget — REPORTED here, not enforced. It existed from the start and
      // nothing ran it, so it drifted to its ceiling unnoticed; running it in the gate is what fixes
      // that, because the number lands in every verify log. It was briefly FATAL and that was wrong:
      // it blocked a release by 0.2 KB on 2026-08-02, and a style budget must not outrank shipping.
      // The script exits 0 and prints ⚠ OVER when it is over (see its own comment).
      ['knowledge footprint', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'knowledge.mjs'), 'footprint'], { stdio: 'inherit', cwd: repo }).status === 0],
      // The gate the PROSE never had (0.2.0 design pass, D4). Every code invariant here has a test;
      // no doc claim had anything, and a whole-codebase review found 8 of its ~13 findings in
      // comments and docs — including a dependency graph both READMEs drew with an edge that has
      // never existed. Two precise checks only (graph vs csproj, retired names stated as current),
      // because a fuzzy "does this symbol exist" sweep would drown the signal and get switched off.
      ['doc-drift', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'doc-drift.mjs')], { stdio: 'inherit', cwd: repo }).status === 0],
      // doc-shape is doc-drift's other half: doc-drift asks whether a claim matches the tree, this asks
      // whether a doc is narrating its own past — the habit that BLINDS doc-drift, because its history
      // suppression stays permanently on inside an amendment stack. Only the narration rows gate; the
      // D-entry line cap is a style budget and warns, the same call the knowledge footprint above
      // already makes and for the same reason (a fatal budget blocked a release by 0.2 KB).
      ['doc-shape', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'doc-shape.mjs'), '--check'], { stdio: 'inherit', cwd: repo }).status === 0],
      // The generated wire reference must match the source constants. It is the one page in `docs/`
      // that restates something the code owns, which D57 says goes stale — so it is only defensible
      // while this gate makes that impossible.
      ['wire-reference', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'wire-reference.mjs'), '--check'], { stdio: 'inherit', cwd: repo }).status === 0],
      // The decisions index is generated from the entries, so it can only be stale — never wrong in an
      // interesting way. Checked here for the reason wire-reference is: a generated doc nobody
      // regenerates is a second copy that drifts silently.
      ['decisions-index', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'decisions-index.mjs'), '--check'], { stdio: 'inherit', cwd: repo }).status === 0],
      // doctor LAST and non-fixing: verify must FAIL on version/README drift rather than leave it to
      // `pack` (which runs doctor --fix, so verify was scanning pre-sync files) — P5.5 H5.
      ['doctor', () => doctor({ fix: false })],
    ];
    let ok = true;
    for (const [label, fn] of steps) {
      if (releaseOnly && devOnly.has(label)) {
        console.log(`\n=== verify: ${label} — SKIPPED (--release: repo hygiene, ships nothing) ===`);
        continue;
      }
      if (!step(`verify: ${label}`, fn)) { ok = false; break; }
    }
    console.log(ok ? `\nVERIFY PASSED${releaseOnly ? ' (release subset)' : ''}` : '\nVERIFY FAILED');
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'pack': {
    if (!doctor({ fix: true })) { process.exitCode = 1; break; }
    const out = path.join(repo, ...config.packagesDir.split('/'));
    fs.rmSync(out, { recursive: true, force: true });
    fs.mkdirSync(out, { recursive: true });

    // ONE host packs everything, including both mobile faces — a net10.0-ios LIBRARY needs no Mac,
    // only the maui-ios workload (2026-08-03). `macOnlyPackableProjects` is empty and `--mac` selects
    // it, so the flag is a no-op today and exists for the next project that genuinely needs Xcode.
    //
    // THE THIRD PLACE that needs the JDK env, after `build` and `test`. Packing Shenora.Android runs
    // the Android tooling, which fails XA5300 without JAVA_HOME — and this one was missed when the
    // other two were fixed, because pack used to SKIP the mobile package entirely. The rule earned
    // twice now: anything that compiles the Android TFM needs androidBuildEnv().
    const packEnv = androidBuildEnv();
    if (packEnv === null) { process.exitCode = 1; break; }

    const macOnly = new Set(config.macOnlyPackableProjects ?? []);
    const macPass = args.includes('--mac');

    // Projects that pack DOWNLOADED artifacts rather than anything built here. `Shenora.Launcher`
    // carries per-RID native binaries produced by the `launcher` CI matrix on two different runners, so
    // no single machine can pack it from source. Skipped when its artifacts are absent — which is every
    // ordinary dev-box run — and packed normally once a release workflow has staged them. It is NOT
    // silently skipped when present, and it FAILS LOUD rather than shipping an empty runtimes/ folder;
    // both halves of that matter, because an empty native package restores fine and breaks the consumer.
    // Skipped only when there is NOTHING to pack — neither a `runtimes/` on disk nor a downloaded
    // `artifacts/runtimes/`.
    // ⚠ This said "since win-x64 is committed, this no longer skips in practice" until 2026-08-14, and it
    // was FALSE the day it was written: the commit that wrote it (`b2f3b50`) is the one that REMOVED the
    // committed binary — its own subject line is "no binary in git" — and `git ls-files` has never
    // matched a `runtimes/` path since. So an ordinary dev-box `pack` DOES skip the launcher and emits
    // four nupkgs, not five. That matters because a reader checking release readiness against that
    // sentence reads four-of-five as complete, which is exactly how 0.5.0 shipped four of five
    // (`docs/REVIEW-GUIDE.md` §4.4). The RELEASE is unaffected: `release.yml`'s launcher matrix builds
    // both RIDs, stages them here, and asserts both are present before packing.
    const needsArtifacts = (config.artifactPackableProjects ?? []);
    const artifactless = needsArtifacts.filter((p) =>
      !fs.existsSync(path.join(repo, p, 'runtimes'))
      && !fs.existsSync(path.join(repo, p, 'artifacts', 'runtimes')));

    const selected = config.packableProjects
      .filter((p) => macOnly.has(p) === macPass)
      .filter((p) => !artifactless.includes(p));
    const skipped = config.packableProjects.filter((p) => !selected.includes(p));

    if (macPass && process.platform !== 'darwin') {
      console.error('dev.mjs pack --mac: needs macOS — these packages require Xcode to build.');
      process.exitCode = 1;
      break;
    }
    if (skipped.length) {
      console.log('  skipped:');
      for (const p of skipped) {
        const why = artifactless.includes(p)
          ? 'no CI artifacts staged — see the `launcher` workflow'
          : (macPass ? 'not part of --mac' : 'needs macOS — see macOnlyPackableProjects');
        console.log(`    ${p}  (${why})`);
      }
    }

    let ok = true;
    for (const proj of selected) {
      ok = step(`pack ${proj}`, () => run('dotnet', ['pack', proj, '-c', 'Release', '-o', out,
        `-p:Version=${config.version}`, '-v', 'minimal', '-clp:ErrorsOnly'], { env: packEnv })) && ok;
    }
    // The npm package belongs to the default pass — `--mac` produces NuGet only, so the two passes
    // cannot both emit a tarball and leave the publish step guessing which is current.
    if (!macPass) {
      // 🔴 EVERY npm package, from the config — never a step per package typed by hand. `@shenora/cli`
      // (D67) was added on 2026-08-08 and this packed only the React package for its first two days:
      // `doctor` held the version in lockstep the whole time, which is exactly what made the gap
      // invisible — every check said "consistent" about a tarball that was never produced. The fix
      // then was a SECOND hardcoded pair, which is the same bug waiting for a third package; this is
      // the loop that ends it. Both packages expose the same `build` script, so nothing is special-cased.
      for (const dir of config.npmPackages) {
        const abs = path.join(repo, ...dir.split('/'));
        const name = dir.split('/').pop();
        ok = ok && ensureNpmDeps(abs);
        ok = ok && step(`npm build (${name})`, () => runNpm('run build', { cwd: abs }));
        ok = ok && step(`npm pack (${name})`, () => runNpm(`pack --pack-destination "${out}"`, { cwd: abs }));
      }
    }
    if (ok) ok = step('build assets the targets NAME are inside the package', () => checkPackagedBuildAssets(out));
    if (ok) ok = step('evict stale Shenora.* from the NuGet global cache', () => evictGlobalCache());
    if (ok) {
      console.log('\npacked:');
      for (const f of fs.readdirSync(out).sort()) {
        const sha = createHash('sha256').update(fs.readFileSync(path.join(out, f))).digest('hex');
        console.log(`  ${f}  sha256=${sha}`);
      }
    }
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'doctor': {
    // Version/README consistency AND doc drift — both are "is what we SAY still true?" checks, so a
    // reader running `doctor` gets both rather than having to know a second command exists.
    // doc-drift never auto-fixes: every one of its findings is a sentence someone has to rewrite.
    const versionOk = doctor({ fix: args.includes('--fix') });
    const driftOk = spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'doc-drift.mjs')],
      { stdio: 'inherit', cwd: repo }).status === 0;
    const jdkOk = reportJdk();
    process.exitCode = versionOk && driftOk && jdkOk ? 0 : 1;
    break;
  }

  case 'changelog': {
    // `--version` so the release pipeline can stamp the version it is ABOUT to publish, before that
    // version has been written back into VersionPrefix; `--date` so a re-cut can reproduce a heading.
    const at = (flag) => { const i = args.indexOf(flag); return i >= 0 ? args[i + 1] : undefined; };
    process.exitCode = changelogDoctor({
      fix: args.includes('--fix'),
      version: at('--version') ?? config.version,
      date: at('--date'),
    }) ? 0 : 1;
    break;
  }

  // ---- Sample-app loop. The capture/input tools below already
  // work against any process named in project.config.mjs once the sample exists.
  case 'sample': {
    const projDir = path.join(repo, ...config.sampleProject.split('/'));
    if (!fs.existsSync(projDir)) { console.error(`sample project not created yet (${config.sampleProject})`); process.exitCode = 1; break; }
    const env = { ...process.env };
    if (args.includes('--dev')) {
      const cdpPort = config.cdpPortBase + Math.floor(Math.random() * 500);
      env.ASPNETCORE_ENVIRONMENT = 'Development';
      env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = `--remote-debugging-port=${cdpPort}`;
      fs.writeFileSync(path.join(repo, 'devtools', '.cdp-port'), String(cdpPort));
      console.log(`sample starting in DEV mode (vite @${config.vitePort}, CDP @${cdpPort})`);
    } else if (!args.includes('--no-build')) {
      // BUILD THE BUNDLE FIRST. Production mode serves the EMBEDDED wwwroot, and this command used
      // to be a bare `dotnet run` — so it happily ran whatever bundle happened to be on disk, with
      // no signal that it was stale. Found live 2026-08-02: a hands-on test of the drop zone showed
      // no hover feedback, and the cause was a wwwroot built three days BEFORE the `.drop-hover`
      // rule was added. The rule had been added (P5.5 H7) precisely because that feedback "is the
      // part an adopter most wants to see working" — and it never reached the thing anyone runs.
      // That is worse than a cosmetic miss: `phase-workflow.md` says desktop behaviour is proven
      // against the sample, so a stale bundle silently proves it against arbitrarily old frontend
      // code. `vite` has always done this chain; the production path had no equivalent.
      // `--no-build` skips it for a quick relaunch when only the C# side changed.
      const webDir = path.join(repo, ...config.sampleWebDir.split('/'));
      if (!fs.existsSync(webDir)) { console.error(`sample web not created yet (${config.sampleWebDir})`); process.exitCode = 1; break; }
      console.log('sample: building the packaged frontend (use --no-build to skip)…');
      if (!ensureNpmDeps(npmDirAbs) || !runNpm('run build', { cwd: npmDirAbs }) ||
          !ensureNpmDeps(webDir) || !runNpm('run build', { cwd: webDir })) { process.exitCode = 1; break; }
    }
    run('dotnet', ['run', '--project', config.sampleProject], { env });
    break;
  }

  case 'vite': {
    const webDir = path.join(repo, ...config.sampleWebDir.split('/'));
    if (!fs.existsSync(webDir)) { console.error(`sample web not created yet (${config.sampleWebDir})`); process.exitCode = 1; break; }
    // Install the SAMPLE's deps too (it was only ever done for the react package, so a fresh clone
    // got a bare "vite: not found" — P5.5 H5). Its @shenora/react dep is a file: link, so the
    // package must be built first or the sample resolves an empty dist.
    if (!ensureNpmDeps(npmDirAbs) || !runNpm('run build', { cwd: npmDirAbs }) || !ensureNpmDeps(webDir)) { process.exitCode = 1; break; }
    runNpm('run dev', { cwd: webDir });
    break;
  }

  case 'shot': {
    run('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'devtools', 'scripts', 'shot-window.ps1'),
      '-ProcessName', config.processName,
      '-OutFile', shotTarget(args[0]?.startsWith('--') ? undefined : args[0], args)]);
    break;
  }

  // Native desktop-verification tools (win-input drives the WebView2 UI via background PostMessage
  // clicks; wgc-shot is an occlusion-immune capture). Built on demand into their gitignored bin/.
  case 'wgc': case 'click': case 'rclick': case 'move': case 'drag': case 'input': {
    const toolName = cmd === 'wgc' ? 'wgc-shot' : 'win-input';
    const exe = ensureTool(toolName);
    if (!exe) { process.exitCode = 1; break; }
    const env = { ...process.env, DEVTOOL_PROC: config.processName };
    if (cmd === 'wgc') {
      run(exe, ['--out', shotTarget(args[0]?.startsWith('--') ? undefined : args[0], args)], { env });
    } else if (cmd === 'input') {
      run(exe, args, { env });
    } else {
      run(exe, [cmd, ...args], { env });
    }
    break;
  }

  // The UI-thread responsiveness probe behind D23 (docs/DECISIONS.md): clicks a
  // control via win-input, THEN samples SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG) sub-100ms
  // while the resulting work runs. Refuses to print sample stats unless the click actually landed
  // (win-input's own "click ok" confirmation) — the guard against the v0.1.0 vacuous pass where a
  // failed launch reported "0 stalls" for having measured nothing.
  //   node devtools/dev.mjs responsiveness <fx> <fy> [--label block|stream] [--duration|--interval|--timeout ms]
  case 'responsiveness': {
    // Check the args BEFORE paying for two `dotnet build` runs — a mistyped invocation used to build
    // both native tools first and only then print the usage error.
    if (args.length < 2 || args[0]?.startsWith('--')) {
      console.error('usage: node devtools/dev.mjs responsiveness <fx> <fy> [--label name] '
        + '[--duration ms] [--interval ms] [--timeout ms] [--title-contains text] '
        + '[--start-timeout ms] [--start-poll ms]');
      process.exitCode = 2;
      break;
    }
    const winInput = ensureTool('win-input');
    const probe = ensureTool('ui-responsiveness');
    if (!winInput || !probe) { process.exitCode = 1; break; }
    const env = { ...process.env, DEVTOOL_PROC: config.processName };
    run(probe, [args[0], args[1], '--win-input', winInput, '--proc', config.processName, ...args.slice(2)], { env });
    break;
  }

  // Hand the segment tier's OWN output to a foreign decoder. NOT part of `verify`, which must run on a
  // clone with no external tool — ffmpeg is not a build dependency and never becomes one.
  //
  // 🔴 THE TEST SUITE CANNOT ASK THIS QUESTION. RealSourceSegmentTests checks everything a fragment can be
  // checked for without decoding it — box structure, sample counts, where each fragment opens — and a
  // stream that satisfies every one of those can still be undecodable. This is the only step that puts a
  // decoder behind the assertion, which is why the suite writes its artifacts to a fixed path instead of
  // a temp one.
  //
  // ⚠ AND A CLEAN EXIT IS NOT THE ANSWER. ffmpeg exits 0 on a file it read no frames from, which is the
  // same trap Mp4FragmentReader's remarks describe for a picture-less segment: the answer has to be a
  // COUNT, not the absence of an error. Both are checked below.
  case 'media-decode': {
    const dir = path.join(repo, 'devtools', '_media-real');
    const merged = path.join(dir, 'merged.mp4');

    if (spawnSync('ffmpeg', ['-version'], { stdio: 'ignore', shell: false }).status !== 0) {
      console.error('ffmpeg not found on PATH — it is what decodes the artifacts. Install it, or skip this '
        + 'verb: nothing in `verify` depends on it.');
      process.exitCode = 1;
      break;
    }

    if (!fs.existsSync(merged) || args.includes('--run')) {
      console.log('producing artifacts (the media suites write them to devtools/_media-real)…');
      // The same env `test` needs: `dotnet test <solution>` BUILDS it, Android TFM included.
      const testEnv = androidBuildEnv();
      if (testEnv === null) { process.exitCode = 1; break; }
      const t = run('dotnet', ['test', config.solution, '--nologo', '-v', 'q',
        '--filter', 'FullyQualifiedName~RealSourceSegmentTests|FullyQualifiedName~RealSourceShapeTests'],
        { env: testEnv });
      if (!t) { process.exitCode = 1; break; }
    }
    if (!fs.existsSync(merged)) {
      console.error(`no artifacts at ${merged} — run with --run, or run the suite first.`);
      process.exitCode = 1;
      break;
    }

    // 🔴 BOTH SHAPES. `merged.mp4` is the ordinary one-fragment-per-segment run; `spill/spill.mp4` carries a
    // segment written as SEVERAL fragments, which is what a source with no cut point produces and the only
    // thing here a decoder could plausibly refuse.
    const spill = path.join(repo, 'devtools', '_media-spill', 'spill.mp4');
    const subjects = [merged, spill].filter((f) => fs.existsSync(f));
    let bad = false;

    for (const file of subjects) {
      const name = path.relative(path.join(repo, 'devtools'), file);
      // Every byte decoded, with errors demoted to nothing: any line here is a real complaint.
      const decode = spawnSync('ffmpeg', ['-hide_banner', '-v', 'error', '-i', file, '-f', 'null', '-'],
        { encoding: 'utf8', shell: false });
      const complaints = (decode.stderr ?? '').trim();

      const probe = spawnSync('ffprobe', ['-hide_banner', '-loglevel', 'error', '-select_streams', 'v',
        '-count_frames', '-show_entries', 'stream=nb_read_frames,codec_name', '-of', 'csv=p=0', file],
        { encoding: 'utf8', shell: false });
      const [codec, frames] = (probe.stdout ?? '').trim().split(',');
      const decoded = Number.parseInt(frames ?? '', 10);

      console.log(`${name}: ${codec || 'no video stream'}, ${Number.isNaN(decoded) ? '?' : decoded} frames decoded`);
      if (complaints) console.error(complaints);
      if (decode.status !== 0 || complaints || !(decoded > 0)) bad = true;
    }

    // 🔴 Say what was NOT looked at. The spill artifact only exists once its test has run, and a verb that
    // silently checks one file reads as having checked both.
    if (subjects.length < 2) console.log('  (no _media-spill/spill.mp4 — the multi-fragment shape was NOT checked)');

    if (bad) {
      console.error('media-decode FAILED — the segment tier produced a stream a decoder will not take.');
      process.exitCode = 1;
      break;
    }
    console.log('media-decode ok');
    break;
  }

  // Append the segment tier's own fragments into a real browser MediaSource, by running the desktop
  // sample and reading the verdict its startup probe prints.
  //
  // 🔴 WHY THIS EXISTS BESIDE media-decode. ffmpeg REPAIRS what it can, so a green decode is a floor and
  // not proof — the judge that matters is the media pipeline a page actually has. The shape most worth
  // asking about is a segment carrying SEVERAL fragments, which appears only when the memory guard spills,
  // and the bound that triggers a spill is internal: the SUITE produces those bytes and this hands them
  // over, rather than the sample being given a way to reach into the kit.
  case 'media-mse': {
    const real = path.join(repo, 'devtools', '_media-real');
    const spill = path.join(repo, 'devtools', '_media-spill');

    if (!fs.existsSync(path.join(real, 'init.mp4')) || !fs.existsSync(path.join(spill, 'init.mp4'))
        || args.includes('--run')) {
      console.log('producing artifacts (the media suites write them under devtools/)…');
      const testEnv = androidBuildEnv();
      if (testEnv === null) { process.exitCode = 1; break; }
      if (!run('dotnet', ['test', config.solution, '--nologo', '-v', 'q', '--filter',
        'FullyQualifiedName~RealSourceSegmentTests|FullyQualifiedName~RealSourceShapeTests'],
        { env: testEnv })) { process.exitCode = 1; break; }
    }

    // BOTH halves, because the probe drops a directory missing either and the count it checks itself
    // against shrinks with it.
    const usable = (dir) => fs.existsSync(path.join(dir, 'init.mp4'))
      && fs.readdirSync(dir).some((f) => f.startsWith('seg') && f.endsWith('.m4s'));
    const cases = [['ordinary', real], ['spill', spill]].filter(([, dir]) => fs.existsSync(dir) && usable(dir));
    if (cases.length === 0) { console.error('no artifacts to append — run with --run.'); process.exitCode = 1; break; }
    // 🔴 Say which shapes are going in, and name the missing one. A verdict listing a single case reads as
    // "the tier passed" unless the absence is stated beside it.
    console.log(`appending: ${cases.map(([label]) => label).join(', ')}`);
    if (cases.length < 2) {
      console.log('  (the multi-fragment SPILL shape was NOT checked — re-run with --run to produce it)');
    }

    const env = { ...process.env, SHENORA_SAMPLE_MSE_DIRS: cases.map(([l, d]) => `${l}=${d}`).join(';') };
    const app = spawn('dotnet', ['run', '--project', config.sampleProject], {
      cwd: repo, env, shell: false, stdio: ['ignore', 'pipe', 'pipe'],
    });

    // ⚠ KILL THE TREE, BY PID. `dotnet run` launches the sample as a CHILD, so killing the parent alone
    // leaves a window open; and killing by NAME is what once took out 38 unrelated processes.
    const stop = () => { try { spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* already gone */ } };

    const verdict = await new Promise((resolve) => {
      let buffer = '';
      const timer = setTimeout(() => resolve(null), 180_000);   // a cold `dotnet run` builds first
      const read = (chunk) => {
        buffer += chunk.toString();
        const hit = buffer.split('\n').find((line) => line.startsWith('SEGMENT MSE:'));
        if (hit) { clearTimeout(timer); resolve(hit.trim()); }
      };
      app.stdout.on('data', read);
      app.stderr.on('data', read);
      app.on('exit', () => { clearTimeout(timer); resolve(buffer.split('\n').find((l) => l.startsWith('SEGMENT MSE:'))?.trim() ?? null); });
    });
    stop();

    if (verdict === null) {
      console.error('media-mse FAILED — the sample never printed a verdict (it may not have reached the page).');
      process.exitCode = 1;
      break;
    }
    console.log(verdict);
    if (!verdict.includes('PASS')) { process.exitCode = 1; break; }
    process.exitCode = 0;
    break;
  }

  // Drive Shenora.IO's staged updater over a REAL directory tree. NOT part of `verify`: it publishes
  // the sample (slow) and the point is a tree with real build shape, which `verify` has no reason to
  // produce on every run. Run it before trusting an update-stage change, and hand the command to an
  // adopter so they can point it at their OWN release — that is what turns one app's manual habit into
  // a step anyone can repeat.
  case 'update-probe': {
    const exe = ensureTool('update-probe');
    if (!exe) { process.exitCode = 1; break; }

    let target = args.find((a) => !a.startsWith('--'));
    if (!target) {
      // No directory given: publish the desktop sample and probe THAT. A publish output — not bin/ —
      // because bin/ accumulates runtime droppings (the WebView2 user-data folder alone is ~150 MB of
      // files no release ever contains), which would measure the wrong thing.
      target = path.join(repo, 'devtools', '_probe-release');
      console.log('no directory given — publishing the desktop sample to probe against…');
      const p = spawnSync('dotnet', ['publish', path.join(repo, config.sampleProject), '-c', 'Release',
        '-o', target, '-v', 'quiet', '--nologo'], { stdio: 'inherit', cwd: repo });
      if (p.status !== 0) { process.exitCode = p.status ?? 1; break; }
    }
    run(exe, [target, ...args.filter((a) => a.startsWith('--'))]);
    break;
  }

  // Build the native launcher and run the conformance harness against the BINARY. Not in `verify`:
  // this repo has no C++ toolchain and deliberately does not require one (design doc §5), and `verify`
  // must run on a clone that has neither CMake nor Docker. Run it when you touch either half of the
  // protocol — and `--posix` too if you touched the POSIX half.
  //
  // ⚠ THE DEFAULT RUN PROVES ONE PLATFORM. Both platform .cpp files compile here, but only the branch
  // your compiler takes is actually checked, so a POSIX-only break sails through green (it did: see the
  // include note at the top of src/platform_posix.cpp). D5 keeps this repo on ONE manual release
  // workflow with no push CI, so `--posix` is not a convenience — it is the only thing between a broken
  // POSIX half and a failed release.
  case 'launcher': {
    if (args.includes('--posix')) { runPosixLauncherBuild(); break; }
    const cmake = resolveCmake();
    if (!cmake) {
      console.error('cmake not found. Install CMake, or use the one bundled with Visual Studio '
        + '(Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin). The release workflow builds both '
        + 'targets regardless — see .github/workflows/release.yml.');
      process.exitCode = 1;
      break;
    }
    const build = path.join(repo, 'devtools', '_launcher-build');
    const src = path.join(repo, 'src', 'Shenora.Launcher');
    const ok = step('cmake configure', () => run(cmake, ['-S', src, '-B', build, '-DCMAKE_BUILD_TYPE=Release']))
      && step('cmake build', () => run(cmake, ['--build', build, '--config', 'Release']));
    if (!ok) { process.exitCode = 1; break; }

    const exe = ['Release/shenora-launcher.exe', 'shenora-launcher.exe', 'shenora-launcher']
      .map((p) => path.join(build, p)).find((p) => fs.existsSync(p));
    if (!exe) { console.error('the launcher built but was not found in the build tree'); process.exitCode = 1; break; }

    // D50 asked for a MEASUREMENT before anything else, because its size figures were bands nobody
    // had built. Printing it every run is what keeps them measurements.
    console.log(`\nlauncher size: ${(fs.statSync(exe).size / 1024).toFixed(1)} KB  (${exe})\n`);

    const probe = ensureTool('update-probe');
    if (!probe) { process.exitCode = 1; break; }
    run('node', [path.join(repo, 'devtools', 'scripts', 'launcher-conformance.mjs'), exe, probe]);
    break;
  }

  case 'knowledge': // check | footprint | new <name> [--core] — two-tier rule-base doctor
    run('node', [path.join(repo, 'devtools', 'scripts', 'knowledge.mjs'), ...args]);
    break;

  case 'android': // devices | connect <host:port> | deploy | run | log | shot — the MAUI device loop
    run('node', [path.join(repo, 'devtools', 'scripts', 'android.mjs'), ...args]);
    break;

  // doctor | setup | push | build | run | shot | tap | type | log | awake | ssh — the iOS half of the
  // MAUI loop, which has to run on a Mac. Needs local/mac.json (gitignored: the machine is private).
  case 'mac':
    run('node', [path.join(repo, 'devtools', 'scripts', 'mac.mjs'), ...args]);
    break;

  // Unlist every version of a package id renamed away (D37). Dry-run by default, and it REFUSES
  // until the replacement is published. Deprecation is web-UI only; the command prints the text.
  case 'nuget-retire':
    run('node', [path.join(repo, 'devtools', 'scripts', 'nuget-retire.mjs'), ...args]);
    break;

  // Where the JDK is, for anything that shells out to the Android build. Prints the path or exits
  // non-zero with the fix. Deliberately ONE owner: android.mjs asks rather than re-probing, the same
  // reason the kit has one owner for UI marshalling.
  case 'android-jdk': {
    const jdk = resolveJdk();
    if (!jdk) {
      console.error('No JDK found. Set JAVA_HOME to a JDK 17+ — Android Studio ships one in its `jbr` folder.');
      process.exitCode = 1;
      break;
    }
    console.log(jdk);
    break;
  }

  case 'clean': {
    // Reclaim the BUILD OUTPUT under devtools/_* (and publish/), never the sources. Those scratch
    // folders are gitignored probes — the P6 consumers, the adoption adapters, the P7 profile
    // proofs — and they are RE-RUNNABLE, so deleting their
    // sources would quietly destroy the thing those entries point at. Their bin/obj/node_modules is
    // ~60 MB of regenerable weight and is fair game.
    //
    // `--all` also drops the probe sources and the packed output, for reclaiming a checkout you do
    // not intend to re-run. It is opt-in because it is the destructive reading of "clean".
    const dropSources = args.includes('--all');
    const targets = [];
    const scratch = fs.existsSync(path.join(repo, 'devtools'))
      ? fs.readdirSync(path.join(repo, 'devtools')).filter((f) => f.startsWith('_')) : [];
    for (const entry of scratch) {
      const full = path.join(repo, 'devtools', entry);
      if (!fs.statSync(full).isDirectory()) continue;
      if (dropSources) { targets.push(full); continue; }
      // Walk shallowly for the regenerable folders rather than guessing at a layout.
      const stack = [full];
      while (stack.length) {
        const dir = stack.pop();
        for (const child of fs.readdirSync(dir, { withFileTypes: true })) {
          if (!child.isDirectory()) continue;
          const childPath = path.join(dir, child.name);
          if (['bin', 'obj', 'node_modules', 'out', 'dist'].includes(child.name)) targets.push(childPath);
          else stack.push(childPath);
        }
      }
    }
    if (dropSources) targets.push(path.join(repo, ...config.packagesDir.split('/')));

    let freed = 0;
    for (const t of targets) {
      if (!fs.existsSync(t)) continue;
      try {
        // Never fs.cpSync/rmSync surprises: rmSync recursive is fine, but a file held open by a
        // running sample or an editor will throw — report it instead of aborting the whole sweep.
        fs.rmSync(t, { recursive: true, force: true });
        freed++;
        console.log(`  removed ${path.relative(repo, t)}`);
      } catch (e) {
        console.error(`  SKIPPED ${path.relative(repo, t)} — ${e.code ?? e.message} (in use?)`);
      }
    }
    console.log(freed === 0
      ? 'clean: nothing to remove.'
      : `clean: removed ${freed} folder(s)${dropSources ? ' INCLUDING probe sources and packed output' : ' (probe sources kept — re-runnable)'}.`);
    break;
  }

  case 'check-sensitive':
    run('node', [path.join(repo, 'devtools', 'scripts', 'check-sensitive.mjs'), ...args]);
    break;

  // stale-scan [path] — every retired name, WITHOUT doc-drift's history suppression. A review tool,
  // never a gate: it is deliberately noisy and the triage is a human's. Run it in the same commit as
  // a rename — see the script header for the three commits that shipped a deleted API without it.
  case 'stale-scan':
    run('node', [path.join(repo, 'devtools', 'scripts', 'stale-scan.mjs'), ...args]);
    break;

  // self-rename-scan — sentences naming one identifier on BOTH sides of a relation ("`X` depends on
  // `X`"), which is what a repo-wide rename leaves in the one sentence whose subject was the old name.
  // Five of these were found across the docs on 2026-08-09/10, three of them after two prose audits had
  // run clean: doc-drift and cite-scan cannot see it, because both names exist and they are the same
  // name. Noisy on purpose, never a gate — same standing as stale-scan.
  case 'self-rename-scan':
    run('node', [path.join(repo, 'devtools', 'scripts', 'self-rename-scan.mjs'), ...args]);
    break;

  // name-scope — a type whose name claims an AREA while serving one KIND, and a file named after a type
  // that does not exist. Neither is findable by the prose scanners: every name involved EXISTS, so
  // doc-drift/cite-scan/stale-scan all see a live identifier and say nothing. Earned when the owner
  // asked why `InteractiveSession.cs` did not match its classes — it declared `SessionResult` and
  // `SessionErrorCodes`, names promising all seven session kinds and serving one. Review tool, never a
  // gate: a CLUSTER file named for its area (ShellContracts.cs) is correct and common here.
  case 'name-scope':
    run('node', [path.join(repo, 'devtools', 'scripts', 'name-scope.mjs'), ...args]);
    break;

  // cite-scan [doc…] — identifiers a doc cites that exist NOWHERE in the source. Starts from the DOCS
  // rather than from retired-names.txt, so it is the only one of the three that catches a rename whose
  // step 2 was skipped — which is every one it found on its first run. Review tool, never a gate.
  case 'cite-scan':
    run('node', [path.join(repo, 'devtools', 'scripts', 'cite-scan.mjs'), ...args]);
    break;

  // decision-audit [D<n>…] [--verbose] — per-ENTRY truth check for DECISIONS.md, ranked worst-first.
  // cite-scan's unit is a LINE; the unit a session trusts (and the unit that gets rewritten) is an
  // ENTRY, and this also checks the three claim kinds that file keeps getting wrong: dead package ids,
  // a live namespace called a package, and a retired name stated as current. It separates a live lie
  // from correct past tense, which is what makes the output triageable.
  // ⚠ TRUTH ONLY — whether a decision is still REASONABLE is a judgement no script makes.
  case 'decision-audit':
    run('node', [path.join(repo, 'devtools', 'scripts', 'decision-audit.mjs'), ...args]);
    break;

  // doc-shape [--check] — the shape rules made mechanical: no dated self-narration in a tracked doc,
  // a D-entry cap, D-number integrity, TASKS.md holds no done-markers, PROJECT_NOTES.md is not a
  // session log. Report-only without `--check`; `verify` passes `--check`.
  case 'doc-shape':
    run('node', [path.join(repo, 'devtools', 'scripts', 'doc-shape.mjs'), ...args]);
    break;

  // reserved-paths — a tracked or stageable path Windows cannot check out (a reserved DEVICE name, or
  // a segment ending in a dot/space). Names only; `verify` runs it.
  case 'reserved-paths':
    run('node', [path.join(repo, 'devtools', 'scripts', 'reserved-paths.mjs'), ...args]);
    break;

  // decisions-index [--check] — regenerate DECISIONS.md's opening list from its own entries, so the
  // decisions can be read without scrolling the rationale. `verify` passes `--check`.
  case 'decisions-index':
    run('node', [path.join(repo, 'devtools', 'scripts', 'decisions-index.mjs'), ...args]);
    break;

  // namespace-moves <from-ref> [to-ref] — old FQN → new FQN for every type that changed namespace,
  // read from the API baselines. A RELEASE step: the package-fold table in a changelog names the
  // packages, and an adopter following it still meets one CS0246 per type (first harvest, 0.11.0 —
  // 154 types moved and the notes listed five). Paste the output into the release's migration notes.
  case 'namespace-moves':
    run('node', [path.join(repo, 'devtools', 'scripts', 'namespace-moves.mjs'), ...args]);
    break;

  // retired-audit [tag] [rev=HEAD] — which public types left the SHIPPED surface without a retired-names
  // entry. The question BEFORE stale-scan's: not "is this name still described as current?" but "is this
  // removal recorded at all?" Release step, not part of verify — it needs tags.
  // Also reports `required` DELTAS, the contract change that keeps every name: GAINING it on a property
  // that already shipped is a hard source break (an adopter's object initializer stops compiling) and
  // fails unless the CHANGELOG names it; LOSING it only invalidates prose, so it prints and passes.
  case 'retired-audit':
    run('node', [path.join(repo, 'devtools', 'scripts', 'retired-audit.mjs'), ...args]);
    break;

  case 'install-hooks':
    // Point git at the tracked hooks dir so the sensitive-info pre-commit guard runs (a clone only
    // needs this once — core.hooksPath is local config, the hook script itself is versioned).
    run('git', ['config', 'core.hooksPath', 'devtools/hooks']);
    console.log('git hooks installed (core.hooksPath = devtools/hooks). Pre-commit runs check-sensitive.');
    break;

  default:
    // ⚠ THIS STRING IS THE ONLY DISCOVERY SURFACE FOR A VERB, so a verb missing from it is a tool nobody
    // finds. `stale-scan` and `cite-scan` were both absent for their whole lives until 2026-08-10 — each
    // shipped with a `case` and a rule telling you to run it, and neither appeared here.
    console.log('usage: node devtools/dev.mjs <build|test|verify|pack|doctor|changelog|sample|vite|shot|wgc|click|rclick|move|drag|input|responsiveness|android|mac|launcher [--posix]|nuget-retire|knowledge|clean|check-sensitive|reserved-paths|install-hooks>');
    console.log('  release        : retired-audit <prev-tag>   (account for every public REMOVAL)');
    console.log('                   namespace-moves <prev-tag> (old FQN -> new FQN, for the migration notes)');
    console.log('  probes         : update-probe [dir] | android-jdk');
    console.log('  media          : media-decode [--run] (ffmpeg) | media-mse [--run] (runs the desktop sample)');
    console.log('  prose review (never gates, triage by hand): stale-scan | cite-scan | self-rename-scan | decision-audit | name-scope');
    console.log('  doc shape      : doc-shape [--check]        (verify runs --check: self-narration FAILS, the D-entry line cap WARNS)');
  console.log('  decisions      : decisions-index [--check]  (regenerate DECISIONS.md\'s opening list from its own entries)');
    process.exitCode = cmd ? 1 : 0;
}
