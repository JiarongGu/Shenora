// Android device loop for the MAUI sample — the mobile twin of `dev.mjs sample`.
//
//   android devices                 list attached devices (starts the adb server if needed)
//   android connect <host:port>     attach an emulator's adb bridge
//   android deploy [--device id]    build + install the sample APK
//   android run    [--device id]    deploy, then launch it
//   android log    [--device id] [--all] [-n N]   the app's log (SHENORA tag; --all = everything)
//   android shot   [name] [--device id]           screenshot -> devtools/_android/
//
// Everything here exists because the loop was run by hand first and each step cost something:
//
//  * The Android TFM needs a JDK. Unset JAVA_HOME makes MSBuild emit a bare `error XA5300` pointing
//    at an install page, on a machine that already has one because Android Studio ships it. dev.mjs
//    resolves it; this reuses that.
//  * `-p:RuntimeIdentifier=android-x64` is not optional for an emulator. Most are x86_64 while a
//    default build can produce arm64 only, and the install then fails INSTALL_FAILED_NO_MATCHING_ABIS
//    — which reads like a packaging fault rather than the wrong architecture.
//  * A screenshot must NOT be piped. `adb exec-out screencap -p > file.png` is corrupted by
//    PowerShell redirection (BOM + re-encoding); capture on the device and `adb pull` the bytes.
//  * No adb port is hardcoded. An emulator's bridge port comes from ITS OWN manager, so `connect`
//    takes it as an argument rather than this file guessing at a vendor default.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import config from '../project.config.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const outDir = path.join(repo, 'devtools', '_android');

