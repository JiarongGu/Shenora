// Drive a Mac over SSH to build and run the MAUI sample on iOS — the one target that cannot be built
// on this dev machine at all, because an iOS build requires Xcode and Xcode requires macOS.
//
//   node devtools/dev.mjs mac doctor         is the Mac reachable, and does it have what a .NET iOS build needs?
//   node devtools/dev.mjs mac setup          one-time: create the bare repo + working clone on the Mac
//   node devtools/dev.mjs mac push           push the current branch and reset the Mac's clone to it
//   node devtools/dev.mjs mac build          push, then `dotnet build -f net10.0-ios` for the simulator
//   node devtools/dev.mjs mac run            build, boot the simulator, install + launch
//   node devtools/dev.mjs mac shot [name]    screenshot the booted simulator, copy the PNG back here
//   node devtools/dev.mjs mac tap <x> <y>    tap it, in the NATIVE pixels of that screenshot
//   node devtools/dev.mjs mac type <text…>   type into whatever has focus
//   node devtools/dev.mjs mac swipe <x1> <y1> <x2> <y2>   drag/scroll, same coordinate space
//   node devtools/dev.mjs mac safari-eval <js…>           run JS in the page and print the VALUE
//   node devtools/dev.mjs mac mirror [port]  live view on the LAN; click to tap, scroll to swipe
//   node devtools/dev.mjs mac log [-n N]     the sample's own log lines from the simulator
//   node devtools/dev.mjs mac devices        iPhones connected to the Mac
//   node devtools/dev.mjs mac device         build, SIGN, install and launch on a real iPhone
//   node devtools/dev.mjs mac device-log     the app's log lines from the device
//   node devtools/dev.mjs mac provision [<bundle-id>…]  mint provisioning profiles the kit owns (see below)
//   node devtools/dev.mjs mac profiles       what profiles this Mac has, and when they expire
//   node devtools/dev.mjs mac awake [on|off] stop the Mac sleeping/locking while it is a build machine
//   node devtools/dev.mjs mac ssh <command…> run anything on the Mac (escape hatch)
//
// PORTED from the public sibling Sonora's `devtools/scripts/mac.mjs`, keeping its post-mortems —
// they were paid for once and none of them are about Capacitor. What changed is the BUILD step only:
// that project builds a web client and an Xcode project, this one runs `dotnet build -f net10.0-ios`.
// Everything around it — the bare-repo push, the refuse-when-dirty rule, the screenshot round trip,
// the two-conversion tap geometry — is the same problem on both sides.
//
// Why SSH is enough: everything Xcode does has a command-line form (xcodebuild, xcrun simctl), so
// there is no GUI, no screen sharing and no agent on the Mac. Which also means the loop is scriptable
// end to end: build, launch on a simulator, screenshot, copy the PNG back and LOOK at it. That last
// step is the point. Verifying a UI change by reading the code that produced it is how a session
// convinces itself of things that are not true.
//
// The Mac's address, user and paths live in local/mac.json, which is gitignored — this file is public.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const CONFIG = path.join(repo, 'local', 'mac.json');
const OUT = path.join(repo, 'devtools', '_mac');   // build logs + screenshots (gitignored via _*)

const DEFAULTS = {
  user: '',
  host: '',
  // The ssh key. Named in config rather than hardcoded because the family already has ONE authorized
  // key for this Mac — making a second repo mint its own would mean provisioning it again for no gain.
  key: path.join(os.homedir(), '.ssh', 'shenora_mac'),
  // Where the Mac keeps the push target and the checkout it builds from.
  bare: 'shenora.git',
  work: 'Shenora',
  // A simulator that exists on a current Xcode. `mac doctor` lists what is actually installed and
  // fails if this one is not among them.
  simulator: 'iPhone 16 Pro',
  project: 'samples/Shenora.Sample.Maui/Shenora.Sample.Maui.csproj',
  tfm: 'net10.0-ios',
  bundleId: 'com.shenora.sample.maui',
  // Set true when this Mac's Xcode is OLDER than the iOS workload wants. It costs two flags, and
  // both are needed — finding that out took four builds, so the order is recorded here:
  //
  //   1. `-p:ValidateXcodeVersion=false` clears the up-front gate (`_ValidateXcodeVersion` in
  //      Xamarin.Shared.Sdk.targets — an EQUALITY check on major.minor, so a newer Xcode is refused
  //      too). On its own it only gets you as far as the linker.
  //   2. `-p:MtouchLink=SdkOnly` clears MT0180, raised by the ILLink Setup step, which independently
  //      checks that Xcode ships the iOS SDK headers this Microsoft.iOS was built against. This is
  //      the mode MT0180's own message recommends ("Link Framework SDKs Only ... to try to avoid the
  //      new APIs"), and it is the reason this works: the app's own assemblies stop being trimmed
  //      against headers the machine does not have.
  //
  // What does NOT work, so nobody retries it: `-p:PublishTrimmed=false` is rejected outright ("iOS
  // projects must build with PublishTrimmed=true"), and `MtouchLink=None` still fails MT0180 because
  // the Setup step runs before the mode is honoured.
  //
  // This lives in local/mac.json rather than the csproj on purpose: which Xcode a machine happens to
  // have is a fact about THAT MACHINE, and burying the override in tracked build files would silence
  // it for everyone, permanently — including the case where the mismatch is the real problem.
  //
  // ⚠ Verified for SIMULATOR DEBUG only. The honest fix is to match the pair (upgrade Xcode, or
  // install a workload band built against the Xcode you have); this is a dev-loop unblock, not a
  // shipping configuration.
  skipXcodeVersionCheck: false,
};

function config() {
  if (!fs.existsSync(CONFIG)) {
    console.error(`No ${path.relative(repo, CONFIG)}.

Create it (it is gitignored — the Mac's address must not land in the public repo):

  {
    "user": "<your mac username>",
    "host": "<mac-name>.local",
    "key": "C:\\\\Users\\\\<you>\\\\.ssh\\\\<key>"
  }

Then: node devtools/dev.mjs mac doctor`);
    process.exit(1);
  }
  const cfg = { ...DEFAULTS, ...JSON.parse(fs.readFileSync(CONFIG, 'utf8')) };
  if (!cfg.user || !cfg.host) { console.error(`${CONFIG}: "user" and "host" are required.`); process.exit(1); }
  return cfg;
}

const q = (s) => `'${String(s).replace(/'/g, `'\\''`)}'`;   // single-quote for the remote shell

/** Run something on the Mac. Returns {status, stdout, stderr}; `inherit` streams instead of capturing. */
/**
 * Run a script in the Mac's GUI LOGIN SESSION rather than over ssh, and wait for it.
 *
 * 🔴 This exists for exactly one reason and it is not convenience: **codesign cannot use a login-keychain
 * key from an ssh session.** An ssh login is a different AUDIT SESSION, so any signing attempt dies with
 * `errSecInternalComponent` — proven here 2026-08-06 by signing a copy of `/bin/echo`, which has nothing to
 * do with this repo and failed identically. That wall is what stopped every device build; a simulator build
 * signs ad-hoc and never meets it.
 *
 * The way through is to ask the GUI session to run the command: `osascript` tells Terminal.app to run a
 * script, Terminal is already in the user's session, and the keychain opens. Ported from the sibling repo
 * that solved this first (D15) — the technique is theirs, and the comment above is the part worth carrying.
 *
 * ⚠ Its output CANNOT be streamed back: the script is detached in another session, so it writes a log and an
 * exit-code file and this polls for them. A script that never finishes leaves no `.done` and hits the
 * timeout — which is why the timeout is generous and the log is returned either way.
 *
 * ⚠ The script is shipped base64-encoded. It crosses ssh's own shell, then a login shell, then AppleScript's
 * string parser, and every one of those would otherwise eat a quote or expand a `$`.
 */
async function guiRun(cfg, script, { tag = 'run', timeoutMs = 20 * 60_000 } = {}) {
  const sh = `/tmp/shenora-gui-${tag}.sh`;
  const log = `/tmp/shenora-gui-${tag}.log`;
  const done = `/tmp/shenora-gui-${tag}.done`;

  // `exit` at the end so the Terminal window closes itself; without it they accumulate one per run.
  const body = `#!/bin/bash\n{\n${script}\n} > ${log} 2>&1\necho $? > ${done}\nexit\n`;
  const encoded = Buffer.from(body, 'utf8').toString('base64');

  ssh(cfg, `rm -f ${done} ${log}`, { check: true });
  ssh(cfg, `printf %s ${q(encoded)} | base64 -d > ${sh} && chmod +x ${sh}`, { check: true });
  ssh(cfg, `osascript -e ${q(`tell application "Terminal" to do script "bash ${sh}"`)} >/dev/null`,
    { check: true });

  const started = Date.now();
  let ticks = 0;
  while (Date.now() - started < timeoutMs) {
    await new Promise(resolve => setTimeout(resolve, 5000));
    const code = (ssh(cfg, `cat ${done} 2>/dev/null`).stdout ?? '').trim();
    if (code !== '') {
      process.stdout.write('\n');
      return { status: Number(code), log: (ssh(cfg, `cat ${log} 2>/dev/null`).stdout ?? '') };
    }
    if (++ticks % 6 === 0) process.stdout.write(`  …${Math.round((Date.now() - started) / 1000)}s\n`);
    else process.stdout.write('.');
  }
  return {
    status: 1,
    log: (ssh(cfg, `cat ${log} 2>/dev/null`).stdout ?? '') + `\n\n(timed out after ${timeoutMs / 1000}s)`,
  };
}

function ssh(cfg, command, { inherit = false, check = false } = {}) {
  const args = [
    '-i', cfg.key,
    '-o', 'BatchMode=yes',              // never prompt: this runs unattended, and a prompt would just hang
    '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ConnectTimeout=10',
    `${cfg.user}@${cfg.host}`,
    // A login shell, so the Mac's PATH (Homebrew, /usr/local/share/dotnet, xcode-select) is what an
    // interactive session would see. Without -l, a Homebrew or pkg-installed `dotnet` is simply not found.
    //
    // SINGLE-quoted, not JSON.stringify'd. ssh concatenates its arguments and hands the result to the
    // remote LOGIN shell, which parses it before `bash -lc` ever runs — so a double-quoted command has
    // every `$var` expanded by that outer shell first, against its own (empty) environment. A probe that
    // set X and used $X silently read an empty string and reported a missing file. Single quotes survive
    // both parses.
    `bash -lc ${q(command)}`,
  ];
  const r = spawnSync('ssh', args, { encoding: 'utf8', stdio: inherit ? 'inherit' : 'pipe' });
  if (check && r.status !== 0) {
    console.error(`\nmac: command failed (exit ${r.status})\n  ${command}\n${r.stderr ?? ''}`);
    process.exit(r.status ?? 1);
  }
  return r;
}

function localRun(exe, argv, opts = {}) {
  return spawnSync(exe, argv, { cwd: repo, encoding: 'utf8', stdio: 'pipe', ...opts });
}

