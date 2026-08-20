// The Android half of the last mile: build, install, launch, read the log.
//
// ⚠ Unlike `ios.ts` this must run on WINDOWS. Everything here goes through `run`/`captureRun` (spawn, no
// shell) rather than `sh`, because `sh` needs `/bin/sh`.
import fs from 'node:fs';
import path from 'node:path';

import { run, captureRun, fail, argValue, splitArgs } from './exec.js';
import { platformTfm, projectDir, requireFields, type DeployConfig } from './config.js';

/**
 * Where `adb` is.
 *
 * ⚠ **`ANDROID_HOME` is not enough on Windows**: the SDK installed by Visual Studio lands in
 * `%LOCALAPPDATA%\Android\Sdk` and sets no environment variable at all. Without that fallback
 * `android doctor` reports "adb NOT FOUND" on a machine that has it — a true statement about PATH read as
 * a missing SDK.
 */
export function adbCandidates(env: NodeJS.ProcessEnv = process.env): string[] {
  const exe = process.platform === 'win32' ? 'adb.exe' : 'adb';
  const roots = [
    env.ANDROID_HOME,
    env.ANDROID_SDK_ROOT,
    env.LOCALAPPDATA ? path.join(env.LOCALAPPDATA, 'Android', 'Sdk') : undefined,
    env.HOME ? path.join(env.HOME, 'Library', 'Android', 'sdk') : undefined,   // macOS default
  ];
  return roots.filter((r): r is string => !!r).map((r) => path.join(r, 'platform-tools', exe));
}

function adbPath(): string {
  return adbCandidates().find((c) => fs.existsSync(c)) ?? 'adb';   // else PATH, or a clear ENOENT
}

/**
 * A usable JDK, from `JAVA_HOME` or from where the tooling actually puts one.
 *
 * 🔴 The Android build needs a JDK and reports its absence from deep inside an MSBuild target as a Java
 * error — which reads as a broken SDK, not as "set JAVA_HOME". Android Studio ships one in `jbr/` and sets
 * no variable, so the common case is a machine that HAS one and cannot say where.
 *
 * ⚠ Returns null rather than throwing: `doctor` wants to REPORT it, `deploy` wants to stop with a fix.
 */
export function resolveJdk(env: NodeJS.ProcessEnv = process.env): string | null {
  const exe = process.platform === 'win32' ? 'java.exe' : 'java';
  const usable = (dir?: string): dir is string => !!dir && fs.existsSync(path.join(dir, 'bin', exe));
  if (usable(env.JAVA_HOME)) return env.JAVA_HOME;

  const candidates = [
    env.ProgramFiles ? path.join(env.ProgramFiles, 'Android', 'Android Studio', 'jbr') : undefined,
    env['ProgramFiles(x86)']
      ? path.join(env['ProgramFiles(x86)']!, 'Android', 'Android Studio', 'jbr') : undefined,
    env.LOCALAPPDATA ? path.join(env.LOCALAPPDATA, 'Programs', 'Android Studio', 'jbr') : undefined,
    '/Applications/Android Studio.app/Contents/jbr/Contents/Home',                       // macOS
  ];
  return candidates.find((c): c is string => usable(c)) ?? null;
}

export interface AndroidDevice {
  serial: string;
  /** `device`, `offline`, `unauthorized` — the state adb reports. */
  state: string;
}

/**
 * Parse `adb devices`.
 *
 * ⚠ **`offline` and `unauthorized` are KEPT rather than filtered.** An unauthorized device is waiting for
 * a "trust this computer" tap on the phone, and silently ignoring it produces "no devices" while one is
 * plainly attached.
 */
export function parseDevices(out: string): AndroidDevice[] {
  return out.split(/\r?\n/)
    .slice(1)                                   // "List of devices attached"
    .map((l) => l.trim())
    .filter((l) => l.length > 0 && !l.startsWith('*'))
    .map((l) => l.split(/\s+/))
    .filter((parts) => parts.length >= 2)
    .map((parts) => ({ serial: parts[0]!, state: parts[1]! }));
}

/**
 * Pick the device to act on. REFUSES to guess when several are attached — silently taking the first
 * deploys to the wrong one and you debug the wrong build, and here an emulator and a phone are routinely
 * attached at once.
 */