/** The SDK's adb: ANDROID_HOME/ANDROID_SDK_ROOT, then the default install, then PATH. */
function adbPath() {
  const roots = [process.env.ANDROID_HOME, process.env.ANDROID_SDK_ROOT,
    process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Android', 'Sdk') : null];
  for (const root of roots) {
    if (!root) continue;
    const candidate = path.join(root, 'platform-tools', 'adb.exe');
    if (fs.existsSync(candidate)) return candidate;
  }
  return 'adb'; // on PATH, or the caller gets a clear ENOENT
}

const adb = adbPath();

function run(exe, argv, opts = {}) {
  return spawnSync(exe, argv, { stdio: 'inherit', cwd: repo, ...opts }).status === 0;
}
function capture(argv) {
  const r = spawnSync(adb, argv, { encoding: 'utf8', cwd: repo });
  return (r.stdout ?? '') + (r.stderr ?? '');
}

/** Attached device ids, excluding the "List of devices" header and anything offline/unauthorized. */
function devices() {
  return capture(['devices']).split('\n').slice(1)
    .map((line) => line.trim())
    .filter((line) => line.endsWith('\tdevice'))
    .map((line) => line.split('\t')[0]);
}

/**
 * The device to act on. Explicit `--device` wins; otherwise exactly ONE attached device is required.
 * Refusing when several are attached is deliberate — silently picking the first would install to
 * whichever adb happened to list first, and the mistake is invisible until you look at the wrong screen.
 */
function target(args) {
  const flag = args.indexOf('--device');
  if (flag >= 0 && args[flag + 1]) return args[flag + 1];
  const attached = devices();
  if (attached.length === 1) return attached[0];
  if (attached.length === 0) {
    console.error('android: no device attached. Start your emulator (or plug in a phone), then:\n' +
      '  node devtools/dev.mjs android connect <host:port>   (an emulator bridge — its manager reports the port)\n' +
      '  node devtools/dev.mjs android devices');
    return null;
  }
  console.error(`android: ${attached.length} devices attached — name one with --device <id>:\n  ` +
    attached.join('\n  '));
  return null;
}

const [sub, ...rest] = process.argv.slice(2);
const project = path.join(repo, ...config.androidSampleProject.split('/'));
const csproj = path.join(project, path.basename(project) + '.csproj');

switch (sub) {
  case 'devices':
    run(adb, ['start-server']);
    run(adb, ['devices', '-l']);
    break;

  case 'connect': {
    const endpoint = rest[0];
    if (!endpoint) {
      console.error('usage: android connect <host:port>   (your emulator manager reports its adb port)');
      process.exitCode = 1;
      break;
    }
    if (!run(adb, ['connect', endpoint])) process.exitCode = 1;
    run(adb, ['devices', '-l']);
    break;
  }

  case 'deploy':
  case 'run': {
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }

    // Reuse dev.mjs's JDK resolution rather than duplicating the probe: one owner for "where is the
    // JDK", the same reason the kit has one owner for UI marshalling.
    const jdk = spawnSync('node', [path.join(repo, 'devtools', 'dev.mjs'), 'android-jdk'],
      { encoding: 'utf8', cwd: repo });
    const javaHome = (jdk.stdout ?? '').trim();
    if (!javaHome) { process.exitCode = 1; break; }
    const env = { ...process.env, JAVA_HOME: javaHome };

    console.log(`android: deploying to ${device}…`);
    const ok = run('dotnet', ['build', csproj, '-f', config.androidTfm, '-t:Install',
      `-p:AdbTarget=-s ${device}`, `-p:RuntimeIdentifier=${config.androidRuntimeIdentifier}`,
      '-v', 'minimal'], { env });
    if (!ok) { process.exitCode = 1; break; }

    if (sub === 'run') {
      console.log('android: launching…');
      run(adb, ['-s', device, 'shell', 'monkey', '-p', config.androidPackageId,
        '-c', 'android.intent.category.LAUNCHER', '1'], { stdio: 'ignore' });
      console.log(`android: launched. Follow the log with:\n  node devtools/dev.mjs android log`);
    }
    break;
  }

  case 'log': {
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }
    const lines = rest.includes('-n') ? Number(rest[rest.indexOf('-n') + 1]) : 0;
    // One tag by default: the sample logs everything under it, so the run reads as a story instead
    // of being buried in platform chatter. --all when you need the platform's side too.
    const filter = rest.includes('--all') ? [] : ['-s', `${config.androidLogTag}:V`, 'AndroidRuntime:E'];

    if (!lines) { run(adb, ['-s', device, 'logcat', ...filter]); break; }

    // Tail in HERE, not with logcat's -t. `-t N` prints the last N lines of the RAW buffer and the
    // filterspec is applied afterwards, so `-t 60 -s SHENORA:V` reliably prints NOTHING once 60
    // lines of platform chatter have gone by — which is exactly how it behaved the first time.
    const dump = capture(['-s', device, 'logcat', '-d', ...filter]).trimEnd().split('\n');
    console.log(dump.slice(-lines).join('\n'));
    break;
  }

  case 'shot': {
    const named = rest.find((a) => !a.startsWith('-')) ?? 'android';
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }
    fs.mkdirSync(outDir, { recursive: true });
    const remote = `/sdcard/${config.shotPrefix}-${named}.png`;
    const local = path.join(outDir, `${named}.png`);
    // Capture ON the device then pull: a piped `exec-out screencap` is corrupted by the shell's
    // text redirection on Windows (BOM + re-encoding), producing a PNG nothing can open.
    if (!run(adb, ['-s', device, 'shell', 'screencap', '-p', remote], { stdio: 'ignore' })) {
      process.exitCode = 1; break;
    }
    if (!run(adb, ['-s', device, 'pull', remote, local], { stdio: 'ignore' })) { process.exitCode = 1; break; }
    run(adb, ['-s', device, 'shell', 'rm', remote], { stdio: 'ignore' });
    console.log(`android: ${path.relative(repo, local)}`);
    break;
  }

  default:
    console.error('usage: node devtools/dev.mjs android <devices|connect <host:port>|deploy|run|log|shot [name]> [--device id]');
    process.exitCode = sub ? 1 : 0;
}