// ---------------------------------------------------------------- the persistent ssh worker
//
// A fresh ssh connection to this Mac costs ~1.8 s; a simulator screenshot costs ~322 ms. So anything
// that runs more than a couple of remote commands spends most of its wall clock in connection setup —
// and `tap` alone runs three (activate, probe for cliclick, click). The mirror below would be unusable.
//
// ⚠ The obvious fix does NOT work here: `ControlMaster` multiplexing does not exist on the Windows ssh
// client, which fails with *"Failed to connect to new control master"*. So hold ONE `bash -s` open and
// feed it commands on stdin, delimiting each result with a sentinel.
//
// Ported from the public sibling Sonora, where it was measured.
let worker = null;
const SENTINEL = '\n__SHENORA_DONE__\n';

function openWorker(cfg) {
  const proc = spawn('ssh', [
    '-i', cfg.key, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ConnectTimeout=10', '-o', 'ServerAliveInterval=30',
    // ⚠ `-l` (LOGIN shell) is load-bearing and the donor does not have it. `ssh()` above uses `bash -lc`
    // so Homebrew is on PATH there; a plain `bash -s` here does NOT get the login profile, so
    // `command -v cliclick` answers NO over the worker and YES over ssh — for the same Mac, with
    // cliclick installed. The damage is silent: `tap` reads that NO and quietly falls back to System
    // Events, which the donor's own notes record as landing only a FOCUS on some web controls. So a
    // capability probe has to run in the same shell as the command it is gating. Found live: `swipe`
    // refused with "cliclick is not installed" seconds after a direct check found it.
    `${cfg.user}@${cfg.host}`, 'bash -l -s',
  ], { stdio: ['pipe', 'pipe', 'pipe'] });
  proc.stdout.setEncoding('utf8');
  proc.stderr.setEncoding('utf8');
  worker = { proc, buf: '', queue: [], dead: false };
  proc.stdout.on('data', (chunk) => {
    worker.buf += chunk;
    let i;
    while ((i = worker.buf.indexOf(SENTINEL)) !== -1) {
      const out = worker.buf.slice(0, i);
      worker.buf = worker.buf.slice(i + SENTINEL.length);
      worker.queue.shift()?.({ status: 0, stdout: out, stderr: '' });
    }
  });
  // A dead worker must RESOLVE every waiter, not leave them pending: an unresolved promise here hangs
  // the whole command with no error, which is the worst way for a lost connection to present.
  const die = () => {
    worker.dead = true;
    while (worker.queue.length) worker.queue.shift()({ status: 1, stdout: '', stderr: 'ssh worker closed' });
  };
  proc.on('exit', die);
  proc.on('error', die);
  return worker;
}

/** Run a command on the Mac over the persistent worker when one is open, else a fresh ssh. */
function sh(cfg, command) {
  if (!worker || worker.dead) return Promise.resolve(ssh(cfg, command));
  return new Promise((resolve) => {
    worker.queue.push(resolve);
    // `printf`, not `echo`: the sentinel must still be emitted when the command left the shell mid-line.
    worker.proc.stdin.write(`{ ${command} ; } 2>/dev/null; printf '%s' ${q(SENTINEL)}\n`);
  });
}

function closeWorker() {
  if (worker && !worker.dead) { try { worker.proc.stdin.end(); } catch { /* already gone */ } }
}

function scpFrom(cfg, remotePath, localPath) {
  return spawnSync('scp', ['-i', cfg.key, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=accept-new',
    `${cfg.user}@${cfg.host}:${remotePath}`, localPath], { encoding: 'utf8' });
}

// ---------------------------------------------------------------- doctor
//
// This is the command that matters before anything else works, and on a Mac that has never built .NET
// it is the whole story: Xcode can be perfect and the build still impossible. It reports every
// prerequisite rather than stopping at the first, because "install these three things" is one trip to
// the machine and three sequential failures are three.
function doctor(cfg) {
  console.log(`mac: ${cfg.user}@${cfg.host}\n`);
  if (!fs.existsSync(cfg.key)) {
    console.error(`No key at ${cfg.key}. Generate one and authorise it on the Mac:\n`
      + `  ssh-keygen -t ed25519 -f "${cfg.key}" -N ""\n`
      + `  (then append ${cfg.key}.pub to ~/.ssh/authorized_keys there)\n\n`
      + `Or point "key" in local/mac.json at a key the Mac already accepts.`);
    process.exit(1);
  }
  const hello = ssh(cfg, 'echo ok');
  if (hello.status !== 0) {
    console.error(`Cannot reach the Mac over SSH.\n${hello.stderr?.trim() ?? ''}\n
Check, in order:
  1. Remote Login is on         (macOS: System Settings -> General -> Sharing -> Remote Login)
  2. this key is authorised     (append ${cfg.key}.pub to ~/.ssh/authorized_keys on the Mac)
  3. the host resolves          (try: ping ${cfg.host})`);
    process.exit(1);
  }
  console.log('  ssh                 ok');

  let missing = 0;
  for (const [name, cmd] of [
    ['xcode', 'xcodebuild -version | head -1'],
    ['xcode-select', 'xcode-select -p'],
    ['git', 'git --version'],
    ['arch', 'uname -m'],
    ['macos', 'sw_vers -productVersion'],
    ['free disk', 'df -h / | tail -1 | awk "{print \\$4}"'],
  ]) {
    const r = ssh(cfg, cmd);
    const line = (r.stdout || r.stderr || '').trim().split('\n')[0];
    console.log(`  ${name.padEnd(19)} ${r.status === 0 ? line : 'MISSING — ' + line}`);
    if (r.status !== 0) missing++;
  }

  // The licence gate is its own failure: xcodebuild exists and still refuses every build until it is accepted.
  const lic = ssh(cfg, 'xcodebuild -checkFirstLaunchStatus');
  console.log(`  xcode first-launch  ${lic.status === 0 ? 'ok' : 'NOT DONE — run: sudo xcodebuild -runFirstLaunch'}`);

  // ---- the .NET half, which is what distinguishes this from the sibling's Capacitor harness.
  //
  // Parse the MAJOR version rather than trusting `dotnet` to exist. A Mac can carry a pile of ancient
  // SDKs (2.1/3.1/5.0 were found on this one) and still have no .NET 10 — and on those, `dotnet
  // workload` is not merely empty, it is NOT A COMMAND (workloads arrived in .NET 6), so probing
  // workloads first reports a confusing parse error instead of the real gap.
  const sdks = ssh(cfg, 'dotnet --list-sdks 2>/dev/null || true');
  const majors = (sdks.stdout ?? '').split('\n')
    .map((l) => Number(l.trim().split('.')[0]))
    .filter(Number.isFinite);
  const newest = majors.length ? Math.max(...majors) : 0;
  const wanted = Number(cfg.tfm.replace(/^net/, '').split('.')[0]) || 10;
  if (newest >= wanted) {
    console.log(`  dotnet sdk          ${newest}.x ok`);
    const wl = ssh(cfg, 'dotnet workload list 2>/dev/null || true');
    const text = (wl.stdout ?? '').toLowerCase();
    const hasIos = text.includes('ios');
    console.log(`  ios workload        ${hasIos ? 'ok' : 'MISSING — run on the Mac: dotnet workload install ios'}`);
    if (!hasIos) missing++;
  } else {
    console.log(`  dotnet sdk          ${newest ? `${newest}.x — TOO OLD` : 'MISSING'}, need ${wanted}.x for ${cfg.tfm}`);
    console.log('  ios workload        (cannot check — needs a .NET 6+ SDK for `dotnet workload` to exist)');
    missing++;
  }

  // Group by runtime and DO NOT truncate blindly. A `head -8` here once hid an entire iOS 26 runtime on
  // the sibling, which made a freshly-installed Xcode look like it only had iPhone 15s — and sent the
  // next twenty minutes in the wrong direction.
  const sims = ssh(cfg, 'xcrun simctl list devices available | grep -E "^-- |iPhone|iPad"');
  const simList = (sims.stdout ?? '').trim();
  console.log(`\n  available simulators:\n${simList.split('\n').map((l) => '    ' + l.trim()).join('\n')}`);

  // The configured simulator must actually EXIST, or every build fails on an unhelpful destination
  // error. Reporting "ready" while it does not is the check lying about the one thing it was asked to
  // verify — which is what happened on the sibling with a default of "iPhone 16" on a Mac without one.
  const hasSim = simList.split('\n').some((l) => l.trim().startsWith(`${cfg.simulator} (`));
  console.log(`\n  configured simulator: ${cfg.simulator}  ${hasSim ? 'ok' : '<- NOT INSTALLED'}`);
  if (!hasSim) {
    console.error(`
Set "simulator" in local/mac.json to one listed above, or create it (a device TYPE can exist while no
device has been made from it):
  xcrun simctl list devicetypes | grep -i iphone
  xcrun simctl create ${JSON.stringify(cfg.simulator)} <device-type-id> <runtime-id>`);
    missing++;
  }

  if (missing) { console.error(`\n${missing} prerequisite(s) missing on the Mac.`); process.exit(1); }
  console.log('\nmac: ready');
}

// ---------------------------------------------------------------- setup
function setup(cfg) {
  console.log('mac: creating the push target + working clone…');
  // A BARE repo is the push target: you cannot push to a checked-out branch of a normal clone. The
  // working clone then resets to it, so the Mac's tree always matches exactly what was pushed — no
  // merge, no drift.
  ssh(cfg, `set -e
    mkdir -p ~/${cfg.bare}
    if [ ! -d ~/${cfg.bare}/refs ]; then git init --bare ~/${cfg.bare} >/dev/null; fi
    if [ ! -d ~/${cfg.work}/.git ]; then git clone ~/${cfg.bare} ~/${cfg.work} 2>/dev/null || true; fi
    echo "bare: ~/${cfg.bare}"; echo "work: ~/${cfg.work}"`, { inherit: true, check: true });

  const url = `ssh://${cfg.user}@${cfg.host}/~/${cfg.bare}`;
  const exists = localRun('git', ['remote']).stdout.split('\n').map((l) => l.trim()).includes('mac');
  localRun('git', ['remote', exists ? 'set-url' : 'add', 'mac', url]);
  console.log(`\nlocal git remote 'mac' -> ${url}`);
  console.log('\nnext: node devtools/dev.mjs mac build');
}

