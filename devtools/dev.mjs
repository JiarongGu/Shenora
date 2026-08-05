// Shenora devtools dispatcher (family pattern: one entry, allow-listed once).
//   node devtools/dev.mjs build            - dotnet build the solution + npm build the react package
//   node devtools/dev.mjs test [dotnet|npm] - dotnet test + vitest (or just one side)
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

// The npm package.json version and the README "## Status" headline are derived; `doctor` fails
// on drift, `doctor --fix` (also run by `pack`) rewrites them.
function doctor({ fix = false } = {}) {
  let problems = 0;
  const fail = (msg) => { problems++; console.error('  FAIL ' + msg); };

  const pkgPath = path.join(npmDirAbs, 'package.json');
  const pkg = readNpmPackage();
  if (pkg.version !== config.version) {
    if (fix) {
      fs.writeFileSync(pkgPath, fs.readFileSync(pkgPath, 'utf8')
        .replace(/"version":\s*"[^"]+"/, `"version": "${config.version}"`));
      console.log(`  fixed ${config.npmDir}/package.json version -> ${config.version}`);
    } else fail(`${config.npmDir}/package.json version ${pkg.version} != VersionPrefix ${config.version}`);
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

  // The npm tarball must SHIP the license text, not just declare MIT in the manifest (P5.5 H6). The
  // package needs its own copy because npm packs only files under the package directory — so the root
  // LICENSE is the source and this is checked against it, rather than trusting two files to stay equal.
  const rootLicense = path.join(repo, 'LICENSE');
  const npmLicense = path.join(npmDirAbs, 'LICENSE');
  if (fs.existsSync(rootLicense)) {
    const expected = fs.readFileSync(rootLicense, 'utf8');
    const actual = fs.existsSync(npmLicense) ? fs.readFileSync(npmLicense, 'utf8') : null;
    if (actual !== expected) {
      if (fix) {
        // readFileSync/writeFileSync, never fs.cpSync — it hard-crashes Node 24 on this machine
        // (see .claude/rules/windows-dev-gotchas.md).
        fs.writeFileSync(npmLicense, expected);
        console.log(`  fixed ${config.npmDir}/LICENSE (copied from the root LICENSE)`);
      } else fail(`${config.npmDir}/LICENSE ${actual === null ? 'is missing' : 'differs from'} the root LICENSE`);
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

  if (problems === 0)
    // Don't claim the tag matched when the check was skipped — a success line that overstates what
    // ran is the same defect class as a doc that overstates what the code does.
    console.log(`  ok  version ${config.version} consistent (props · npm · README · ARCHITECTURE · LICENSE)`
      + (releasing ? ' — tag check skipped, this is the release' : ' and matches the newest tag')
      + `; ${config.packableProjects.length} packable project(s) agree with their csprojs`
      + '; no stray tracked filenames');
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
function ensureTool(toolName) {
  const exe = path.join(repo, 'devtools', toolName, 'bin', 'Release', TOOL_TFM[toolName], `${toolName}.exe`);
  if (!fs.existsSync(exe)) {
    console.log(`building ${toolName} (first run)…`);
    const b = spawnSync('dotnet', ['build', path.join(repo, 'devtools', toolName, `${toolName}.csproj`),
      '-c', 'Release', '-v', 'quiet'], { stdio: 'inherit', cwd: repo });
    if (b.status !== 0) return null;
  }
  return exe;
}

switch (cmd) {
  case 'build': {
    // No -clp:ErrorsOnly: warnings must be VISIBLE (they are errors under TreatWarningsAsErrors,
    // but a suppressed-warning build is how invisible problems accumulated — P5.5 H5).
    const buildEnv = androidBuildEnv();
    const ok = buildEnv !== null
      && step('dotnet build', () => run('dotnet', ['build', config.solution, '-v', 'minimal'], { env: buildEnv }))
      && ensureNpmDeps(npmDirAbs)
      && step('npm build (react package)', () => runNpm('run build', { cwd: npmDirAbs }));
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'test': {
    const which = args[0] ?? 'all';
    // Fail loudly on a typo: this used to fall through both ifs and exit 0 having run NOTHING,
    // i.e. `dev.mjs test dotnett` reported success (P5.5 H5).
    if (!['all', 'dotnet', 'npm'].includes(which)) {
      console.error(`dev.mjs test: unknown target "${which}" — expected all | dotnet | npm`);
      process.exitCode = 1;
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
      ok = testEnv !== null
        && step('dotnet test', () => run('dotnet', ['test', config.solution, '-v', 'minimal', '--nologo'], { env: testEnv }))
        && ok;
    }
    if (which === 'all' || which === 'npm')
      ok = (ensureNpmDeps(npmDirAbs) && step('vitest (react package)', () => runNpm('test', { cwd: npmDirAbs }))) && ok;
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
      ['sample web typecheck', () => {
        // The e2e subject's TS was never type-checked by any gate (P5.5 H5). Skipped only when the
        // sample web app doesn't exist yet.
        const webDir = path.join(repo, ...config.sampleWebDir.split('/'));
        if (!fs.existsSync(webDir)) return true;
        return ensureNpmDeps(webDir) && runNpm('run typecheck', { cwd: webDir });
      }],
      ['check-sensitive --tree', () => spawnSync('node', [path.join(repo, 'devtools', 'scripts', 'check-sensitive.mjs'), '--tree'], { stdio: 'inherit', cwd: repo }).status === 0],
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
    const selected = config.packableProjects.filter((p) => macOnly.has(p) === macPass);
    const skipped = config.packableProjects.filter((p) => !selected.includes(p));

    if (macPass && process.platform !== 'darwin') {
      console.error('dev.mjs pack --mac: needs macOS — these packages require Xcode to build.');
      process.exitCode = 1;
      break;
    }
    if (skipped.length) {
      console.log(`  skipped (${macPass ? 'not part of --mac' : 'needs macOS — see macOnlyPackableProjects'}):`);
      for (const p of skipped) console.log(`    ${p}`);
    }

    let ok = true;
    for (const proj of selected) {
      ok = step(`pack ${proj}`, () => run('dotnet', ['pack', proj, '-c', 'Release', '-o', out,
        `-p:Version=${config.version}`, '-v', 'minimal', '-clp:ErrorsOnly'], { env: packEnv })) && ok;
    }
    // The npm package belongs to the default pass — `--mac` produces NuGet only, so the two passes
    // cannot both emit a tarball and leave the publish step guessing which is current.
    if (!macPass) {
      ok = ok && ensureNpmDeps(npmDirAbs);
      ok = ok && step('npm build (react package)', () => runNpm('run build', { cwd: npmDirAbs }));
      ok = ok && step('npm pack (react package)', () => runNpm(`pack --pack-destination "${out}"`, { cwd: npmDirAbs }));
    }
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
    process.exitCode = versionOk && driftOk ? 0 : 1;
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

  // ---- Sample-app loop (Phase 2+; see docs/ROADMAP.md). The capture/input tools below already
  // work against any process named in project.config.mjs once the sample exists.
  case 'sample': {
    const projDir = path.join(repo, ...config.sampleProject.split('/'));
    if (!fs.existsSync(projDir)) { console.error(`sample project not created yet (${config.sampleProject}) — Phase 2, see docs/ROADMAP.md`); process.exitCode = 1; break; }
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
    if (!fs.existsSync(webDir)) { console.error(`sample web not created yet (${config.sampleWebDir}) — Phase 2, see docs/ROADMAP.md`); process.exitCode = 1; break; }
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
    // proofs — and docs/ROADMAP + task-archive describe them as RE-RUNNABLE, so deleting their
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

  case 'install-hooks':
    // Point git at the tracked hooks dir so the sensitive-info pre-commit guard runs (a clone only
    // needs this once — core.hooksPath is local config, the hook script itself is versioned).
    run('git', ['config', 'core.hooksPath', 'devtools/hooks']);
    console.log('git hooks installed (core.hooksPath = devtools/hooks). Pre-commit runs check-sensitive.');
    break;

  default:
    console.log('usage: node devtools/dev.mjs <build|test|verify|pack|doctor|changelog|sample|vite|shot|wgc|click|rclick|move|drag|input|responsiveness|android|mac|nuget-retire|knowledge|clean|check-sensitive|install-hooks>');
    process.exitCode = cmd ? 1 : 0;
}
