// Shenora devtools dispatcher (family pattern: one entry, allow-listed once).
//   node devtools/dev.mjs build            - dotnet build the solution + npm build the react package
//   node devtools/dev.mjs test [dotnet|npm] - dotnet test + vitest (or just one side)
//   node devtools/dev.mjs verify           - build · test · check-sensitive --tree · knowledge check (the "am I done?" gate)
//   node devtools/dev.mjs pack             - nupkgs + npm tarball -> publish/packages (lockstep version, sha256 printed)
//   node devtools/dev.mjs doctor [--fix]   - version/readme drift check (npm package.json + README headline vs VersionPrefix)
//   node devtools/dev.mjs sample [--dev]   - run the sample desktop app (Phase 2+)
//   node devtools/dev.mjs vite             - run the sample web dev server (Phase 2+)
//   node devtools/dev.mjs shot|wgc [name]  - capture the sample window (PrintWindow / occlusion-immune WGC)
//   node devtools/dev.mjs click|rclick|move <fx> <fy>       - background mouse at client fractions (no CDP, no focus steal)
//   node devtools/dev.mjs drag <fx1> <fy1> <fx2> <fy2>      - background press-move-release between two fractions
//   node devtools/dev.mjs input <args…>    - raw win-input passthrough (list | click | rclick | move | drag)
//   node devtools/dev.mjs knowledge <…>    - two-tier rule-base doctor (check | footprint | new <name> [--core])
//   node devtools/dev.mjs check-sensitive [--tree] - public-repo leak scan (the pre-commit guard)
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

// ---- Evict this repo's packages from the NuGet GLOBAL cache after packing.
//
// NuGet keys the global folder (~/.nuget/packages) on id+VERSION and it wins over every source, so
// re-packing the same pre-release version leaves consumers restoring the OLD copy — silently, with
// no warning and no restore error. Found in P6.1: a consumer resolved a Shenora.WebView2 packed
// before the D19 re-layer, so `Shenora.WinForms` was simply absent from its dependency graph and the
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

  if (problems === 0) console.log(`  ok  version ${config.version} consistent (props · npm · README · LICENSE)`);
  return problems === 0;
}

switch (cmd) {
  case 'build': {
    // No -clp:ErrorsOnly: warnings must be VISIBLE (they are errors under TreatWarningsAsErrors,
    // but a suppressed-warning build is how invisible problems accumulated — P5.5 H5).
    const ok = step('dotnet build', () => run('dotnet', ['build', config.solution, '-v', 'minimal']))
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
    if (which === 'all' || which === 'dotnet')
      ok = step('dotnet test', () => run('dotnet', ['test', config.solution, '-v', 'minimal', '--nologo'])) && ok;
    if (which === 'all' || which === 'npm')
      ok = (ensureNpmDeps(npmDirAbs) && step('vitest (react package)', () => runNpm('test', { cwd: npmDirAbs }))) && ok;
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'verify': {
    // The "am I done?" gate — run EVERYTHING and stop at the first failure.
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
      // doctor LAST and non-fixing: verify must FAIL on version/README drift rather than leave it to
      // `pack` (which runs doctor --fix, so verify was scanning pre-sync files) — P5.5 H5.
      ['doctor', () => doctor({ fix: false })],
    ];
    let ok = true;
    for (const [label, fn] of steps) {
      if (!step(`verify: ${label}`, fn)) { ok = false; break; }
    }
    console.log(ok ? '\nVERIFY PASSED' : '\nVERIFY FAILED');
    process.exitCode = ok ? 0 : 1;
    break;
  }

  case 'pack': {
    if (!doctor({ fix: true })) { process.exitCode = 1; break; }
    const out = path.join(repo, ...config.packagesDir.split('/'));
    fs.rmSync(out, { recursive: true, force: true });
    fs.mkdirSync(out, { recursive: true });
    let ok = true;
    for (const proj of config.packableProjects) {
      ok = step(`pack ${proj}`, () => run('dotnet', ['pack', proj, '-c', 'Release', '-o', out,
        `-p:Version=${config.version}`, '-v', 'minimal', '-clp:ErrorsOnly'])) && ok;
    }
    ok = ok && ensureNpmDeps(npmDirAbs);
    ok = ok && step('npm build (react package)', () => runNpm('run build', { cwd: npmDirAbs }));
    ok = ok && step('npm pack (react package)', () => runNpm(`pack --pack-destination "${out}"`, { cwd: npmDirAbs }));
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

  case 'doctor':
    process.exitCode = doctor({ fix: args.includes('--fix') }) ? 0 : 1;
    break;

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
    const name = args[0] ?? `${config.shotPrefix}-${new Date().toISOString().slice(11, 19).replaceAll(':', '')}`;
    fs.mkdirSync(path.join(repo, 'devtools', 'screenshots'), { recursive: true });
    run('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass',
      '-File', path.join(repo, 'devtools', 'scripts', 'shot-window.ps1'),
      '-ProcessName', config.processName,
      '-OutFile', path.join(repo, 'devtools', 'screenshots', `${name}.png`)]);
    break;
  }

  // Native desktop-verification tools (win-input drives the WebView2 UI via background PostMessage
  // clicks; wgc-shot is an occlusion-immune capture). Built on demand into their gitignored bin/.
  case 'wgc': case 'click': case 'rclick': case 'move': case 'drag': case 'input': {
    const toolName = cmd === 'wgc' ? 'wgc-shot' : 'win-input';
    const toolTfm = { 'win-input': 'net10.0-windows', 'wgc-shot': 'net10.0-windows10.0.22621.0' };
    const exe = path.join(repo, 'devtools', toolName, 'bin', 'Release', toolTfm[toolName], `${toolName}.exe`);
    if (!fs.existsSync(exe)) {
      console.log(`building ${toolName} (first run)…`);
      const b = spawnSync('dotnet', ['build', path.join(repo, 'devtools', toolName, `${toolName}.csproj`),
        '-c', 'Release', '-v', 'quiet'], { stdio: 'inherit', cwd: repo });
      if (b.status !== 0) { process.exitCode = 1; break; }
    }
    const env = { ...process.env, DEVTOOL_PROC: config.processName };
    if (cmd === 'wgc') {
      const name = args[0] ?? `${config.shotPrefix}-${new Date().toISOString().slice(11, 19).replaceAll(':', '')}`;
      fs.mkdirSync(path.join(repo, 'devtools', 'screenshots'), { recursive: true });
      run(exe, ['--out', path.join(repo, 'devtools', 'screenshots', `${name}.png`)], { env });
    } else if (cmd === 'input') {
      run(exe, args, { env });
    } else {
      run(exe, [cmd, ...args], { env });
    }
    break;
  }

  case 'knowledge': // check | footprint | new <name> [--core] — two-tier rule-base doctor
    run('node', [path.join(repo, 'devtools', 'scripts', 'knowledge.mjs'), ...args]);
    break;

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
    console.log('usage: node devtools/dev.mjs <build|test|verify|pack|doctor|sample|vite|shot|wgc|click|rclick|move|drag|input|knowledge|check-sensitive|install-hooks>');
    process.exitCode = cmd ? 1 : 0;
}
