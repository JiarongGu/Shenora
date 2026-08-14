#!/usr/bin/env node
// launcher-conformance — drive a PREBUILT launcher over sandbox directories and check it agrees with
// the C# half of the two-phase update protocol.
//
// WHY THIS SHAPE. `dev.mjs verify` cannot compile C++ and should not try (§5); the design doc takes the
// sibling's model instead — test the BINARY, not the source, so the check needs no compiler at the
// moment it runs. And every stage here is written by the REAL C# implementation (`update-probe
// --stage-only`), never by a fixture this file invents: the whole risk D50 names is that a protocol
// implemented twice, once per language, drifts. Fixtures written on this side would agree with
// themselves and prove nothing.
//
// Usage: node devtools/scripts/launcher-conformance.mjs <launcher-exe> <update-probe-exe>
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const [launcher, probe] = process.argv.slice(2);
if (!launcher || !probe) {
  console.error('usage: launcher-conformance.mjs <launcher-exe> <update-probe-exe>');
  process.exit(2);
}
for (const [what, exe] of [['launcher', launcher], ['update-probe', probe]]) {
  if (!fs.existsSync(exe)) {
    console.error(`launcher-conformance: no ${what} at ${exe} — build it first.`);
    process.exit(2);
  }
}

let failures = 0;
const cases = [];
const test = (name, fn) => cases.push([name, fn]);

/** A sandbox laid out the way Sonora's topology (D50/§2) does: launcher at {root}, app in {root}/app. */
function sandbox() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'shenora-launcher-'));
  fs.mkdirSync(path.join(root, 'app'), { recursive: true });
  fs.mkdirSync(path.join(root, 'release'), { recursive: true });
  // The launcher resolves the install root from its OWN location, so it has to sit in the sandbox.
  const placed = path.join(root, path.basename(launcher));
  fs.copyFileSync(launcher, placed);
  return { root, app: path.join(root, 'app'), release: path.join(root, 'release'), exe: placed };
}

const write = (file, text) => {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, text);
};

/** Stage `release` into `root` using the REAL C# implementation. */
const stage = (box) =>
  execFileSync(probe, [box.release, '--stage-only', box.root], { encoding: 'utf8' });

/** Run the launcher's apply path and parse its machine-readable line. */
function apply(box) {
  let out = '';
  let code = 0;
  try {
    out = execFileSync(box.exe, ['--apply-and-exit'], { encoding: 'utf8', cwd: box.root });
  } catch (e) {
    out = (e.stdout ?? '') + (e.stderr ?? '');
    code = e.status ?? 1;
  }
  const fields = Object.fromEntries(
    [...out.matchAll(/(\w+)=([^\s]*)/g)].map((m) => [m[1], m[2]]));
  return { code, out, ...fields };
}

const assert = (cond, message) => { if (!cond) throw new Error(message); };

// ── The cases ───────────────────────────────────────────────────────────────────────────────────────

test('applies a stage the C# side produced', (box) => {
  write(path.join(box.app, 'app.dll'), 'v1');
  write(path.join(box.release, 'app.dll'), 'v2');
  write(path.join(box.release, 'libs/new.dll'), 'added');
  stage(box);

  const r = apply(box);
  assert(r.applied === '1', `expected applied=1, got: ${r.out.trim()}`);
  assert(fs.readFileSync(path.join(box.app, 'app.dll'), 'utf8') === 'v2', 'the file was not replaced');
  assert(fs.existsSync(path.join(box.app, 'libs/new.dll')), 'the added file did not land');
  assert(fs.existsSync(path.join(box.app, 'manifest.json')), 'the baseline was not written');
  assert(!fs.existsSync(path.join(box.root, '.update')), 'the stage was not cleared');
});

test('a SECOND run does nothing — the marker is gone', (box) => {
  write(path.join(box.release, 'app.dll'), 'v2');
  stage(box);
  assert(apply(box).applied === '1', 'first apply should succeed');

  // Idempotency is the property that matters at boot: a launcher runs on EVERY start, and one that
  // re-applies a cleared stage would overwrite the running install on every launch.
  const second = apply(box);
  assert(second.attempted === '0', `second run should find nothing staged, got: ${second.out.trim()}`);
});