// ---------------------------------------------------------------- push
function push(cfg) {
  const branch = localRun('git', ['rev-parse', '--abbrev-ref', 'HEAD']).stdout.trim();
  // The Mac builds COMMITTED work, so an uncommitted fix never reaches it: the build reproduces the
  // very error you just fixed and the obvious conclusion — "the fix did not work" — is wrong. On the
  // sibling this started as a WARNING and that was not enough; it scrolled past twice in one session,
  // once behind a `grep` on the output, and cost two rounds of drawing conclusions about code the Mac
  // had never seen. A wrong answer delivered confidently is worse than a stopped command, so it REFUSES.
  const dirty = localRun('git', ['status', '--porcelain']).stdout.trim();
  if (dirty && !process.argv.includes('--allow-dirty')) {
    const lines = dirty.split('\n');
    console.error(`mac: REFUSING to push — ${lines.length} uncommitted change(s). The Mac builds HEAD, so these
would NOT be in the build, and its result would not be about your current code:\n`);
    console.error(lines.slice(0, 12).map((l) => '        ' + l).join('\n'));
    if (lines.length > 12) console.error(`        …and ${lines.length - 12} more`);
    console.error('\n  Commit them, or re-run with --allow-dirty to build HEAD regardless.');
    process.exit(1);
  }
  if (dirty) console.log(`mac: --allow-dirty — building HEAD, ignoring ${dirty.split('\n').length} local change(s)`);
  console.log(`mac: pushing ${branch}…`);
  const p = spawnSync('git', ['push', '--force-with-lease', 'mac', `${branch}:refs/heads/${branch}`], {
    cwd: repo,
    stdio: 'inherit',
    env: { ...process.env, GIT_SSH_COMMAND: `ssh -i "${cfg.key}" -o BatchMode=yes -o StrictHostKeyChecking=accept-new` },
  });
  if (p.status !== 0) { console.error('mac: push failed'); process.exit(p.status ?? 1); }
  // Reset, not merge, so a half-finished local state cannot be silently combined with whatever the Mac
  // had last time.
  //
  // `-f`, and it is load-bearing rather than defensive — the version WITHOUT it (which is what the
  // sibling has) aborts with "Your local changes would be overwritten by checkout" the moment
  // anything has touched the Mac's tree, and then the push has already landed while the checkout has
  // not, leaving the clone silently behind the branch it reports. Hit immediately: a file was scp'd
  // there to trial a fix before committing it.
  //
  // Discarding is correct here in a way it would never be locally: this clone is a BUILD SCRATCH
  // AREA created by `mac setup`, nobody edits in it, and the whole contract of this command is that
  // the Mac's tree equals what was just pushed. Deliberately NOT `git clean` — that would delete
  // bin/obj and turn every build into a cold one.
  ssh(cfg, `set -e; cd ~/${cfg.work}; git fetch origin ${q(branch)}; git checkout -f -B ${q(branch)} FETCH_HEAD; git log --oneline -1`,
    { inherit: true, check: true });
}

// ---------------------------------------------------------------- build
//
// The simulator RID follows the MAC's architecture, not this machine's: iossimulator-x64 on an Intel
// Mac, iossimulator-arm64 on Apple Silicon. Getting it wrong is the iOS twin of the Android
// INSTALL_FAILED_NO_MATCHING_ABIS trap already recorded in android.mjs — the build succeeds and the
// install is what fails, so the error names the wrong step. Asked, never assumed.
function simulatorRid(cfg) {
  const arch = ssh(cfg, 'uname -m', { check: true }).stdout.trim();
  return arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64';
}

// The npm package is a BUILD ARTIFACT and `dist/` is gitignored, so pushing the branch does not
// carry it. Without this the sample silently falls back to its inline transport and logs
// "INLINE fallback" — which is the sample behaving as designed, and a WEAKER proof than the Android
// run, where `dev.mjs verify` had already built dist/ on the same machine. Seen on the first iOS run.
// Skipped, with a reason, when the Mac has no node rather than failing the whole build over it.
function buildClientPackage(cfg) {
  const hasNode = ssh(cfg, 'command -v npm >/dev/null 2>&1 && echo YES || echo NO');
  if (!(hasNode.stdout ?? '').trim().startsWith('YES')) {
    console.log('mac: no npm on the Mac — skipping the client build; the page will use its inline transport.');
    return;
  }
  console.log('mac: building @shenora/react so the sample uses the SHIPPED transport…');
  const r = ssh(cfg, `set -e -o pipefail
    cd ~/${cfg.work}/src/Shenora.React
    npm install --no-audit --no-fund --silent
    npm run build 2>&1 | tail -5`);
  if (r.status !== 0) {
    console.log(`mac: client build failed — continuing with the inline fallback.\n${(r.stderr ?? r.stdout ?? '').trim().split('\n').slice(-5).join('\n')}`);
  }
}

function build(cfg, { skipPush = false } = {}) {
  if (!skipPush) push(cfg);
  fs.mkdirSync(OUT, { recursive: true });
  buildClientPackage(cfg);
  const rid = simulatorRid(cfg);
  const skipXcode = cfg.skipXcodeVersionCheck
    ? ' -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly'
    : '';
  if (skipXcode) {
    console.log('mac: ⚠ skipXcodeVersionCheck (local/mac.json) — this Mac\'s Xcode is older than the iOS\n'
      + '     workload wants, so the version gate and full trimming are both off.\n'
      + '     Verified for SIMULATOR DEBUG builds only; not a shipping configuration.');
  }
  console.log(`\nmac: dotnet build ${cfg.tfm} (${rid})…`);
  // -o pipefail because the build is piped through `tail`: without it the pipeline reports TAIL's exit
  // status, which is always 0, so a failed build sails through and the next step tries to install a
  // binary that was never produced — reporting a confusing second error instead of the real one.
  const r = ssh(cfg, `set -e -o pipefail
    cd ~/${cfg.work}
    dotnet build ${q(cfg.project)} -c Debug -f ${q(cfg.tfm)} -p:RuntimeIdentifier=${q(rid)}${skipXcode} 2>&1 | tail -60`);
  fs.writeFileSync(path.join(OUT, 'build.log'), (r.stdout ?? '') + (r.stderr ?? ''));
  console.log((r.stdout ?? '').split('\n').slice(-30).join('\n'));
  if (r.status !== 0) {
    console.error('\nmac: BUILD FAILED — full output: devtools/_mac/build.log');
    process.exit(r.status ?? 1);
  }
  console.log('\nmac: build ok');

  // ⚠ THE SECOND CONFIGURATION, and it exists because not building it shipped a broken package.
  //
  // Shenora.iOS 0.9.0 could not be linked by any app that did not opt into the Live Activity devkit —
  // [DllImport("__Internal")] is resolved at link time, and the shim was only built when
  // ShenoraLiveActivityViews was set. The repo never saw it because this sample IS opted in: the single
  // configuration that worked was the only one ever built. Found by the first adopter, not by the gate.
  //
  // So the gate now builds the OTHER one. It is a link check, not a run — cheap, and it is exactly the
  // failure that got through. Non-fatal on its own line so the reason is unmistakable when it breaks.
  console.log('\nmac: link check — the same sample WITHOUT the Live Activity opt-in…');
  // ⚠ The shim output and the app bundle are DELETED first, and that is what makes this a real check.
  // Without it the .a left by the build above satisfies the link, and the gate passes a sabotage — measured:
  // re-gating the shim on the opt-in (the exact 0.9.0 defect) still reported "link check ok". The question
  // is whether the kit PRODUCES what it needs, not whether something happens to be lying around.
  const optOut = ssh(cfg, `set -e -o pipefail
    cd ~/${cfg.work}
    rm -rf ${q(`${cfg.project.replace(/\/[^/]+\.csproj$/, '')}/shenora-liveactivity`)}
    rm -rf ${q(`${cfg.project.replace(/\/[^/]+\.csproj$/, '')}/bin/Debug/${cfg.tfm}/${rid}`)}
    dotnet build ${q(cfg.project)} -c Debug -f ${q(cfg.tfm)} -p:RuntimeIdentifier=${q(rid)}${skipXcode} \
      -p:ShenoraLiveActivityViews= 2>&1 | tail -25`);
  if (optOut.status !== 0) {
    console.error('\nmac: LINK CHECK FAILED — an app that does NOT enable the devkit cannot build.\n'
      + 'That is the 0.9.0 defect: the kit must link for consumers who never use the feature.\n'
      + (optOut.stdout ?? '').split('\n').slice(-12).join('\n'));
    process.exit(optOut.status ?? 1);
  }
  console.log('mac: link check ok — the kit links without the devkit too');

  // Leave the tree holding the REAL build, or `mac run` would install the opt-out one.
  //
  // ⚠ CLEAN, not incremental, and that is not caution — an incremental rebuild here produced an app that
  // launched and was killed instantly with SIGKILL (Code Signature Invalid). The link check above removes
  // the .app but not obj/, so the follow-up build re-assembled a bundle whose parts no longer matched their
  // signature. It is the same partial-deletion trap `mobile-shells.md` already records, reintroduced by this
  // very gate; the failure reads like a provisioning fault and has nothing to do with provisioning.
  console.log('mac: restoring the real build (clean, so the bundle and its signature agree)…');
  const sampleDir = cfg.project.replace(/\/[^/]+\.csproj$/, '');
  const restore = ssh(cfg, `set -e -o pipefail
    cd ~/${cfg.work}
    rm -rf ${q(`${sampleDir}/bin`)} ${q(`${sampleDir}/obj`)} ${q(`${sampleDir}/shenora-liveactivity`)}
    dotnet build ${q(cfg.project)} -c Debug -f ${q(cfg.tfm)} -p:RuntimeIdentifier=${q(rid)}${skipXcode} 2>&1 | tail -8`);
  if (restore.status !== 0) {
    console.error('\nmac: could not restore the real build after the link check.\n'
      + (restore.stdout ?? '').split('\n').slice(-8).join('\n'));
    process.exit(restore.status ?? 1);
  }
  return rid;
}

// ---------------------------------------------------------------- run
function run(cfg) {
  const rid = build(cfg);
  console.log('\nmac: booting the simulator + launching…');
  // `open -a Simulator` is what actually shows the window; a booted device with no UI still installs
  // and runs, which looks like nothing happened. Find the .app rather than composing its path: the
  // MAUI output layout has moved between SDK versions and a wrong guess reports "not found" for a
  // build that in fact succeeded.
  ssh(cfg, `set -e
    APP=$(find ~/${cfg.work}/samples/Shenora.Sample.Maui/bin/Debug/${cfg.tfm}/${rid} -maxdepth 1 -name '*.app' | head -1)
    if [ -z "$APP" ]; then echo "no .app under bin/Debug/${cfg.tfm}/${rid}" >&2; exit 1; fi
    echo "app: $APP"
    xcrun simctl boot ${q(cfg.simulator)} 2>/dev/null || true
    open -a Simulator || true
    xcrun simctl install booted "$APP"
    xcrun simctl launch booted ${q(cfg.bundleId)}`, { inherit: true, check: true });
  console.log('\nmac: running. Screenshot it with:  node devtools/dev.mjs mac shot');
}

// ---------------------------------------------------------------- shot
function shot(cfg, name = 'sim') {
  fs.mkdirSync(OUT, { recursive: true });
  const remote = `/tmp/shenora-${name}.png`;
  ssh(cfg, `xcrun simctl io booted screenshot ${q(remote)}`, { check: true });
  const local = path.join(OUT, `${name}.png`);
  const r = scpFrom(cfg, remote, local);
  if (r.status !== 0) { console.error(`mac: screenshot copy failed\n${r.stderr}`); process.exit(1); }
  console.log(`mac: ${path.relative(repo, local)}`);
}

