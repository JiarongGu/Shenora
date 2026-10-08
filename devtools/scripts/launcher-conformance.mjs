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
//          [--screen <screen-launcher> <wide-launcher> <fake-app>]
// --screen adds the startup screen's cases: launchers built with SHENORA_LAUNCHER_TESTS=ON (the template with a small
// screen compiled in) and the fake app they start, which records its arguments and closes, leaves or ignores the screen.
// The same build's other test binaries (the stripes and corrupt launchers, the Windows console host) are found beside
// the screen launcher.
import { execFileSync, spawn } from 'node:child_process';
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

test('a file name outside ASCII lands, and a tracked one is removed', (box) => {
  // Manifest paths are UTF-8, and on Windows a narrow path is read in the ANSI code page: the removal looked for
  // another name, and a name that code page cannot hold threw out of the apply. Two scripts, so the case fails on a
  // Western code page and on a CJK one alike.
  write(path.join(box.release, 'café-中.dll'), 'v2');
  stage(box);
  write(path.join(box.app, 'vieux-ğ-旧.dll'), 'dropped by the new release');
  write(path.join(box.app, 'manifest.json'),
    JSON.stringify({ version: '1.0', files: [{ path: 'vieux-ğ-旧.dll', size: 5, sha256: 'x' }] }));

  const r = apply(box);
  assert(r.applied === '1', `expected applied=1, got: ${r.out.trim()}`);
  assert(fs.existsSync(path.join(box.app, 'café-中.dll')), 'a non-ASCII file name did not land');
  assert(!fs.existsSync(path.join(box.app, 'vieux-ğ-旧.dll')), 'a tracked non-ASCII file was not removed');
});

test('an overlay that fails part-way leaves the installed version whole', (box) => {
  // A write that failed mid-overlay left two versions in the tree, and the template then STARTED it.
  write(path.join(box.app, 'a.dll'), 'v1');
  write(path.join(box.app, 'b.dll'), 'v1');
  write(path.join(box.release, 'a.dll'), 'v2');
  write(path.join(box.release, 'b.dll'), 'v2');
  write(path.join(box.release, 'c-new.dll'), 'v2');
  stage(box);
  fs.chmodSync(path.join(box.app, 'b.dll'), 0o444);   // read-only: its overwrite fails, whatever the walk order
  try {
    const r = apply(box);
    assert(r.applied === '0' && r.attempted === '1', `expected a failed apply, got: ${r.out.trim()}`);
    assert(fs.readFileSync(path.join(box.app, 'a.dll'), 'utf8') === 'v1', 'a replaced file was not put back');
    assert(!fs.existsSync(path.join(box.app, 'c-new.dll')), 'a file the failed overlay added was left behind');
    assert(fs.existsSync(path.join(box.root, '.update', 'ready.json')), 'the stage was not kept for a retry');
  } finally {
    fs.chmodSync(path.join(box.app, 'b.dll'), 0o644);
  }
});

test('a process whose path only STARTS like the app tree is left running', (box) => {
  // `…/app` matched `…/app-old/…` as a bare string prefix, and the apply closed, then killed, that process.
  const tool = process.platform === 'win32' ? path.join(process.env.SystemRoot, 'System32', 'PING.EXE') : '/bin/sleep';
  const copy = path.join(box.root, 'app-old', path.basename(tool));
  fs.mkdirSync(path.dirname(copy));
  fs.copyFileSync(tool, copy);
  const child = spawn(copy, process.platform === 'win32' ? ['-n', '60', '127.0.0.1'] : ['60'], { stdio: 'ignore' });
  try {
    write(path.join(box.release, 'app.dll'), 'v2');
    stage(box);
    const r = apply(box);
    assert(r.applied === '1', `expected applied=1, got: ${r.out.trim()}`);
    let alive = true;
    try { process.kill(child.pid, 0); } catch { alive = false; }
    assert(alive, 'the apply stopped a process outside the app tree (app-old/)');
  } finally {
    child.kill();
  }
});