test('REMOVALS are tracked paths only — user data survives', (box) => {
  // §4 and D30: user data lives in the same tree, so "delete what the release does not list" would
  // destroy it. This is the guard whose failure loses data rather than merely failing.
  write(path.join(box.app, 'old.dll'), 'dropped by the new release');
  write(path.join(box.release, 'app.dll'), 'v2');
  stage(box);
  // Baseline says old.dll was installed; the release does not list it, so it must go...
  const baseline = { version: '1.0', files: [{ path: 'old.dll', size: 5, sha256: 'x' }] };
  write(path.join(box.app, 'manifest.json'), JSON.stringify(baseline));
  // ...while these two were never tracked and must survive.
  write(path.join(box.app, 'data/user.db'), 'the user\'s own file');
  write(path.join(box.app, 'stray.log'), 'not tracked, not deleted');

  const r = apply(box);
  assert(r.applied === '1', `expected applied=1, got: ${r.out.trim()}`);
  assert(!fs.existsSync(path.join(box.app, 'old.dll')), 'a tracked-and-dropped file was NOT removed');
  assert(fs.existsSync(path.join(box.app, 'data/user.db')), 'USER DATA WAS DELETED');
  assert(fs.existsSync(path.join(box.app, 'stray.log')), 'an untracked file was deleted');
});

test('refuses when the staged manifest is missing', (box) => {
  write(path.join(box.release, 'app.dll'), 'v2');
  stage(box);
  fs.rmSync(path.join(box.root, '.update', 'staged', 'manifest.json'));

  const r = apply(box);
  assert(r.applied === '0' && r.attempted === '1', `expected a refusal, got: ${r.out.trim()}`);
  assert(fs.existsSync(path.join(box.root, '.update')), 'a refused stage must be LEFT for a retry');
  // The same refusal the C# ApplyAsync gives, for the same reason: removals are installed-minus-release
  // and an absent release manifest would delete every tracked path, including what was just overlaid.
});

test('refuses when the staged manifest lists nothing', (box) => {
  write(path.join(box.release, 'app.dll'), 'v2');
  stage(box);
  write(path.join(box.root, '.update', 'staged', 'manifest.json'), '{"version":"2.0","files":[]}');
  assert(apply(box).applied === '0', 'an empty release manifest must be refused, not obeyed');
});

test('REFUSES a staged manifest whose path escapes the app root', (box) => {
  // 🔴 The manifest is the only input this program takes from a remote server, and it drives
  // `fs::remove`. `std::filesystem::operator/` REPLACES its left side when the right is absolute —
  // byte for byte the trap C#'s `Path.Combine` has — so a rooted or `..` path would delete outside
  // the tree. `parse_manifest` refuses the whole manifest, and step 2 turns that into a refusal.
  // The C# owner is `ManifestDiff.IsSafeRelativePath`; these two must agree, which is why this case
  // lives beside the "reads what the C# side WRITES" mirror rather than in the C# suite.
  write(path.join(box.release, 'app.dll'), 'v2');
  stage(box);
  const staged = path.join(box.root, '.update', 'staged', 'manifest.json');
  for (const escaping of ['../escape.txt', '..\\escape.txt', '/etc/passwd', 'C:\\Windows\\evil.dll']) {
    write(staged, JSON.stringify({
      version: '2.0',
      files: [{ path: escaping, size: 2, sha256: 'x' }],
    }));
    assert(apply(box).applied === '0',
      `an escaping manifest path was accepted and applied: ${escaping}`);
  }
});

test('the manifest parser reads what the C# side WRITES', (box) => {
  // The conformance case proper. `update-probe --stage-only` wrote this file with System.Text.Json:
  // camelCase names, indented, and it carries members the C++ parser does not model. If the parser
  // rejected unknown members, or tripped on the formatting, this is where it shows.
  write(path.join(box.release, 'nested/deep/x.dll'), 'v2');
  write(path.join(box.release, 'Mixed Case Name.dll'), 'v2');
  stage(box);
  const written = fs.readFileSync(path.join(box.root, '.update', 'staged', 'manifest.json'), 'utf8');
  assert(written.includes('"path"'), 'the C# side did not write camelCase — the mirror assumption is wrong');

  const r = apply(box);
  assert(r.applied === '1', `C#-written manifest was not applied: ${r.out.trim()}`);
  assert(fs.existsSync(path.join(box.app, 'nested/deep/x.dll')), 'a nested path did not land');
  assert(fs.existsSync(path.join(box.app, 'Mixed Case Name.dll')), 'a spaced/mixed-case path did not land');
});

// ── Run ─────────────────────────────────────────────────────────────────────────────────────────────

console.log(`launcher : ${launcher}`);
console.log(`probe    : ${probe}\n`);
for (const [name, fn] of cases) {
  const box = sandbox();
  try {
    fn(box);
    console.log(`  ok    ${name}`);
  } catch (e) {
    failures++;
    console.error(`  FAIL  ${name}\n        ${e.message}`);
  } finally {
    try { fs.rmSync(box.root, { recursive: true, force: true }); } catch { /* best effort */ }
  }
}

console.log(failures === 0
  ? `\nlauncher-conformance: ${cases.length} case(s) PASSED against a prebuilt binary`
  : `\nlauncher-conformance: ${failures} of ${cases.length} FAILED`);
process.exitCode = failures === 0 ? 0 : 1;