// ---------------------------------------------------------------- tap / type
//
// simctl can screenshot a simulator but cannot touch it, so input goes through the Simulator app's own
// window via System Events — which means converting a point in the SCREENSHOT into a point on the
// Mac's DESKTOP. Doing that by eye does not work: a screenshot is device PIXELS while the window is
// desktop POINTS, and the Simulator additionally scales the device to fit. Two conversions, both
// invisible in the image, and on the sibling the first attempt missed every target.
//
// So ask the window instead of guessing. The Simulator's first AXGroup IS the device screen and its
// position and size are already in desktop points, so the mapping is
//
//     desktop = groupOrigin + screenshotPixel * (groupSize / screenshotSize)
//
// with no device model, scale setting or bezel thickness hardcoded — and it stays correct when the
// window is moved, resized, or pointed at a different simulator. Pass the coordinates you read off the PNG.
//
// Requires Accessibility permission for whatever runs the ssh session (System Settings → Privacy &
// Security → Accessibility); without it every click silently does nothing or errors -25204.
async function metrics(cfg) {
  const script = [
    'tell application "System Events" to tell process "Simulator" to tell first group of first window',
    'set p to position',
    'set s to size',
    'return ("" & (item 1 of p) & " " & (item 2 of p) & " " & (item 1 of s) & " " & (item 2 of s))',
    'end tell',
  ].map((l) => `-e ${q(l)}`).join(' ');
  // Geometry and screenshot size are read TOGETHER in one command: they are two halves of the same
  // ratio, and a round trip each is most of a tap's latency.
  const r = await sh(cfg, `osascript ${script}; `
    + 'xcrun simctl io booted screenshot --type=png /tmp/shenora-geom.png >/dev/null 2>&1 && '
    + 'sips -g pixelWidth -g pixelHeight /tmp/shenora-geom.png | awk \'/pixel/{print $2}\'');
  // `shotH`, not `sh` — the obvious name SHADOWS the sh() helper above, and because this is a `const`
  // the shadow wins from the top of the function, so the `await sh(...)` two lines up dies with
  // "Cannot access 'sh' before initialization". Hit while porting: the sibling's oddly-named `shh`
  // looked like a typo and was the fix. Do not tidy it back.
  const [x, y, w, h, sw, shotH] = r.stdout.trim().split(/\s+/).map(Number);
  if (![x, y, w, h, sw, shotH].every(Number.isFinite) || !sw || !shotH) {
    console.error(`mac: could not read the Simulator window geometry — is the Simulator running, and has the
ssh session been granted Accessibility permission (System Settings -> Privacy & Security -> Accessibility)?
Without it every click silently does nothing.\n${r.stdout}${r.stderr}`);
    process.exit(1);
  }
  // Callers still read `.sh` (screenshot height) — only the LOCAL had to be renamed.
  return { x, y, w, h, sw, sh: shotH };
}

// ⚠ `px`/`py` are NATIVE screenshot pixels — the size `mac shot` really captured (1206x2622 on a Pro),
// NOT the size a tool DISPLAYED it at. Anything that previews the PNG may downscale it and label the
// display size; multiply back before calling this or every tap misses. Hit live in this repo during the
// DM1 iOS run, and already written down in the sibling this was ported from.
async function tap(cfg, px, py) {
  const m = await metrics(cfg);
  const x = Math.round(m.x + Number(px) * (m.w / m.sw));
  const y = Math.round(m.y + Number(py) * (m.h / m.sh));
  // Prefer cliclick when present — `System Events click at` intermittently only FOCUSES a web button
  // on the sim rather than clicking it; cliclick lands a real click every time via CGEvent. Falls back
  // for hosts that do not have it installed.
  const has = await sh(cfg, 'command -v cliclick >/dev/null 2>&1 && echo YES || echo NO');
  await sh(cfg, `osascript -e ${q('tell application "Simulator" to activate')}`);
  const viaCliclick = (has.stdout ?? '').trim().startsWith('YES');
  if (viaCliclick) {
    await sh(cfg, `cliclick c:${x},${y}`);
  } else {
    await sh(cfg, `osascript -e ${q('delay 0.15')} `
      + `-e ${q(`tell application "System Events" to tell process "Simulator" to click at {${x}, ${y}}`)}`);
  }
  // SAY which mechanism landed it. The fallback is materially weaker — it can register as focus-only on
  // some web controls — so a silent downgrade means a tap that "succeeded" and did nothing, and the next
  // half hour goes on the page instead of on the harness. Exactly how the shell-PATH bug above hid.
  console.log(`mac: tapped screenshot (${px}, ${py}) -> desktop (${x}, ${y})`
    + `  [${viaCliclick ? 'cliclick' : 'System Events — WEAKER, install cliclick on the Mac'}]`);
}

async function typeText(cfg, text) {
  // keystroke must be addressed to the Simulator PROCESS; sending it to System Events itself errors -25204.
  await sh(cfg, `osascript -e ${q('tell application "Simulator" to activate')} -e ${q('delay 0.3')} `
    + `-e ${q(`tell application "System Events" to tell process "Simulator" to keystroke ${JSON.stringify(text)}`)}`);
  console.log(`mac: typed ${text.length} chars`);
}

/**
 * Drag (px1, py1) → (px2, py2) in screenshot pixels. Scrolling, and any gesture a tap cannot express.
 *
 * iOS Simulator turns a mac mouse drag into a single-touch swipe, but only if the drag UNFOLDS over
 * several events — a snap from A to B lands as a tap on A. Hence the stepped path with a short wait
 * per step (~15 steps over ~300 ms reads as a natural flick).
 *
 * cliclick is REQUIRED here, and the reason is worth keeping: raw `CGEventPost` from an ssh session
 * goes to the console user's event stream, which the Simulator is not listening on unless sshd holds
 * Accessibility permission — which it never does out of the box, so the call succeeds and nothing
 * happens. System Events `click at` DOES land (it runs inside a permission-holding process) but has no
 * mouse-down/mouse-up primitive, so a drag cannot be built from it. Missing cliclick therefore REFUSES
 * loudly rather than half-scrolling.
 */
async function swipe(cfg, px1, py1, px2, py2, { steps = 15, holdMs = 20 } = {}) {
  if ([px1, py1, px2, py2].some((v) => v === undefined)) {
    console.error('mac swipe: needs four screenshot-pixel coordinates — <fromX> <fromY> <toX> <toY>.');
    process.exit(1);
  }
  const has = await sh(cfg, 'command -v cliclick >/dev/null 2>&1 && echo YES || echo NO');
  if (!(has.stdout ?? '').trim().startsWith('YES')) {
    console.error('mac: cliclick is not installed on the Mac — swipe would silently do nothing, so it is\n'
      + 'refusing instead. Install it once with:\n  brew install cliclick');
    process.exit(1);
  }
  const m = await metrics(cfg);
  const toDesktop = (px, py) => [
    Math.round(m.x + Number(px) * (m.w / m.sw)),
    Math.round(m.y + Number(py) * (m.h / m.sh)),
  ];
  const [x1, y1] = toDesktop(px1, py1);
  const [x2, y2] = toDesktop(px2, py2);
  // dd = mouse down, dm = drag move, w:<ms> = wait, du = mouse up.
  const line = [`dd:${x1},${y1}`];
  for (let i = 1; i <= steps; i++) {
    line.push(`w:${holdMs}`,
      `dm:${Math.round(x1 + (x2 - x1) * (i / steps))},${Math.round(y1 + (y2 - y1) * (i / steps))}`);
  }
  line.push(`du:${x2},${y2}`);
  await sh(cfg, `osascript -e ${q('tell application "Simulator" to activate')}`);
  await sh(cfg, `cliclick ${line.join(' ')}`);
  console.log(`mac: swiped (${px1}, ${py1}) -> (${px2}, ${py2})  [desktop (${x1},${y1})->(${x2},${y2})]`);
}

// ---------------------------------------------------------------- safari-eval
//
// Evaluate JavaScript INSIDE the simulator's webview and get the value back. This is the difference
// between reading state and guessing at it: without it the only way to know what the page thinks is to
// screenshot it and read pixels, which cannot report a number, a header, or an array — and a `<video>`
// element in particular can only ever say "no supported source" however it failed. The DM1 media work
// (D44) was done the hard way and its sharpest evidence — that a ranged response came back as four
// bytes of the WRONG four bytes — could only be obtained by running a `fetch` in the page.
//
// The bridge is `ios_webkit_debug_proxy`, which exposes the sim's Web Inspector as a Chrome DevTools
// Protocol endpoint. Ported from the public sibling Sonora.
async function safariEval(cfg, js) {
  if (!js) {
    console.error('mac safari-eval: pass the JS as the last argument, e.g.\n'
      + '  node devtools/dev.mjs mac safari-eval "document.title"');
    process.exit(1);
  }
  const installed = ssh(cfg, 'command -v ios_webkit_debug_proxy').status === 0;
  if (!installed) {
    console.error(`mac safari-eval: the CDP bridge is not on the Mac. Install it once:
  node devtools/dev.mjs mac ssh 'brew install ios-webkit-debug-proxy'

⚠ If that fails with "Permission denied" under /usr/local/Cellar, DO NOT reach for
\`sudo chown -R\` on the Homebrew tree — that is the advice the donor carried and on this Mac it is
both unnecessary and destructive, because it would take Homebrew away from the user who owns it.

The real shape, measured here 2026-08-03: the ssh user and the Homebrew OWNER are different accounts.
The tree is group-writable to \`admin\` and the ssh user IS in that group, so most of it works — but
individual formula directories predate that and are mode 755, so brew cannot add a new version
directory inside one. The first casualty is a DEPENDENCY (ca-certificates), which is why the error
never mentions the package being installed.

  Cheapest fix, and it changes no permissions at all: run the install ON the Mac, in Terminal.app,
  logged in as the account that owns \`/usr/local/Cellar\` (\`ls -ld /usr/local/Cellar\` names it).`);
    process.exit(1);
  }
  // Start iwdp in the background if it is not already up. -F drops its built-in HTML frontend (we speak
  // CDP directly); -c binds control port 9221 and reserves a per-sim page-port range. `pgrep -f` matches
  // the FULL command line, so the long binary name is not truncated away.
  ssh(cfg, `pgrep -f ios_webkit_debug_proxy >/dev/null 2>&1 \
    || (nohup ios_webkit_debug_proxy -F -c null:9221,:9222-9322 </dev/null >/tmp/iwdp.log 2>&1 &) \
    && sleep 1`);

  const nodeScript = `
    const targetHint = ${JSON.stringify(cfg.safariEvalTargetHint ?? '')};
    const js = ${JSON.stringify(js)};
    (async () => {
      // Poll the control port briefly — iwdp may have been started by THIS run a moment ago.
      let sims = null;
      for (let i = 0; i < 20; i++) {
        try { sims = await (await fetch('http://127.0.0.1:9221/json')).json(); if (Array.isArray(sims)) break; } catch {}
        await new Promise((r) => setTimeout(r, 250));
      }
      if (!Array.isArray(sims) || sims.length === 0) {
        console.error('safari-eval: iwdp reports no simulators — is one booted?'); process.exit(2);
      }
      let target = null;
      for (const sim of sims) {
        try {
          const port = new URL(sim.url).port;
          const pages = await (await fetch('http://127.0.0.1:' + port + '/json')).json();
          const p = pages.find((p) => (
            (p.type === 'page' || p.type === 'web' || p.type === undefined)
            && (targetHint ? (p.url?.includes(targetHint) || p.title?.includes(targetHint)) : (p.url || p.webSocketDebuggerUrl))
          ));
          if (p && p.webSocketDebuggerUrl) { target = p; break; }
        } catch {}
      }
      if (!target) {
        console.error('safari-eval: no inspectable page in any sim. Is the app foregrounded? Run \`mac run\` first.');
        process.exit(3);
      }
      // CDP over WebSocket. Node's global WebSocket is WHATWG (addEventListener), not the ws package's API.
      const ws = new WebSocket(target.webSocketDebuggerUrl);
      let nextId = 1;
      const call = (method, params) => new Promise((res, rej) => {
        const id = nextId++;
        const handler = (evt) => {
          let m; try { m = JSON.parse(evt.data); } catch { return; }
          if (m.id === id) { ws.removeEventListener('message', handler); res(m); }
        };
        ws.addEventListener('message', handler);
        ws.send(JSON.stringify({ id, method, params }));
        setTimeout(() => rej(new Error('CDP call timed out: ' + method)), 15000);
      });
      await new Promise((res, rej) => {
        ws.addEventListener('open', res);
        ws.addEventListener('error', () => rej(new Error('WS connect failed to ' + target.webSocketDebuggerUrl)));
      });
      // awaitPromise, so an \`async\` expression or a bare fetch chain resolves before we read it —
      // which is what makes this usable for asking the page to perform a request and report the result.
      const rsp = await call('Runtime.evaluate', {
        expression: js, returnByValue: true, awaitPromise: true, generatePreview: true,
      });
      ws.close();
      const r = rsp.result;
      if (r?.exceptionDetails) { console.error(JSON.stringify(r.exceptionDetails, null, 2)); process.exit(4); }
      const v = r?.result;
      if (v?.type === 'undefined') process.stdout.write('undefined\\n');
      else if ('value' in (v ?? {})) process.stdout.write(JSON.stringify(v.value) + '\\n');
      else process.stdout.write(JSON.stringify(v ?? rsp) + '\\n');
    })().catch((e) => { console.error('safari-eval failed:', e.message ?? e); process.exit(1); });
  `;
  const r = ssh(cfg, `node -e ${q(nodeScript)}`);
  if (r.stdout) process.stdout.write(r.stdout);
  if (r.status !== 0) {
    if (r.stderr) process.stderr.write(r.stderr);
    process.exit(r.status ?? 1);
  }
}