function resolveDevice(wanted: string | undefined): AndroidDevice | null {
  const r = captureRun(adbPath(), ['devices']);
  if (r.status !== 0) {
    fail('could not run `adb devices`.',
      '  Install the Android SDK platform-tools, or set ANDROID_HOME.');
    return null;
  }
  const all = parseDevices(r.out);
  const ready = all.filter((d) => d.state === 'device');

  if (all.length === 0) {
    fail('no Android device or emulator is attached.',
      '  Start an emulator, or plug a phone in with USB debugging enabled.');
    return null;
  }
  if (ready.length === 0) {
    const states = all.map((d) => `${d.serial} (${d.state})`).join(', ');
    fail(`no device is ready: ${states}.`,
      '  `unauthorized` means the phone is waiting for you to tap "Allow USB debugging".');
    return null;
  }
  if (wanted) {
    const match = ready.find((d) => d.serial === wanted);
    if (match) return match;
    fail(`no ready device matches ${JSON.stringify(wanted)}.`);
    return null;
  }
  if (ready.length > 1) {
    fail('several devices are attached, so this will not guess which one you meant.',
      `  Pass --device <serial>:\n${ready.map((d) => `    ${d.serial}`).join('\n')}`);
    return null;
  }
  return ready[0]!;
}

export function cmdDevices(): void {
  const r = captureRun(adbPath(), ['devices']);
  if (r.status !== 0) {
    fail('could not run `adb devices`.', '  Install platform-tools, or set ANDROID_HOME.');
    return;
  }
  const all = parseDevices(r.out);
  if (all.length === 0) {
    console.log('shenora: no devices. Start an emulator, or plug a phone in with USB debugging on.');
    return;
  }
  for (const d of all) console.log(`  ${d.serial}  ${d.state}`);
}

/**
 * Build and install in one step, then launch.
 *
 * 🔴 **`-t:Install` rather than `adb install`**, because the .NET Android SDK owns the packaging: it
 * signs with the debug keystore, picks the ABI from the RID, and knows which of the produced apks to
 * push. Building and then installing by hand is where an ABI mismatch turns into
 * `INSTALL_FAILED_NO_MATCHING_ABIS` — an error that names the install while the fault is the build.
 */
export function cmdDeploy(cfg: DeployConfig, args: string[]): void {
  if (!requireFields(cfg, ['project', 'bundleId'])) return;
  if (!platformTfm(cfg, 'android')) return;
  // Reading from `own` keeps `argValue` from taking a passthrough token as a device serial.
  const { own, passthrough } = splitArgs(args);
  const device = resolveDevice(argValue(own, '--device'));
  if (!device) return;

  const jdk = resolveJdk();
  if (!jdk) {
    fail('no JDK found, and the Android build needs one.',
      '  Set JAVA_HOME to a JDK 17+. Android Studio ships one in its `jbr` folder.');
    return;
  }

  console.log(`shenora: building and installing to ${device.serial}…`);
  const build = run('dotnet', [
    'build', path.join(cfg.root, cfg.project),
    '-c', cfg.configuration,
    '-f', cfg.androidTfm,
    '-t:Install',
    `-p:AdbTarget=-s ${device.serial}`,
    '-v', 'minimal',
    ...passthrough,
  ], { cwd: cfg.root, env: { JAVA_HOME: jdk } });
  if (build.status !== 0) {
    fail('the build/install failed — see the output above.',
      '  INSTALL_FAILED_NO_MATCHING_ABIS means the RID does not match the device (an x86_64 emulator '
      + 'needs an x86_64 build).');
    return;
  }

  // `monkey` rather than `am start`: it needs only the package id and the LAUNCHER category, so the
  // adopter never names an activity class that MAUI generates.
  const launch = run(adbPath(), ['-s', device.serial, 'shell', 'monkey',
    '-p', cfg.bundleId, '-c', 'android.intent.category.LAUNCHER', '1'], { quiet: true });
  if (launch.status !== 0) {
    fail(`installed, but could not launch ${cfg.bundleId}.`,
      '  Check `bundleId` matches the ApplicationId the project builds.');
    return;
  }
  console.log(`\nshenora: running on ${device.serial}. Read its output with \`shenora android log\`.`);
}

/**
 * The app's own log.
 *
 * 🔴 **FILTER FIRST, TAIL HERE.** `adb logcat -t N` applies `-t` to the RAW buffer and only then the
 * filterspec, so `-t 60 -s SHENORA:V` reliably prints NOTHING once sixty lines of platform chatter have
 * gone by — which reads exactly like "my app logged nothing". This dumps the filtered buffer and slices
 * in the tool.
 */