// ── The startup screen (--screen) ───────────────────────────────────────────────────────────────────
// What the app is passed, that the screen is up before the app starts, the three ways it ends (the app closes it, the
// app exits, the timeout), and the frame the compositor draws. The stock launcher, built with no screen, passes nothing.

const screenAt = process.argv.indexOf('--screen');
const screenRoots = [];   // removed at the end: a fake app in "close" mode holds its exe a few seconds more
if (screenAt > 0) {
  const [screenLauncher, wideLauncher, fakeApp] = process.argv.slice(screenAt + 1, screenAt + 4);
  for (const [what, exe] of [['screen launcher', screenLauncher], ['wide launcher', wideLauncher], ['fake app', fakeApp]]) {
    if (!exe || !fs.existsSync(exe)) {
      console.error(`launcher-conformance: no ${what} at ${exe} — build with -DSHENORA_LAUNCHER_TESTS=ON.`);
      process.exit(2);
    }
  }
  const appName = 'MyApp.exe';   // the template's kAppExecutable

  /** A sandbox with `launcherExe` at its root and the fake app as the app. */
  const screenBox = (launcherExe) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'shenora-screen-'));
    screenRoots.push(root);
    fs.mkdirSync(path.join(root, 'app'));
    const exe = path.join(root, path.basename(launcherExe));
    fs.copyFileSync(launcherExe, exe);
    fs.copyFileSync(fakeApp, path.join(root, 'app', appName));
    if (process.platform !== 'win32') {
      fs.chmodSync(exe, 0o755);
      fs.chmodSync(path.join(root, 'app', appName), 0o755);
    }
    return { root, exe, app: path.join(root, 'app') };
  };

  /** Run the launcher with the fake app in `mode`; resolve with how long the launcher ran, in ms. */
  const runScreen = (box, mode) => new Promise((resolve, reject) => {
    const started = Date.now();
    const child = spawn(box.exe, [], { cwd: box.root, env: { ...process.env, SHENORA_FAKE_APP: mode }, stdio: 'ignore' });
    const timer = setTimeout(() => { child.kill(); reject(new Error('the launcher did not exit within 20 s')); }, 20000);
    child.on('error', (e) => { clearTimeout(timer); reject(e); });
    child.on('exit', (code) => { clearTimeout(timer); resolve({ code, ms: Date.now() - started }); });
  });
  const read = (file) => (fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '');
  const argsOf = (box) => read(path.join(box.app, 'args.txt')).split(/\r?\n/).filter(Boolean);
  const until = async (ok, ms) => { for (let t = 0; t < ms && !ok(); t += 100) await new Promise((r) => setTimeout(r, 100)); };

  /** A launcher's frame, from --startup-screen-dump: w, h, then premultiplied 0xAARRGGBB, top row first. */
  const dump = (launcherExe, phase) => {
    const file = path.join(os.tmpdir(), `shenora-dump-${process.pid}-${path.basename(launcherExe)}.bin`);
    execFileSync(launcherExe, ['--startup-screen-dump', file, String(phase)]);
    const buf = fs.readFileSync(file);
    fs.rmSync(file, { force: true });
    const w = buf.readInt32LE(0), h = buf.readInt32LE(4);
    return { w, h, px: (x, y) => buf.readUInt32LE(8 + (y * w + x) * 4) };
  };
  const hex = (v) => '0x' + (v >>> 0).toString(16).padStart(8, '0');

  test('screen: the app is passed --startup-screen, the screen is up when it starts, and its close ends the launcher', async () => {
    const box = screenBox(screenLauncher);
    const { ms } = await runScreen(box, 'close');
    const args = argsOf(box);
    const at = args.indexOf('--startup-screen');
    assert(at >= 0 && /^\d+$/.test(args[at + 1] ?? ''), `no --startup-screen <id> in ${JSON.stringify(args)}`);
    assert(read(path.join(box.app, 'seen.txt')).includes('visible=1'), 'the screen was not visible when the app started');
    assert(ms < 2500, `the launcher ran ${ms} ms: its screen's close did not end it before the 3 s timeout`);
    assert(read(path.join(box.root, 'launcher.log')).includes('startup screen closed by the app'), 'launcher.log does not say the app closed it');
  });

  test('screen: an app that exits at once (a second launch handed over) ends it', async () => {
    const box = screenBox(screenLauncher);
    const { ms } = await runScreen(box, 'exit');
    assert(ms < 2500, `the launcher outlived the app by ${ms} ms`);
  });

  test('screen: an app that never closes it is outlived only by the timeout', async () => {
    const box = screenBox(screenLauncher);
    try {
      const { ms } = await runScreen(box, 'stay');
      assert(ms >= 2800 && ms < 8000, `the launcher exited after ${ms} ms, not at its 3 s timeout`);
    } finally {
      fs.writeFileSync(path.join(box.app, 'stop.txt'), '');
    }
  });

  // X11 only: Windows cannot destroy another process's window, and its WM_DESTROY is handled anyway. Xlib's default
  // error handler EXITS the process, so a launcher that kept drawing to a destroyed window died, before or after
  // starting the app.
  if (process.platform !== 'win32') {
    test('screen: a window destroyed from outside ends the screen, not the launch', async () => {
      const box = screenBox(screenLauncher);
      const { code, ms } = await runScreen(box, 'destroy');
      assert(code === 0, `the launcher exited ${code} after its window was destroyed`);
      assert(ms < 2500, `the launcher ran ${ms} ms after its window was destroyed`);
      assert(/startup screen (closed|ended)/.test(read(path.join(box.root, 'launcher.log'))), 'launcher.log says nothing of the screen');
    });
  }

  // Beside the screen launcher, built by the same SHENORA_LAUNCHER_TESTS=ON build.
  const besideScreen = (name) => path.join(path.dirname(screenLauncher), name + (process.platform === 'win32' ? '.exe' : ''));
  const stripesLauncher = besideScreen('shenora-launcher-stripes');
  const corruptLauncher = besideScreen('shenora-launcher-corrupt');

  test('screen: a fractional shrink weighs each source pixel by how much of it a pixel covers', () => {
    // 3x1 red, green, blue into 2x1: the left pixel covers all of red and half of green, the right half of green and
    // all of blue, so 2:1 weights — not the 1:1 a whole-pixel box gives.
    const { w, h, px } = dump(stripesLauncher, 0);
    assert(w === 2 && h === 1, `frame ${w}x${h}`);
    assert(px(0, 0) === 0xffaa5500, `the left pixel ${hex(px(0, 0))}, not 0xffaa5500`);
    assert(px(1, 0) === 0xff0055aa, `the right pixel ${hex(px(1, 0))}, not 0xff0055aa`);
  });

  test('screen: a PNG that does not decode says why, and the app still starts with no argument', async () => {
    const box = screenBox(corruptLauncher);
    await runScreen(box, 'exit');
    const file = path.join(box.app, 'args.txt');
    await until(() => fs.existsSync(file), 5000);
    assert(fs.existsSync(file), 'the app never started');
    assert(!argsOf(box).includes('--startup-screen'), 'a launcher with no screen passed --startup-screen');
    const log = read(path.join(box.root, 'launcher.log'));
    assert(/would not decode: \S/.test(log), `launcher.log gives no reason: ${JSON.stringify(log.trim())}`);
  });

  if (process.platform !== 'win32') {
    test('screen: a killed X connection ends the screen, not the launch', async () => {
      const box = screenBox(screenLauncher);
      const { code, ms } = await runScreen(box, 'kill');
      assert(code === 0, `the launcher exited ${code} after its X connection was killed`);
      assert(ms < 2500, `the launcher ran ${ms} ms after its X connection was killed`);
    });
  }

  // Windows only: the launcher is a GUI-subsystem program, so in a terminal its stdout is no console at all — which the
  // pipes the apply cases read hide. The console host runs it as a shell does and reads the console back.
  if (process.platform === 'win32') {
    test('--apply-and-exit prints its line to the terminal it was started from', (box) => {
      const host = besideScreen('shenora-console-host');
      assert(fs.existsSync(host), `no console host at ${host} — build with -DSHENORA_LAUNCHER_TESTS=ON`);
      const out = path.join(box.root, 'console.txt');
      // Hidden, with no stdio: libuv then starts the host with a windowless console of its own, so a run from a
      // terminal never flips that terminal's screen. The host's own failures land in `out`, named.
      try {
        execFileSync(host, [out, box.exe, '--apply-and-exit'], { windowsHide: true, stdio: 'ignore' });
      } catch { /* reported from `out` below */ }
      const text = read(out);
      assert(/applied=0 attempted=0/.test(text), `the terminal showed ${JSON.stringify(text.trim())}`);
    });
  }

  test('screen: a launcher built with no screen passes no argument', async () => {
    const box = screenBox(launcher);
    await runScreen(box, 'exit');
    // The stock launcher does not wait for the app: give it a moment to write what it was passed.
    const file = path.join(box.app, 'args.txt');
    await until(() => fs.existsSync(file), 5000);
    assert(fs.existsSync(file), 'the app never started');
    assert(!argsOf(box).includes('--startup-screen'), 'the stock launcher passed --startup-screen');
  });

  test('screen: the frame — rounded corners, the background, the image centred, the bar', () => {
    const { w, h, px } = dump(screenLauncher, 0.5);
    assert(w === 200 && h === 100, `frame ${w}x${h}`);
    assert(px(0, 0) >>> 24 === 0, `a rounded corner is opaque: ${hex(px(0, 0))}`);
    assert(px(4, 50) === 0xff102030, `the background ${hex(px(4, 50))}`);
    assert(px(100, 50) === 0xff00ff00, `the image's centre ${hex(px(100, 50))}`);   // 100x100 green fits 100x100, centred
    assert(px(40, 50) === 0xff102030, `left of the image ${hex(px(40, 50))}`);
    assert(px(100, 99) === 0xffff0000, `the bar at phase 0.5 ${hex(px(100, 99))}`);
  });

  test('screen: a wide image keeps its aspect (square corners, no bar)', () => {
    const { px } = dump(wideLauncher, 0.5);
    assert(px(0, 0) === 0xff102030, `a square corner is not the background: ${hex(px(0, 0))}`);
    assert(px(100, 50) === 0xff0000ff, `the 4:1 image is not across the middle: ${hex(px(100, 50))}`);   // 200x50 at 200 wide
    assert(px(100, 10) === 0xff102030, `above the 4:1 image is not the background: ${hex(px(100, 10))}`);
    assert(px(100, 99) === 0xff102030, `a launcher with no bar drew one: ${hex(px(100, 99))}`);
  });
}

// ── Run ─────────────────────────────────────────────────────────────────────────────────────────────

console.log(`launcher : ${launcher}`);
console.log(`probe    : ${probe}\n`);
for (const [name, fn] of cases) {
  const box = sandbox();
  try {
    await fn(box);
    console.log(`  ok    ${name}`);
  } catch (e) {
    failures++;
    console.error(`  FAIL  ${name}\n        ${e.message}`);
  } finally {
    try { fs.rmSync(box.root, { recursive: true, force: true }); } catch { /* best effort */ }
  }
}

// The screen cases' sandboxes, once their fake apps have let go of their executables.
for (let attempt = 0; attempt < 50 && screenRoots.length; attempt++) {
  for (const root of [...screenRoots]) {
    try { fs.rmSync(root, { recursive: true, force: true }); screenRoots.splice(screenRoots.indexOf(root), 1); } catch { /* still held */ }
  }
  if (screenRoots.length) await new Promise((r) => setTimeout(r, 200));
}

console.log(failures === 0
  ? `\nlauncher-conformance: ${cases.length} case(s) PASSED against a prebuilt binary`
  : `\nlauncher-conformance: ${failures} of ${cases.length} FAILED`);
process.exitCode = failures === 0 ? 0 : 1;