// ---------------------------------------------------------------- mirror
//
// A LIVE view of the simulator served on the LAN, where clicking the image taps the sim and a wheel
// scroll swipes it. Two reasons it exists rather than being a nicety:
//
//   1. It is the RELIABLE drive path. A raw `mac tap` synthetic click intermittently only FOCUSES a
//      web control rather than clicking it — the mirror lands it every time, and the difference is not
//      the coordinate maths (identical) but that its persistent ssh keeps the Simulator focused when
//      the click arrives.
//   2. Content below the fold is otherwise unreachable, so any bug whose control is off-screen costs a
//      session.
//
// Ported from the public sibling Sonora, which earned both of those.
// ⚠ NOT 7672. That is the sibling's default, both repos live on this machine, and both mirrors would
// point at the same simulator — so the collision is silent rather than obvious (see the EADDRINUSE
// handler below). Same reasoning as the family's one-dev-port-per-app rule in `webview2-hosting.md`.
const MIRROR_PORT = 7674;

function mirror(cfg, port = MIRROR_PORT) {
  openWorker(cfg);
  let busy = false;
  const body = (req) => new Promise((res) => {
    let s = '';
    req.on('data', (c) => { s += c; });
    req.on('end', () => { try { res(JSON.parse(s)); } catch { res({}); } });
  });

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/frame') {
      // ONE screenshot at a time: the worker is a single pipe, so overlapping requests only queue behind
      // each other and every queued frame is stale before it is served.
      if (busy) { res.writeHead(429).end(); return; }
      busy = true;
      let png = null;
      try {
        const r = await sh(cfg, 'xcrun simctl io booted screenshot --type=png /tmp/shenora-mirror.png >/dev/null 2>&1'
          + ' && base64 -i /tmp/shenora-mirror.png');
        const b64 = r.stdout.replace(/\s+/g, '');
        if (b64) png = Buffer.from(b64, 'base64');
      } finally { busy = false; }
      if (!png?.length) { res.writeHead(503).end('no booted simulator'); return; }
      res.writeHead(200, { 'content-type': 'image/png', 'cache-control': 'no-store' }).end(png);
      return;
    }
    if (url.pathname === '/tap' && req.method === 'POST') {
      const { x, y } = await body(req);
      await tap(cfg, x, y);
      res.writeHead(204).end();
      return;
    }
    if (url.pathname === '/type' && req.method === 'POST') {
      const { text } = await body(req);
      if (text) await typeText(cfg, text);
      res.writeHead(204).end();
      return;
    }
    if (url.pathname === '/swipe' && req.method === 'POST') {
      const { fromX, fromY, toX, toY } = await body(req);
      if ([fromX, fromY, toX, toY].every((n) => Number.isFinite(n))) await swipe(cfg, fromX, fromY, toX, toY);
      res.writeHead(204).end();
      return;
    }
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }).end(MIRROR_HTML);
  });

  // A bound port must FAIL LOUDLY AND SPECIFICALLY. Without this the default handler throws an
  // unhandled 'error' event and the process dies on a raw EADDRINUSE stack — and the reason that is
  // not merely ugly: the sibling this was ported from defaults to a mirror port too, so the thing
  // already listening may be ANOTHER REPO's mirror pointed at the SAME simulator. It answers
  // /frame with a valid screenshot, so a smoke test passes while testing someone else's server.
  // Happened on the first run of this function.
  server.on('error', (err) => {
    if (err.code === 'EADDRINUSE') {
      console.error(`mac mirror: port ${port} is already in use.\n`
        + '  Something is already listening — possibly a sibling repo\'s mirror on the same machine,\n'
        + '  which would answer with the SAME simulator and look like this one working.\n'
        + `  Pick another:  node devtools/dev.mjs mac mirror ${port + 1}`);
      process.exit(1);
    }
    console.error(`mac mirror: ${err.message}`);
    process.exit(1);
  });

  server.listen(port, '0.0.0.0', () => {
    const addrs = Object.values(os.networkInterfaces()).flat()
      .filter((n) => n && n.family === 'IPv4' && !n.internal).map((n) => n.address);
    console.log('mac mirror: live simulator view + click-to-tap\n'
      + addrs.map((a) => `  http://${a}:${port}`).join('\n')
      + `\n  http://localhost:${port}\n\nCtrl-C to stop.`);
  });
}

const MIRROR_HTML = `<!doctype html><meta charset="utf-8"><title>Simulator</title>
<meta name="viewport" content="width=device-width,initial-scale=1">
<style>
 body{margin:0;background:#111;color:#ccc;font:14px system-ui;display:flex;flex-direction:column;
      align-items:center;gap:10px;padding:12px}
 img{max-width:min(420px,92vw);border-radius:14px;cursor:crosshair;display:block;background:#000}
 .row{display:flex;gap:8px;width:min(420px,92vw)}
 input{flex:1;padding:8px 10px;border-radius:8px;border:1px solid #444;background:#1b1b1b;color:#eee}
 button{padding:8px 14px;border-radius:8px;border:1px solid #444;background:#2a2a2a;color:#eee}
 small{opacity:.6}
</style>
<img id="s" alt="simulator">
<div class="row"><input id="t" placeholder="type into the simulator… (Enter)" autocapitalize="none"
  autocorrect="off" spellcheck="false"><button id="g">Send</button></div>
<small id="m">click the screen to tap it · scroll to swipe</small>
<script>
const img=document.getElementById('s'),msg=document.getElementById('m'),inp=document.getElementById('t');
let stop=false;
// Sequential, NOT on a timer: a fixed interval stacks requests whenever a frame takes longer than the
// interval, and every queued frame is already stale by the time it is served.
async function loop(){
  while(!stop){
    try{
      const r=await fetch('/frame?t='+Date.now());
      if(r.ok){const b=await r.blob();const u=URL.createObjectURL(b);
        await new Promise(k=>{img.onload=k;img.onerror=k;img.src=u});
        setTimeout(()=>URL.revokeObjectURL(u),1000);}
    }catch(e){}
    await new Promise(k=>setTimeout(k,250));
  }
}
img.addEventListener('click',async e=>{
  const r=img.getBoundingClientRect();
  // NATURAL pixels — the server's tap already knows how to turn screenshot pixels into desktop points.
  const x=Math.round((e.clientX-r.left)/r.width*img.naturalWidth);
  const y=Math.round((e.clientY-r.top)/r.height*img.naturalHeight);
  msg.textContent='tap '+x+', '+y;
  await fetch('/tap',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({x,y})});
});
// Wheel on the mirror -> swipe on the sim. iOS scrolls OPPOSITE a mouse wheel (content follows the
// finger), so deltaY is inverted. One tick ~= 40% of screen height, which reads as a native flick.
let scrolling=false;
img.addEventListener('wheel',async e=>{
  e.preventDefault();
  if(scrolling)return;
  scrolling=true;
  try{
    const centerX=Math.round(img.naturalWidth/2);
    const startY=Math.round(img.naturalHeight*0.65);
    const endY=Math.round(startY-(e.deltaY>0?1:-1)*img.naturalHeight*0.4);
    msg.textContent='swipe '+startY+' -> '+endY;
    await fetch('/swipe',{method:'POST',headers:{'content-type':'application/json'},
      body:JSON.stringify({fromX:centerX,fromY:startY,toX:centerX,toY:endY})});
  } finally { scrolling=false; }
}, {passive:false});
async function send(){
  if(!inp.value)return;
  await fetch('/type',{method:'POST',headers:{'content-type':'application/json'},
    body:JSON.stringify({text:inp.value})});
  msg.textContent='typed: '+inp.value; inp.value='';
}
document.getElementById('g').onclick=send;
inp.addEventListener('keydown',e=>{if(e.key==='Enter')send()});
loop();
</script>`;

// ---------------------------------------------------------------- log
//
// The simulator's unified log. `log show --last` rather than `log stream`: a stream never returns and
// would hang an unattended session — the same reason android.mjs tails a bounded slice instead of
// following.
//
// Default is the SAMPLE'S OWN tag, not the process, and that distinction is the whole usability of
// this command. A process-wide predicate is ~99% WebKit lifecycle chatter (a `runJavaScriptInFrame`
// pair every notification tick), so `tail -n` then shows a screen of noise and NONE of the app's
// lines — which reads exactly like "the app logged nothing" and sent this session looking for a
// broken log sink that was working perfectly. Identical in shape to the `logcat -t N` trap already
// recorded in android.mjs: filter FIRST, tail after. `--all` for the platform's side.
function log(cfg, n = 80, { all = false } = {}) {
  // The app reaches the unified log through libSystem.Native (Console -> stdout), so it is the
  // MESSAGE that carries the tag, not the subsystem — verified by reading back a real run.
  const predicate = all
    ? 'process == "Shenora.Sample.Maui"'
    : `eventMessage CONTAINS "[${'SHENORA'}]"`;
  const r = ssh(cfg, `xcrun simctl spawn booted log show --last 15m --style compact `
    + `--predicate ${q(predicate)} 2>/dev/null | tail -${Number(n) || 80}`);
  const out = (r.stdout ?? '').trim();
  console.log(out || `mac: no ${all ? 'process' : 'SHENORA'} log lines in the last 15 minutes (is the app running?)`);
}