export function cmdLog(cfg: DeployConfig, args: string[]): void {
  const device = resolveDevice(argValue(args, '--device'));
  if (!device) return;
  const lines = Number(argValue(args, '-n') ?? '80') || 80;
  const all = args.includes('--all');

  // 🔴 BY PID, NOT BY TAG. A tag filter needs to know how the app logs and there is no right answer: a
  // MAUI app using `Console.WriteLine` lands under `DOTNET`, one using `Android.Util.Log` under whatever
  // tag it chose — so a `DOTNET` default shows the runtime's cryptography chatter and none of the app's
  // verdicts, which reads as "my app logged nothing". The PID is every line the APP wrote under ANY tag,
  // and it excludes a STALE instance flooding the buffer.
  let filter: string[] = [];
  let how = 'everything';
  if (!all) {
    const pid = appPid(device.serial, cfg.bundleId);
    if (pid) {
      filter = [`--pid=${pid}`];
      how = `pid ${pid}`;
    } else {
      // Not running: fall back to the tag and SAY which one — "nothing" is about to be a legitimate
      // answer and the reader needs to know why.
      filter = ['-s', `${cfg.androidLogTag}:V`, 'AndroidRuntime:E'];
      how = `tag ${cfg.androidLogTag} — ${cfg.bundleId} is not running`;
    }
  }

  const r = captureRun(adbPath(), ['-s', device.serial, 'logcat', '-d', ...filter]);
  if (r.status !== 0) {
    fail('could not read the log.',
      '  ⚠ If this HANGS or comes back empty, check the emulator is still running — `adb logcat -d` '
      + 'against a device that has gone away does not error, it hangs.');
    return;
  }

  const body = r.out.trimEnd().split(/\r?\n/);
  console.log(`shenora: last ${Math.min(lines, body.length)} line(s) from ${device.serial} (${how})\n`);
  console.log(body.slice(-lines).join('\n'));
}

/** The running app's pid, or null. `pidof` answers nothing (and exit 1) when it is not running. */
function appPid(serial: string, packageId: string): string | null {
  if (!packageId) return null;
  const r = captureRun(adbPath(), ['-s', serial, 'shell', 'pidof', packageId]);
  const pid = r.out.trim().split(/\s+/)[0];
  return r.status === 0 && pid && /^\d+$/.test(pid) ? pid : null;
}

/**
 * A distributable. `-p:AndroidPackageFormat=aab` for Play, the default `apk` for anything else.
 *
 * ⚠ Release is the default: a Debug Android build is a different artifact, signed with the debug keystore
 * and not installable as an update over a release one.
 */
export function cmdBuild(cfg: DeployConfig, args: string[]): void {
  if (!requireFields(cfg, ['project'])) return;
  if (!platformTfm(cfg, 'android')) return;
  // ⚠ An ARRAY, spread into `run`'s argv. No shell, so nothing re-splits an argument containing a space —
  // the iOS half has to quote for `sh`, this one does not have the problem.
  const { own, passthrough } = splitArgs(args);
  const configuration = argValue(own, '--configuration') ?? 'Release';
  const aab = own.includes('--aab');
  if (passthrough.length) console.log(`shenora: extra build args: ${passthrough.join(' ')}`);

  console.log(`shenora: publishing ${cfg.project} (${cfg.androidTfm}, ${configuration}`
    + `${aab ? ', aab' : ''})…`);
  // Stamped BEFORE the publish: the SDK does not clean between runs, so anything in the output directory
  // older than this belongs to a previous one.
  const startedAt = Date.now();
  const r = run('dotnet', [
    'publish', path.join(cfg.root, cfg.project),
    '-c', configuration,
    '-f', cfg.androidTfm,
    ...(aab ? ['-p:AndroidPackageFormat=aab'] : []),
    ...passthrough,
  ], { cwd: cfg.root });
  if (r.status !== 0) {
    fail('the publish failed — see the output above.');
    return;
  }

  // `projectDir`, not `path.dirname` — a `project` naming a DIRECTORY resolves one level too high, which
  // reports "no .apk appeared" about a folder that never could hold one (see its doc).
  const dir = path.join(projectDir(cfg), 'bin', configuration, cfg.androidTfm, 'publish');
  const format = aab ? 'aab' : 'apk';
  const artifact = findPackage(dir, format, startedAt) ?? findPackage(path.dirname(dir), format, startedAt);
  if (!artifact) {
    // Say STALE when a leftover is what was found — "no .apk appeared" beside a directory visibly
    // containing one is the most confusing message this tool could print.
    const stale = findPackage(dir, format) ?? findPackage(path.dirname(dir), format);
    fail(stale
      ? `the publish reported success but the only .${format} under ${dir} predates this build `
        + `(${stale}) — it is left over from an earlier run, so nothing was produced this time.`
      : `the publish reported success but no .${format} appeared under ${dir}.`);
    return;
  }
  console.log(`\nshenora: ${artifact}`);
  console.log(`         ${(fs.statSync(artifact).size / (1024 * 1024)).toFixed(1)} MB`);
  console.log('\n  ⚠ Signed with the DEBUG keystore unless the project configures a release one — that '
    + 'is a project setting, not something this tool should invent.');
}

