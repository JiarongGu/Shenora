// Drive a Mac over SSH to build and run the MAUI sample on iOS — the one target that cannot be built
// on this dev machine at all, because an iOS build requires Xcode and Xcode requires macOS.
//
//   node devtools/dev.mjs mac doctor         is the Mac reachable, and does it have what a .NET iOS build needs?
//   node devtools/dev.mjs mac setup          one-time: create the bare repo + working clone on the Mac
//   node devtools/dev.mjs mac push           push the current branch and reset the Mac's clone to it
//   node devtools/dev.mjs mac build          push, then `dotnet build -f net10.0-ios` for the simulator
//   node devtools/dev.mjs mac run            build, boot the simulator, install + launch
//   node devtools/dev.mjs mac shot [name]    screenshot the booted simulator, copy the PNG back here
//   node devtools/dev.mjs mac tap <x> <y>    tap it, in the coordinates of that screenshot
//   node devtools/dev.mjs mac type <text…>   type into whatever has focus
//   node devtools/dev.mjs mac log [-n N]     the sample's own log lines from the simulator
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
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
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

function build(cfg, { skipPush = false } = {}) {
  if (!skipPush) push(cfg);
  fs.mkdirSync(OUT, { recursive: true });
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
function metrics(cfg) {
  const script = [
    'tell application "System Events" to tell process "Simulator" to tell first group of first window',
    'set p to position',
    'set s to size',
    'return ("" & (item 1 of p) & " " & (item 2 of p) & " " & (item 1 of s) & " " & (item 2 of s))',
    'end tell',
  ].map((l) => `-e ${q(l)}`).join(' ');
  // Geometry and screenshot size are read TOGETHER in one command: they are two halves of the same
  // ratio, and a round trip each is most of a tap's latency.
  const r = ssh(cfg, `osascript ${script}; `
    + 'xcrun simctl io booted screenshot --type=png /tmp/shenora-geom.png >/dev/null 2>&1 && '
    + 'sips -g pixelWidth -g pixelHeight /tmp/shenora-geom.png | awk \'/pixel/{print $2}\'');
  const [x, y, w, h, sw, sh] = r.stdout.trim().split(/\s+/).map(Number);
  if (![x, y, w, h, sw, sh].every(Number.isFinite) || !sw || !sh) {
    console.error(`mac: could not read the Simulator window geometry — is the Simulator running, and has the
ssh session been granted Accessibility permission (System Settings -> Privacy & Security -> Accessibility)?
Without it every click silently does nothing.\n${r.stdout}${r.stderr}`);
    process.exit(1);
  }
  return { x, y, w, h, sw, sh };
}

function tap(cfg, px, py) {
  const m = metrics(cfg);
  const x = Math.round(m.x + Number(px) * (m.w / m.sw));
  const y = Math.round(m.y + Number(py) * (m.h / m.sh));
  // Prefer cliclick when present — `System Events click at` intermittently only FOCUSES a web button
  // on the sim rather than clicking it; cliclick lands a real click every time via CGEvent. Falls back
  // for hosts that do not have it installed.
  const has = ssh(cfg, 'command -v cliclick >/dev/null 2>&1 && echo YES || echo NO');
  ssh(cfg, `osascript -e ${q('tell application "Simulator" to activate')}`);
  if ((has.stdout ?? '').trim().startsWith('YES')) {
    ssh(cfg, `cliclick c:${x},${y}`, { check: true });
  } else {
    ssh(cfg, `osascript -e ${q('delay 0.15')} `
      + `-e ${q(`tell application "System Events" to tell process "Simulator" to click at {${x}, ${y}}`)}`, { check: true });
  }
  console.log(`mac: tapped screenshot (${px}, ${py}) -> desktop (${x}, ${y})`);
}

function typeText(cfg, text) {
  // keystroke must be addressed to the Simulator PROCESS; sending it to System Events itself errors -25204.
  ssh(cfg, `osascript -e ${q('tell application "Simulator" to activate')} -e ${q('delay 0.3')} `
    + `-e ${q(`tell application "System Events" to tell process "Simulator" to keystroke ${JSON.stringify(text)}`)}`,
    { check: true });
  console.log(`mac: typed ${text.length} chars`);
}

// ---------------------------------------------------------------- log
//
// The simulator's unified log, filtered to this app. `log show --last` rather than `log stream`: a
// stream never returns and would hang an unattended session — the same reason android.mjs tails a
// bounded slice instead of following.
function log(cfg, n = 80) {
  const r = ssh(cfg, `xcrun simctl spawn booted log show --last 5m --style compact `
    + `--predicate ${q(`processImagePath CONTAINS "Shenora" OR eventMessage CONTAINS "[Shenora"`)} 2>/dev/null | tail -${Number(n) || 80}`);
  const out = (r.stdout ?? '').trim();
  console.log(out || 'mac: no matching log lines in the last 5 minutes (is the app running?)');
}

// ---------------------------------------------------------------- awake
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
  case 'tap': tap(cfg, rest[0], rest[1]); break;
  case 'type': typeText(cfg, rest.join(' ')); break;
  case 'log': {
    const i = rest.indexOf('-n');
    log(cfg, i >= 0 ? rest[i + 1] : 80);
    break;
  }
  case 'awake': awake(cfg, rest[0]); break;
  case 'ssh': ssh(cfg, rest.join(' '), { inherit: true, check: true }); break;
  default:
    console.log('usage: node devtools/dev.mjs mac <doctor|setup|push|build|run|shot|tap|type|log|awake|ssh>');
    process.exit(cmd ? 1 : 0);
}