// ---------------------------------------------------------------- awake
// The simulator's view of LIVE ACTIVITIES — what exists, who owns it, and whether the widget that renders
// it was ever launched.
//
// It OBSERVES rather than drives, and that limit is real rather than laziness: an activity is started BY THE
// APP (or by an APNs push), so there is no `simctl` verb to start one from outside and any "drive it" tool
// would really be driving the app's own IPC, which is the app's shape and not the kit's. What a developer
// actually lacks is sight — a Live Activity is invisible until you background the app, the Dynamic Island is
// not rendered on a simulator at all (its scene target is lockscreen-only there, measured), and a malformed
// state fails with no error anywhere. This answers the three questions that follow from that: is my extension
// registered, did an activity really start, and did the OS launch the widget to render it.
function activities(cfg) {
  const sh = (script) => (ssh(cfg, script).stdout ?? '').trim();

  console.log('== widget extensions the OS has registered (pluginkit)');
  const plugins = sh('xcrun simctl spawn booted pluginkit -mAvvv 2>/dev/null '
    + `| grep -i -A1 ${q(cfg.bundleId)} | head -20`);
  console.log(plugins || `  none for ${cfg.bundleId} — the build produced no widget extension, or it was `
    + 'never installed. Check ShenoraLiveActivityViews is set.');

  console.log('\n== activities liveactivitiesd knows about (last 15m)');
  // `Starting activity` carries the id, the state and the content sources on one line, which is the single
  // most informative line the daemon emits — so it is the one worth surfacing rather than the whole subsystem.
  const started = sh('xcrun simctl spawn booted log show --last 15m --style compact '
    + `--predicate 'process == "liveactivitiesd"' 2>/dev/null `
    + "| grep -E 'Created activity|Starting activity|Ending activity|Dismissed' | tail -12");
  console.log(started || '  none — no activity was started in the last 15 minutes.');

  console.log('\n== was the widget LAUNCHED to render (chronod/ExtensionKit)');
  const launched = sh('xcrun simctl spawn booted log show --last 15m --style compact 2>/dev/null '
    + `| grep -i ${q(cfg.bundleId)} | grep -iE 'Launching process|launch request' | tail -4`);
  console.log(launched || '  no launch recorded. An activity can be ACTIVE with the widget never launched — '
    + 'that is the shape of a module-name mismatch between the shim and the extension.');

  console.log('\n⚠ An empty Dynamic Island on a SIMULATOR is expected: an activity there reports only a '
    + 'lockscreen scene target, so the pill stays blank however long you wait. Use a device to see it.');
}

const AGENT_LABEL = 'dev.shenora.caffeinate';

function awake(cfg, mode = 'on') {
  const uid = ssh(cfg, 'id -u', { check: true }).stdout.trim();
  const plistPath = `Library/LaunchAgents/${AGENT_LABEL}.plist`;
  if (mode === 'off') {
    ssh(cfg, `launchctl bootout gui/${uid}/${AGENT_LABEL} 2>/dev/null; rm -f ~/${plistPath}; `
      + 'defaults -currentHost delete com.apple.screensaver idleTime 2>/dev/null; true', { check: true });
    console.log('mac: awake off — caffeinate agent removed, screensaver back to the system default.');
    return;
  }
  // -d display, -i idle, -m disk, -s system. KeepAlive restarts it if it ever dies.
  const plist = `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>${AGENT_LABEL}</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/bin/caffeinate</string>
    <string>-dims</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
</dict>
</plist>
`;
  fs.mkdirSync(OUT, { recursive: true });
  const tmp = path.join(OUT, 'caffeinate.plist');
  fs.writeFileSync(tmp, plist);
  ssh(cfg, 'mkdir -p ~/Library/LaunchAgents', { check: true });
  const scp = spawnSync('scp', ['-i', cfg.key, '-o', 'BatchMode=yes', '-o', 'StrictHostKeyChecking=accept-new',
    tmp, `${cfg.user}@${cfg.host}:${plistPath}`], { encoding: 'utf8' });
  if (scp.status !== 0) { console.error(`mac: could not copy the launch agent\n${scp.stderr}`); process.exit(1); }
  ssh(cfg, `launchctl bootout gui/${uid}/${AGENT_LABEL} 2>/dev/null; `
    + `launchctl bootstrap gui/${uid} ~/${plistPath}`, { check: true });
  // 0 = never. Separate from caffeinate: -d stops the display SLEEPING, it does not stop the screensaver
  // starting, and it is the screensaver that brings the lock screen with it.
  ssh(cfg, 'defaults -currentHost write com.apple.screensaver idleTime -int 0', { check: true });
  console.log('mac: awake on — caffeinate agent loaded, screensaver disabled. Revert with `mac awake off`.');
  console.log('  Sleep on BATTERY is untouched (that needs sudo); the caffeinate assertion covers it while loaded.');
}

// ---------------------------------------------------------------- provision
//
// Mint a provisioning profile for a bundle id, using an Xcode project THIS KIT owns.
//
// 🔴 Why any of this is needed: **.NET cannot mint a profile.** It consumes them —
// `-p:CodesignProvision=Automatic` picks one that already exists and fails with "Could not find any
// available provisioning profiles" when none does. Only `xcodebuild -allowProvisioningUpdates` creates
// one, and it needs *an* Xcode project to point at. So without this, reaching a device at all required
// owning an Xcode project — in a kit whose stated measure is how little native code an adopter writes.
//
// ⚠ The wrong fix, tried and rejected (2026-08-06): borrow a sibling app's Capacitor project. It works and
// it is wrong three ways — slow, drags that app's SPM checkouts into an unrelated build, and makes the kit
// depend on a consumer having Capacitor. Owner: *"why you rely on capacitor instead create your own one"*.
//
// ⚠ It does NOT call `push`, deliberately. A provisioning profile is state of the MACHINE, not of the
// repo, and `push` refuses a dirty tree — so tying the two would make "I cannot sign" depend on whether
// some unrelated edit happened to be committed. The stub is three small text files, uploaded directly.

const PROVISION_FILES = ['README.md', 'ShenoraProvision/main.swift', 'ShenoraProvision.xcodeproj/project.pbxproj'];
const PROVISION_DIR = 'shenora-provision';

/** Upload the stub project, so provisioning works from any tree state. */
function uploadProvisionStub(cfg) {
  const local = path.join(repo, 'devtools', 'ios-provision');
  ssh(cfg, `rm -rf ~/${PROVISION_DIR} && mkdir -p ~/${PROVISION_DIR}/ShenoraProvision ~/${PROVISION_DIR}/ShenoraProvision.xcodeproj`, { check: true });
  for (const rel of PROVISION_FILES) {
    const encoded = fs.readFileSync(path.join(local, rel)).toString('base64');
    // Through base64 for the same reason guiRun encodes its script: the content crosses ssh's own shell
    // before anything else sees it, and a pbxproj is full of quotes, braces and dollar signs.
    ssh(cfg, `printf %s ${q(encoded)} | base64 -d > ~/${PROVISION_DIR}/${rel}`, { check: true });
  }
}

/**
 * The Apple Developer team, from local/mac.json or derived from the signing certificate.
 *
 * ⚠ Derived rather than hardcoded because a team id is a PERSONAL identifier — `sensitive-info.md` keeps
 * those out of tracked files, and this file is public. The certificate already on the machine knows it.
 */
function teamId(cfg) {
  if (cfg.team) return cfg.team;
  const r = ssh(cfg, `set -e
    name=$(security find-identity -v -p codesigning | head -1 | sed -n 's/.*"\\(.*\\)".*/\\1/p')
    [ -n "$name" ] || exit 3
    security find-certificate -c "$name" -p | openssl x509 -noout -subject | tr ',' '\\n' | sed -n 's/.*OU=//p' | head -1`);
  const derived = (r.stdout ?? '').trim();
  if (r.status !== 0 || !derived) {
    console.error('mac: no codesigning identity on the Mac, so there is no team to provision against.\n'
      + '     Sign in to Xcode → Settings → Accounts with the Apple ID that owns the device, then retry.\n'
      + '     Or set "team" in local/mac.json if you know the id.');
    process.exit(1);
  }
  return derived;
}

/**
 * Every profile on the Mac, with the bundle id it is FOR — the only thing that proves one was minted.
 *
 * 🔴 <b>TWO locations, and looking in only the classic one is a trap that has already cost a session.</b>
 * Xcode wrote profiles to `~/Library/MobileDevice/Provisioning Profiles/` for a decade; **Xcode 16 moved
 * them to `~/Library/Developer/Xcode/UserData/Provisioning Profiles/`**. A check against the old path on a
 * current Xcode reports "zero provisioning profiles on disk" about a machine that has several — which reads
 * as "provisioning has never worked here" and sends the next session off to mint profiles that already
 * exist. Measured on Xcode 26.3, where minting put all three in the NEW path and left the old one absent.
 */
function installedProfiles(cfg) {
  const r = ssh(cfg, `for dir in ~/Library/MobileDevice/Provisioning\\ Profiles ~/Library/Developer/Xcode/UserData/Provisioning\\ Profiles; do
    [ -d "$dir" ] || continue
    for f in "$dir"/*.mobileprovision; do
      [ -e "$f" ] || continue
      plist=$(security cms -D -i "$f" 2>/dev/null)
      app=$(printf %s "$plist" | plutil -extract Entitlements.application-identifier raw - 2>/dev/null)
      exp=$(printf %s "$plist" | plutil -extract ExpirationDate raw - 2>/dev/null)
      echo "$app|$exp|$dir"
    done
  done`);
  return (r.stdout ?? '').split('\n').map((l) => l.trim()).filter(Boolean)
    .map((line) => {
      const [app, expires, dir] = line.split('|');
      // The application-identifier is prefixed with the team id; the bundle id is what follows it.
      return { app, bundleId: app.includes('.') ? app.slice(app.indexOf('.') + 1) : app, expires, dir };
    });
}