/**
 * The publish output, for the format that was actually ASKED FOR.
 *
 * Within a format, **`-Signed.apk` first**: the SDK leaves both and the unsigned one installs nowhere, so
 * handing back the wrong file is a failure the adopter meets minutes later, on a device.
 *
 * 🔴 **ACROSS formats it never substitutes.** Preferring `-Signed.apk` unconditionally makes
 * `android build --aab`, run in a directory still holding an APK from an earlier publish, report that APK
 * as the artifact complete with its size — and the user uploads it to Play believing it is the bundle they
 * just built.
 *
 * @param dir Directory to look in.
 * @param format What the caller built — `'aab'` accepts only a bundle, `'apk'` only an APK.
 * @param builtAfter Epoch ms; an artifact older than this is STALE and is not returned. The publish
 *   directory is not cleaned between runs, so without this a build that produced nothing hands back the
 *   previous run's output and every downstream step believes it succeeded.
 */
export function findPackage(dir: string, format: 'apk' | 'aab' = 'apk', builtAfter?: number): string | null {
  if (!fs.existsSync(dir)) return null;
  const entries = fs.readdirSync(dir);
  const pick = format === 'aab'
    ? entries.find((e) => e.endsWith('.aab'))
    : entries.find((e) => e.endsWith('-Signed.apk')) ?? entries.find((e) => e.endsWith('.apk'));
  if (!pick) return null;

  const full = path.join(dir, pick);
  if (builtAfter !== undefined) {
    // A whole second of slack: mtime resolution varies by filesystem, and a build finishing in the same
    // tick as the start stamp must not be called stale.
    try {
      if (fs.statSync(full).mtimeMs < builtAfter - 1000) return null;
    } catch {
      return null; // vanished between readdir and stat — treat as absent rather than crash
    }
  }
  return full;
}

/**
 * Can this machine build and deploy Android at all?
 *
 * ⚠ It asks about the MACHINE, so it must not need a config — "can this box do it?" is the question
 * someone has BEFORE they have a project wired.
 */
export function cmdDoctor(): void {
  const rows: [string, string][] = [];

  const dotnet = captureRun('dotnet', ['--version']);
  rows.push(['dotnet', dotnet.status === 0 ? dotnet.out.trim() : 'NOT FOUND']);

  const workloads = captureRun('dotnet', ['workload', 'list']);
  rows.push(['android workload',
    /android/i.test(workloads.out) ? 'installed' : 'MISSING — dotnet workload install android']);

  const adb = captureRun(adbPath(), ['version']);
  rows.push(['adb', adb.status === 0 ? (adb.out.split(/\r?\n/)[0] ?? 'ok').trim() : 'NOT FOUND']);

  // Reported as RESOLVED rather than as `JAVA_HOME` — `deploy` uses the same resolution, so this row is
  // the truth `deploy` will act on. See `resolveJdk` for what its absence looks like.
  const jdk = resolveJdk();
  rows.push(['jdk', jdk ?? 'NOT FOUND — set JAVA_HOME to a JDK 17+ (Android Studio ships one in `jbr`)']);

  const devices = captureRun(adbPath(), ['devices']);
  const ready = devices.status === 0 ? parseDevices(devices.out).filter((d) => d.state === 'device') : [];
  rows.push(['devices ready', ready.length ? ready.map((d) => d.serial).join(', ') : 'none attached']);

  const width = Math.max(...rows.map(([k]) => k.length));
  console.log('');
  for (const [k, v] of rows) console.log(`  ${k.padEnd(width)}  ${v}`);
}