async function provision(cfg, bundleIds) {
  // The sample's two ids by default: the app, and the Live Activity extension it embeds. An extension
  // needs its OWN profile, and forgetting the second one fails at the very end of a device install with
  // an error that names the app.
  const ids = bundleIds.length > 0 ? bundleIds : [cfg.bundleId, `${cfg.bundleId}.shenoraliveactivity`];
  const team = teamId(cfg);
  console.log(`mac: provisioning ${ids.length} bundle id(s) for team ${team}…`);

  uploadProvisionStub(cfg);

  const failures = [];
  for (const id of ids) {
    console.log(`\nmac: ${id}`);
    // 🔴 Through guiRun, NOT ssh, and this is the whole reason guiRun exists: codesign cannot use a
    // login-keychain key from an ssh session (errSecInternalComponent — proven on a copy of /bin/echo, so
    // it is nothing to do with this repo). Xcode's stored Apple ID session has the same problem. The GUI
    // login session has both.
    const script = `set -o pipefail
cd ~/${PROVISION_DIR}
xcodebuild -project ShenoraProvision.xcodeproj -target ShenoraProvision \\
  -sdk iphoneos -configuration Debug -allowProvisioningUpdates \\
  SYMROOT=~/${PROVISION_DIR}/build OBJROOT=~/${PROVISION_DIR}/build/obj \\
  PRODUCT_BUNDLE_IDENTIFIER=${q(id)} DEVELOPMENT_TEAM=${q(team)} CODE_SIGN_STYLE=Automatic 2>&1 | tail -40`;
    const { status, log } = await guiRun(cfg, script, { tag: `provision`, timeoutMs: 10 * 60_000 });
    if (status !== 0) {
      failures.push(id);
      console.log(log.split('\n').slice(-20).join('\n'));
    } else {
      console.log('  minted ok');
    }
  }

  // Report what is actually ON DISK rather than what xcodebuild said. A build can succeed against a
  // profile it already had, so "exit 0" does not mean a profile now exists for the id that was asked for.
  const profiles = installedProfiles(cfg);
  console.log('\nmac: provisioning profiles now on the Mac:');
  if (profiles.length === 0) console.log('  (none)');
  for (const p of profiles) console.log(`  ${p.bundleId}   expires ${p.expires ?? '?'}`);

  const missing = ids.filter((id) => !profiles.some((p) => p.bundleId === id));
  if (missing.length > 0) {
    console.error(`\nmac: NO PROFILE for: ${missing.join(', ')}`);
    console.error('  If xcodebuild asked to sign in, do it once in Xcode on the Mac and re-run.');
    process.exit(1);
  }

  console.log('\nmac: all requested ids have a profile.');
  console.log('  ⚠ A free / personal-team profile expires after 7 DAYS — re-run this when signing starts failing.');
  console.log('  ⚠ A FIRST install also needs the certificate trusted ON THE PHONE:');
  console.log('    Settings → General → VPN & Device Management → the developer account → Trust.');
  if (failures.length > 0) console.log(`  (xcodebuild reported failures for ${failures.join(', ')}, but the profiles exist.)`);
}

// ---------------------------------------------------------------- device
//
// Build, install and launch on a REAL iPhone — the peer of `dev.mjs android`, and the half the kit was
// missing entirely. Everything above this line is the SIMULATOR loop; a simulator cannot answer whether a
// Dynamic Island renders, whether a hardware decoder exists, or whether the app signs.
//
// The split that makes it work, and it is not obvious:
//   · BUILDING needs the login keychain, so it goes through `guiRun` (an ssh session is a different audit
//     session and codesign dies with errSecInternalComponent — proven on a copy of /bin/echo).
//   · INSTALLING and LAUNCHING need no keychain at all, so plain ssh + `xcrun devicectl` is enough.
// Getting that backwards is what makes people reach for a GUI on the Mac.

/**
 * Connected devices, from devicectl's own JSON rather than its column layout.
 *
 * 🔴 <b>An unreachable MAC must not report as "no devices".</b> This returned <c>[]</c> for any failure,
 * so when the Mac's mDNS name stopped resolving the harness said "no iPhone connected to the Mac" — and
 * sent two people to check a phone that was fine, over the LAN, the whole time. It is the same defect
 * `CodecProbe` had (a failed query indistinguishable from a negative result), committed again in the same
 * session, which is why it is worth the comment: **the two states are not the same answer and must never
 * print the same sentence.**
 */
function devices(cfg) {
  const r = ssh(cfg, `f=$(mktemp) && xcrun devicectl list devices --json-output "$f" >/dev/null 2>&1 && cat "$f" && rm -f "$f"`);
  const failure = (r.stderr ?? '') + (r.stdout ?? '');
  if (/Could not resolve hostname|Connection refused|Operation timed out|No route to host|Connection closed/i.test(failure)) {
    console.error(`mac: CANNOT REACH THE MAC (${cfg.host}) — this is not a statement about the phone.\n`
      + `  ${failure.trim().split('\n')[0]}\n`
      + '  Wake it, check the LAN, or set "host" to its IP in local/mac.json if mDNS is flapping.');
    process.exit(1);
  }
  if (r.status !== 0 || !(r.stdout ?? '').trim()) return [];
  try {
    const parsed = JSON.parse(r.stdout);
    return (parsed?.result?.devices ?? []).map((d) => ({
      id: d?.identifier,
      name: d?.deviceProperties?.name ?? '(unnamed)',
      model: d?.hardwareProperties?.marketingName ?? d?.hardwareProperties?.productType ?? '?',
      os: d?.deviceProperties?.osVersionNumber ?? '?',
      state: d?.connectionProperties?.pairingState ?? '?',
      available: (d?.connectionProperties?.tunnelState ?? '') !== 'unavailable',
    })).filter((d) => d.id);
  } catch { return []; }
}

/**
 * Pick the device to work with.
 * ⚠ Refuses when several are connected rather than guessing. `dev.mjs android` already learned this the
 * expensive way: with two targets attached an unqualified command silently picked the wrong one and a whole
 * deploy/launch/log cycle was read as the other device's.
 */
function pickDevice(cfg, wanted) {
  const found = devices(cfg).filter((d) => d.available);
  if (found.length === 0) {
    console.error('mac: no iPhone connected to the Mac.\n'
      + '  Plug it in (or pair over Wi-Fi), unlock it, and trust the computer if asked.\n'
      + '  Check with: node devtools/dev.mjs mac devices');
    process.exit(1);
  }
  if (wanted) {
    const match = found.find((d) => d.id === wanted || d.name === wanted);
    if (!match) {
      console.error(`mac: no connected device matches ${wanted}. Connected: ${found.map((d) => d.name).join(', ')}`);
      process.exit(1);
    }
    return match;
  }
  if (found.length > 1) {
    console.error(`mac: ${found.length} devices connected — name one with --device <name|id>:`);
    for (const d of found) console.error(`  ${d.name}  (${d.model}, iOS ${d.os})  ${d.id}`);
    process.exit(1);
  }
  return found[0];
}

/**
 * Is every embedded app EXTENSION actually installable-and-launchable on a device?
 *
 * 🔴 **This exists because the Dynamic Island rendered an empty black capsule for a whole release band and
 * nothing anywhere said why** (2026-08-07). ActivityKit reported success at every step — the activity
 * started, took three updates and returned an id — the system reserved Island space for it, and the widget
 * simply never rendered. The cause was that the `.appex` was signed with **no entitlements and no
 * `embedded.mobileprovision`**, so iOS would not launch the extension process. Apple's rule is explicit:
 * an extension is provisioned separately from its container and needs its own profile at
 * `…app/PlugIns/NAME.appex/embedded.mobileprovision`, and an extension missing required entitlements is
 * not launched.
 *
 * ⚠ **A SIMULATOR CANNOT CATCH THIS AND NEVER WILL** — it does not enforce code signing, so the extension
 * loads there regardless. That is the 0.9.0 lesson repeated exactly: the one configuration that was ever
 * exercised was the one that worked. This check is the device-shaped half, and it needs no device: it reads
 * the BUILD OUTPUT, so it runs in seconds on the Mac and fails before anything is installed.
 *
 * It asserts what the OS asserts, in the OS's own terms — never "did the build step run".
 */
function checkAppExtensions(cfg, app) {
  const plugins = `${app}/PlugIns`;
  const listing = ssh(cfg, `ls -1 ${q(plugins)} 2>/dev/null | grep '\\.appex$' || true`);
  const found = (listing.stdout ?? '').split('\n').map((l) => l.trim()).filter(Boolean);
  if (found.length === 0) return { ok: true, checked: 0, problems: [] };

  const problems = [];
  for (const name of found) {
    const appex = `${plugins}/${name}`;
    const plist = `${appex}/Info.plist`;
    const r = ssh(cfg, `set -e
      id=$(plutil -extract CFBundleIdentifier raw ${q(plist)} 2>/dev/null || echo '')
      ents=$(codesign -d --entitlements - ${q(appex)} 2>/dev/null | tr -d '\\0' || true)
      appid=$(printf %s "$ents" | grep -A2 'application-identifier' | grep -o '[A-Z0-9]\\{10\\}\\.[A-Za-z0-9._-]*' | head -1)
      prof=no; [ -f ${q(`${appex}/embedded.mobileprovision`)} ] && prof=yes
      echo "id=$id"; echo "appid=$appid"; echo "profile=$prof"
      for k in CFBundleSupportedPlatforms DTPlatformName DTSDKName UIDeviceFamily; do
        v=$(plutil -extract $k xml1 -o - ${q(plist)} 2>/dev/null | grep -c '<' || echo 0)
        echo "key_$k=$v"
      done`);

    const read = (key) => ((r.stdout ?? '').match(new RegExp(`^${key}=(.*)$`, 'm'))?.[1] ?? '').trim();
    const bundleId = read('id');
    const appId = read('appid');
    const hasProfile = read('profile') === 'yes';

    // An extension is provisioned SEPARATELY from its container; the container's profile does not cover it.
    if (!hasProfile) problems.push(`${name}: no embedded.mobileprovision — iOS will not launch it on a device`);
    if (!appId) problems.push(`${name}: signed with NO entitlements — an extension without application-identifier is not launched`);
    // A prefix mismatch is the other half of the same rule, and it is what the installer rejects outright.
    else if (bundleId && !appId.endsWith(`.${bundleId}`)) {
      problems.push(`${name}: entitlement application-identifier "${appId}" does not match CFBundleIdentifier "${bundleId}"`);
    }

    // 🔴 The PLATFORM keys. Xcode writes these into everything it builds, so nobody building extensions the
    // normal way learns they exist — and a hand-written Info.plist that omits them yields an .appex that
    // installs, signs, validates, and is then NEVER REGISTERED as an extension. Every layer reports success
    // and the widget simply never loads, which is the hardest shape of failure there is.
    for (const key of ['CFBundleSupportedPlatforms', 'DTPlatformName', 'DTSDKName', 'UIDeviceFamily']) {
      if (read(`key_${key}`) === '0') {
        problems.push(`${name}: Info.plist has no ${key} — the system will not register it as an extension`);
      }
    }
  }

  return { ok: problems.length === 0, checked: found.length, problems };
}

/** The built .app, found rather than composed — the MAUI output layout has moved between SDK versions. */
function findApp(cfg, rid) {
  const sampleDir = cfg.project.replace(/\/[^/]+\.csproj$/, '');
  const r = ssh(cfg, `find ~/${cfg.work}/${sampleDir}/bin/Debug/${cfg.tfm}/${rid} -maxdepth 2 -name '*.app' -type d 2>/dev/null | head -1`);
  return (r.stdout ?? '').trim();
}

async function device(cfg, args) {
  const wanted = args.includes('--device') ? args[args.indexOf('--device') + 1] : null;
  const target = pickDevice(cfg, wanted);
  console.log(`mac: target — ${target.name} (${target.model}, iOS ${target.os})`);

  // A profile must exist BEFORE the build, or the failure arrives 90 seconds later wearing a signing
  // error that reads like a certificate problem.
  const profiles = installedProfiles(cfg);
  const missing = [cfg.bundleId].filter((id) => !profiles.some((p) => p.bundleId === id));
  if (missing.length > 0) {
    console.error(`mac: no provisioning profile for ${missing.join(', ')}.\n`
      + '  Run: node devtools/dev.mjs mac provision');
    process.exit(1);
  }
  const expiring = profiles.filter((p) => p.bundleId.startsWith(cfg.bundleId) && Date.parse(p.expires) - Date.now() < 24 * 3600_000);
  for (const p of expiring) console.log(`mac: ⚠ ${p.bundleId} expires ${p.expires} — re-run \`mac provision\` soon.`);

  if (!args.includes('--no-push')) push(cfg);
  fs.mkdirSync(OUT, { recursive: true });
  buildClientPackage(cfg);

  const rid = 'ios-arm64';
  const skipXcode = cfg.skipXcodeVersionCheck ? ' -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly' : '';
  if (skipXcode) {
    console.log('mac: ⚠ skipXcodeVersionCheck is on, and on a DEVICE build that matters more than on a\n'
      + '     simulator one: full trimming is off and the Xcode/workload pair is mismatched. Fine for a\n'
      + '     dev loop and a measurement; NOT a shipping configuration.');
  }

  // 🔴 Through guiRun, because this is the step that SIGNS. An ssh session cannot use a login-keychain key.
  console.log(`\nmac: dotnet build ${cfg.tfm} (${rid}), signing in the GUI session…`);
  const build = await guiRun(cfg, `set -o pipefail
cd ~/${cfg.work}
dotnet build ${q(cfg.project)} -c Debug -f ${q(cfg.tfm)} -p:RuntimeIdentifier=${q(rid)}${skipXcode} \\
  -p:CodesignProvision=Automatic -p:CodesignKey=${q('Apple Development')} 2>&1 | tail -60`,
    { tag: 'device-build', timeoutMs: 30 * 60_000 });

  fs.writeFileSync(path.join(OUT, 'device-build.log'), build.log);
  if (build.status !== 0) {
    console.error(build.log.split('\n').slice(-30).join('\n'));
    console.error('\nmac: DEVICE BUILD FAILED — full output: devtools/_mac/device-build.log');
    process.exit(build.status || 1);
  }
  console.log('mac: build + sign ok');

  const app = findApp(cfg, rid);
  if (!app) {
    console.error(`mac: the build succeeded but no .app was found under bin/Debug/${cfg.tfm}/${rid}.`);
    process.exit(1);
  }
  console.log(`mac: ${app.replace(/^.*\//, '')}`);

  // Before installing, not after: an extension that cannot launch installs perfectly happily and then does
  // nothing, which is the hardest possible thing to notice.
  const extensions = checkAppExtensions(cfg, app);
  if (extensions.checked > 0 && extensions.ok) {
    console.log(`mac: app extensions ok (${extensions.checked}) — entitlements + embedded profile present`);
  }
  if (!extensions.ok) {
    console.error('\nmac: APP EXTENSION NOT DEVICE-LAUNCHABLE:');
    for (const problem of extensions.problems) console.error(`  ${problem}`);
    console.error('\n  The app will install and run, and the extension will silently do NOTHING — a Live\n'
      + '  Activity shows as an empty black capsule while every ActivityKit call reports success.\n'
      + '  A simulator cannot catch this: it does not enforce code signing.');
    process.exit(1);
  }

  // Install and launch need NO keychain, so they go over plain ssh — much faster and easier to read.
  //
  // 🔴 `set -o pipefail` on BOTH, and it is not decoration. Without it a pipeline reports TAIL's exit
  // status, which is always 0 — so a failed install sails through, the launch is attempted against an app
  // that was never installed, and the command finishes by printing "running on the device". Measured on
  // the first real device run, which reported success while the install had been REJECTED. This file
  // already documents the identical trap on the build step; it was reintroduced here the same day.
  console.log('\nmac: installing…');
  const install = ssh(cfg, `set -o pipefail; xcrun devicectl device install app --device ${q(target.id)} ${q(app)} 2>&1 | tail -20`);
  console.log((install.stdout ?? '').trim());
  if (install.status !== 0) {
    console.error('\nmac: INSTALL FAILED.');
    console.error('  If it says the app could not be verified, the certificate is not TRUSTED on the phone yet:');
    console.error('  Settings → General → VPN & Device Management → the developer account → Trust.');
    process.exit(install.status ?? 1);
  }

  console.log('\nmac: launching…');
  const launch = ssh(cfg, `set -o pipefail; xcrun devicectl device process launch --device ${q(target.id)} ${q(cfg.bundleId)} 2>&1 | tail -20`);
  console.log((launch.stdout ?? '').trim());
  if (launch.status !== 0) { console.error('\nmac: LAUNCH FAILED.'); process.exit(launch.status ?? 1); }

  console.log('\nmac: running on the device. Logs: node devtools/dev.mjs mac device-log');
}

/**
 * The app's own log lines, from the DEVICE.
 * ⚠ Filtered at the source, not tailed and grepped here. `android.mjs` already records why: an unfiltered
 * device log is a firehose that rotates the interesting lines out while you are reading them.
 */
/**
 * Read the app's own log off the device by RELAUNCHING it with a console attached.
 *
 * ⚠ It relaunches, and that is deliberate rather than a compromise: this repo's probes run at STARTUP, so
 * a reader that attached to an already-running app would miss everything it came for.
 *
 * ⚠⚠ THIS FUNCTION WAS BROKEN AND SAID NOTHING (fixed 2026-08-07). It called
 * `devicectl device console`, which is not a subcommand on any Xcode — `devicectl device` offers copy,
 * info, install, notification, orientation, process, reboot, sysdiagnose, uninstall and nothing else. The
 * invocation piped through `2>/dev/null | head`, so the error text was discarded AND the exit status became
 * head's, which is always 0. The tool printed nothing, reported success, and the fallback advice below its
 * status check could never fire. **That is the exact trap `.claude/knowledge/mobile-shells.md` records for
 * `mac device` — the same mistake, in a sibling function, surviving the write-up of the first one.**
 * `set -o pipefail` is now on, as it is on the launch path.
 */
function deviceLog(cfg, count, wanted) {
  const target = pickDevice(cfg, wanted);
  const lines = Number(count) || 200;
  console.log(`mac: relaunching ${cfg.bundleId} with a console attached (startup output is the point)…`);
  const r = ssh(cfg,
    `set -o pipefail; xcrun devicectl device process launch --console --terminate-existing `
    + `--device ${q(target.id)} ${q(cfg.bundleId)} 2>&1 | head -${lines}`,
    { inherit: true });
  // head closing the pipe kills the stream, which is how this returns at all — so SIGPIPE (141) is the
  // expected success path, not a failure.
  if (r.status !== 0 && r.status !== 141) {
    console.log('\nmac: could not attach a console. Check the app is installed (`mac device`), or read it\n'
      + '     on the Mac with Console.app filtered to the device.');
  }
}

// ---------------------------------------------------------------- dispatch
const [cmd, ...rest] = process.argv.slice(2);
const cfg = cmd ? config() : null;

switch (cmd) {
  case 'doctor': doctor(cfg); break;
  case 'setup': setup(cfg); break;
  case 'push': push(cfg); break;
  case 'build': build(cfg); break;
  case 'run': run(cfg); break;
  case 'shot': shot(cfg, rest[0]); break;
  // The interactive ones open the persistent worker first and close it after: each runs several remote
  // commands (geometry, cliclick probe, activate, click) and a fresh ssh costs ~1.8 s apiece.
  case 'tap': openWorker(cfg); await tap(cfg, rest[0], rest[1]); closeWorker(); break;
  case 'type': openWorker(cfg); await typeText(cfg, rest.join(' ')); closeWorker(); break;
  case 'swipe': openWorker(cfg); await swipe(cfg, rest[0], rest[1], rest[2], rest[3]); closeWorker(); break;
  case 'safari-eval': await safariEval(cfg, rest.join(' ')); break;
  // No default repeated here — MIRROR_PORT is the ONE place it lives. Writing it twice is how the
  // constant ended up describing a port the tool never actually used.
  case 'mirror': mirror(cfg, Number(rest[0]) || undefined); break;
  case 'log': {
    const i = rest.indexOf('-n');
    log(cfg, i >= 0 ? rest[i + 1] : 80, { all: rest.includes('--all') });
    break;
  }
  case 'activity': activities(cfg); break;
  case 'device': await device(cfg, rest); break;
  case 'appex-check': {
    // Standalone so the check can be run against an EXISTING build without rebuilding or installing.
    const built = findApp(cfg, 'ios-arm64');
    if (!built) { console.error('mac: no ios-arm64 .app built yet — run `mac device` first.'); process.exit(1); }
    const result = checkAppExtensions(cfg, built);
    if (result.checked === 0) { console.log('mac: no app extensions embedded.'); break; }
    for (const problem of result.problems) console.error(`  ${problem}`);
    console.log(result.ok
      ? `mac: app extensions ok (${result.checked}) — entitlements + embedded profile present`
      : `mac: ${result.problems.length} problem(s) — the extension will NOT launch on a device`);
    if (!result.ok) process.exit(1);
    break;
  }
  case 'devices': {
    // ⚠ PRINT AVAILABILITY, not just pairing. This listing showed `pairingState` ("paired") while
    // `pickDevice` filters on `tunnelState` — so a phone that was paired but not currently reachable
    // listed as if it were ready, `mac device` failed with "no iPhone connected", and that error told you
    // to check with THIS command, which said everything was fine. A diagnostic that contradicts the thing
    // it diagnoses is worse than none. Hit live 2026-08-07.
    const found = devices(cfg);
    if (found.length === 0) console.log('mac: no devices known to this Mac.');
    for (const d of found) {
      const status = d.available ? 'READY' : 'NOT CONNECTED';
      console.log(`${d.name}  (${d.model}, iOS ${d.os})  ${d.state}/${status}  ${d.id}`);
    }
    if (found.length > 0 && !found.some((d) => d.available)) {
      console.log('\nmac: every device above is PAIRED but none is connected — `mac device` will refuse.\n'
        + '  Plug it in (or bring it onto the same network for Wi-Fi pairing), unlock it, and keep it awake.');
    }
    break;
  }
  case 'device-log': {
    const i = rest.indexOf('-n');
    deviceLog(cfg, i >= 0 ? rest[i + 1] : 200,
      rest.includes('--device') ? rest[rest.indexOf('--device') + 1] : null);
    break;
  }
  case 'provision': await provision(cfg, rest.filter((a) => !a.startsWith('-'))); break;
  case 'profiles': {
    const found = installedProfiles(cfg);
    if (found.length === 0) console.log('mac: no provisioning profiles on this Mac — run `mac provision`.');
    for (const p of found) console.log(`${p.bundleId}   expires ${p.expires ?? '?'}`);
    break;
  }
  case 'awake': awake(cfg, rest[0]); break;
  case 'ssh': ssh(cfg, rest.join(' '), { inherit: true, check: true }); break;
  default:
    console.log('usage: node devtools/dev.mjs mac <doctor|setup|push|build|run|shot|tap|type|swipe|'
      + 'safari-eval|mirror|log|activity|devices|device|device-log|provision|profiles|awake|ssh>');
    process.exit(cmd ? 1 : 0);
}
