// The iOS loop: doctor · devices · simulators · deploy (simulator or hardware) · log · shot.
//
// 🔴 WHY THIS SHIPS. A hybrid framework's real measure is how little native work an adopting app has to
// redo — and the device loop is part of that. Every check below exists because this kit hit the failure
// it catches, on real hardware, and each one costs a day to rediscover.
//
// ⚠ It runs on this Mac, or on one reached over ssh through the `Target` seam (`./remote/`) — see
// `docs/design/cli-remote.md`. The one thing that differs is signing: it needs the login keychain, which
// an ssh session cannot reach (a different audit session), so a DEVICE build on a remote target hands off
// through `target.gui` instead of `target.sh`. On a local Mac you are already in a GUI session, so nothing
// extra happens there.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { q, fail, argValue, splitArgs, shellPassthrough } from './exec.js';
import { iosTfmOf, platformTfm, projectDir, requireFields, type DeployConfig } from './config.js';
import { resolveTarget, resolveHost, diagnoseHost, reportDiagnosis } from './remote/host.js';
import type { Target } from './remote/target.js';
import { pushTree } from './remote/push.js';

interface Device {
  id: string;
  name: string;
  state: string;
  os: string;
}

/**
 * Either the devices, or WHY we could not find out.
 *
 * 🔴 **The distinction is the whole point of this type.** Every failure used to collapse to an empty
 * array — devicectl missing, devicectl erroring, unparseable output — and the callers then said "no
 * iPhone is connected. Plug it in, unlock it, tap Trust." That is a confident statement about the user's
 * HARDWARE made when the truth is the tool failed, and this file's own doc already calls it "the single
 * worst answer this tool can give". It fixed ONE cause of it (the stdout pipe) and left the rest.
 */
export type DeviceLookup =
  | { ok: true; devices: Device[] }
  | { ok: false; detail: string };

/**
 * Parse `devicectl list devices --json-output`. Split out because it is the half that can be tested off
 * macOS, and because an unparseable answer must be distinguishable from an empty one: a run with no
 * phones attached still writes a valid document with an empty array, so nothing parseable is a FAILURE.
 */
export function parseDeviceList(json: string): DeviceLookup {
  const start = json.indexOf('{');
  if (start < 0) return { ok: false, detail: 'devicectl produced no JSON document' };
  try {
    const parsed = JSON.parse(json.slice(start)) as {
      result?: { devices?: Array<Record<string, any>> };
    };
    if (!parsed.result || !Array.isArray(parsed.result.devices)) {
      return { ok: false, detail: 'devicectl output had no result.devices array' };
    }
    return {
      ok: true,
      devices: parsed.result.devices
        .map((d) => ({
          id: String(d['identifier'] ?? ''),
          name: String(d['deviceProperties']?.name ?? '(unnamed)'),
          state: describeConnection(d['connectionProperties']),
          os: String(d['deviceProperties']?.osVersionNumber ?? ''),
        }))
        .filter((d) => d.id),
    };
  } catch (error) {
    return { ok: false, detail: `devicectl output could not be parsed — ${(error as Error).message}` };
  }
}

/**
 * Connected iPhones, as devicectl reports them — or why the question could not be answered.
 *
 * 🔴 **The JSON goes to a TEMP FILE, never `/dev/stdout`.** That was the original shape and it reported
 * "no devices" with a paired iPhone 17 Pro sitting there — the single worst answer this tool can give,
 * because it looks like a fact about your phone rather than a bug in the reader. It survived because it
 * WORKS interactively: over ssh (or any tty) stdout is a terminal and the write lands, so every manual
 * check agreed with the code. Under `spawnSync` stdout is an anonymous pipe, `devicectl` cannot open it
 * the way it wants, and the command yields nothing — which this function could not distinguish from
 * "no devices attached".
 *
 * ⚠ The lesson generalises past this call: **a tool that only reads correctly from a terminal is broken
 * everywhere it actually runs.** Prefer a file an implementation cannot be clever about. And the answer
 * it gives now distinguishes the two cases, which is the other half of the same lesson.
 */
function devices(target: Target): DeviceLookup {
  const out = `/tmp/shenora-devicectl-${process.pid}.json`;
  try {
    // ⚠ stderr is CAPTURED rather than redirected to /dev/null: it is the only place devicectl says
    // why it refused, and the callers now have somewhere to put that. `quiet` keeps it off a healthy run.
    const r = target.sh(`xcrun devicectl list devices --json-output ${q(out)}`, { quiet: true });
    if (r.status !== 0) {
      return { ok: false, detail: r.out.trim() || `xcrun devicectl exited ${r.status}` };
    }
    const json = target.probe(`cat ${q(out)} 2>/dev/null`);
    return parseDeviceList(json);
  } catch (error) {
    return { ok: false, detail: `could not run devicectl — ${(error as Error).message}` };
  } finally {
    // A leftover temp file is not worth failing over.
    target.sh(`rm -f ${q(out)}`, { quiet: true });
  }
}

/**
 * How a phone is reachable, in the terms that decide whether you can deploy to it.
 *
 * 🔴 **`tunnelState` is NOT that, and reading it is why this printed `disconnected` beside a perfectly
 * usable phone.** The tunnel is a debug channel brought up ON DEMAND, so an idle device that is paired,
 * powered and on the same LAN reports `disconnected` — and the honest reading of that word is "go find a
 * cable", which cost a round here. **`pairingState` is the field that answers "can I deploy to this".**
 *
 * `transportType` is reported alongside it because it changes what you should expect rather than whether
 * it works: `localNetwork` is genuinely supported, and also the transport that drops mid-operation
 * (`peer is no longer reachable`) on anything long. Naming it lets a developer attribute that failure to
 * the transport instead of to their build.
 */
export function describeConnection(connection: Record<string, any> | undefined): string {
  const pairing = String(connection?.pairingState ?? 'unknown');
  const transport = String(connection?.transportType ?? '');
  return transport ? `${pairing} via ${transport}` : pairing;
}

/**
 * Pick the device to act on. REFUSES to guess when several are connected — silently taking the first one
 * deploys to the wrong phone and you then debug the wrong build. The Android half of this kit learned
 * that expensively.
 */
function resolveDevice(target: Target, wanted: string | undefined): Device | null {
  const lookup = devices(target);
  // The tool failed — say THAT, rather than a confident claim about the phone on the desk.
  if (!lookup.ok) {
    fail('could not ask this Mac which devices are connected.',
      `  devicectl failed, so this is NOT "no phone is attached":\n\n${lookup.detail}`);
    return null;
  }
  const found = lookup.devices;
  if (found.length === 0) {
    fail('no iPhone is connected.',
      '  Plug it in, unlock it, tap Trust. `shenora ios devices` lists what the Mac can see.');
    return null;
  }
  if (wanted) {
    const match = found.find((d) => d.id === wanted || d.name === wanted);
    if (match) return match;
    fail(`no connected device matches ${JSON.stringify(wanted)}.`);
    return null;
  }
  if (found.length > 1) {
    fail('several devices are connected, so this will not guess which one you meant.',
      `  Pass --device <name|id>:\n${found.map((d) => `    ${d.name}  (${d.id})`).join('\n')}`);
    return null;
  }
  return found[0]!;
}

export function cmdDevices(cfg: DeployConfig | null, args: readonly string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  const lookup = devices(target);
  if (!lookup.ok) {
    fail('could not ask this Mac which devices are connected.',
      `  devicectl failed, so this is NOT "no phone is attached":\n\n${lookup.detail}`);
    return;
  }
  if (lookup.devices.length === 0) {
    console.log('shenora: no devices. Plug a phone in, unlock it, tap Trust.');
    return;
  }
  for (const d of lookup.devices) console.log(`  ${d.name}  iOS ${d.os}  ${d.state}  ${d.id}`);
}

export function cmdSimulators(cfg: DeployConfig | null, args: readonly string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  // The list and the filter are SEPARATE steps: piped through grep, a failed `xcrun` and a genuinely
  // empty list both came back as '' — and "no simulators installed" beside a broken xcode-select sends
  // the reader to install components they already have.
  const list = target.sh('xcrun simctl list devices available', { quiet: true });
  if (list.status !== 0) {
    fail('could not list simulators — `xcrun simctl` itself failed.',
      `  Usually xcode-select points at a missing or stale Xcode; \`xcode-select -p\` shows which.\n\n${list.out.trim()}`);
    return;
  }
  const rows = list.out.split('\n').filter((l) => /^ {4}\S/.test(l)).map((l) => l.trimStart());
  console.log(rows.length ? rows.join('\n') : 'shenora: no simulators installed — Xcode > Settings > Components.');
}

export function cmdDoctor(cfg: DeployConfig | null, args: readonly string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;

  // 🔴 THE TRANSPORT IS DIAGNOSED FIRST, and this ordering is the whole value of a remote doctor. Every
  // probe below asks the Mac a question and reads silence as "not installed" — so against a Mac that is
  // merely ASLEEP this command prints `MISSING Xcode`, `MISSING .NET SDK`, `MISSING ios workload`, which
  // is a confident and completely wrong report about a machine that is fine. One reachability question,
  // asked before any of them, is the difference between that and "your Mac is not answering, here is
  // which of the six causes it is".
  if (target.isRemote) {
    const host = resolveHost(cfg, args);
    if (host) {
      const diagnosis = diagnoseHost(host);
      if (diagnosis.verdict !== 'ok') {
        reportDiagnosis(diagnosis);
        return;
      }
      console.log(`  ok      ${'reached'.padEnd(20)} ${target.label}`);
    }
  }

  let ok = true;
  // 🔴 COUNTED, because the worst outcome this command has is silence plus exit 0 — it reads as
  // "nothing to report, everything is fine" and the operator moves on. Reported by the first adopter:
  // `npx shenora ios doctor` printed nothing and succeeded while the same code through the binary
  // printed the full report. Whatever swallows the output, a doctor that reported NOTHING has not
  // examined anything, and must not say so with a zero exit.
  let written = 0;
  const line = (label: string, value: string, good = true): void => {
    console.log(`  ${good ? 'ok     ' : 'MISSING'} ${label.padEnd(20)} ${value}`);
    written++;
    if (!good) ok = false;
  };

  // WHICH binary is answering — the first question when a report looks wrong or absent, and the one an
  // operator cannot ask from outside (`npx` may resolve a different copy than the one just installed).
  line('shenora cli', `${cliVersion()}  ${cliEntry()}`);

  const xcode = target.probe('xcodebuild -version | head -1');
  line('Xcode', xcode || '(not found — install it from the App Store)', Boolean(xcode));

  const dotnet = target.probe('dotnet --version');
  line('.NET SDK', dotnet || '(not found)', Boolean(dotnet));

  const workload = target.probe('dotnet workload list 2>/dev/null | grep -i ios | head -1');
  line('ios workload', workload || '(run `dotnet workload install maui-ios`)', Boolean(workload));

  // An "Apple Development" identity is what a DEVICE build signs with. Absent, the build fails late with
  // a codesign error that reads as a project problem rather than a machine one.
  // Counted HERE, not with `grep -c`: grep exits 1 for zero matches, so a locked keychain (`security`
  // failing outright) and a genuinely empty identity list were the same '' — and "none, go to Xcode
  // Settings" about a keychain problem sends the reader to a screen that is already correct.
  const identity = target.sh('security find-identity -v -p codesigning', { quiet: true });
  const identityCount = identity.status === 0 ? (identity.out.match(/Apple Development/g)?.length ?? 0) : 0;
  line('signing identity',
    identity.status !== 0
      ? '(could not ask — `security` failed; a locked login keychain does this. Unlock it and retry)'
      : identityCount > 0 ? `${identityCount} found` : '(none — Xcode > Settings > Accounts)',
    identityCount > 0);

  // 🔴 A CERTIFICATE IS NOT AN ACCOUNT, AND NEITHER IS A PROFILE — all three decide whether a DEVICE
  // build can sign, and the row above is the one that stays green longest. Measured on a Mac reporting
  // `1 found` → `ready` that could not sign at all: valid certificate, a free personal team, and NO
  // Xcode Apple ID and NO provisioning profiles. The build then dies on `No Accounts: Add a new account
  // in Accounts settings` — after a full compile.
  const signing = describeDeviceSigning({
    identities: identity.status === 0 ? identityCount : null,
    accounts: xcodeAccountCount(target),
    profiles: provisioningProfileCount(target),
  });
  line('device signing', signing.text, signing.good);

  // 🔴 PREDICTED, not discovered at minute twenty. Every row above can say `ok` on a Mac that cannot
  // build at all: the workload and Xcode are each fine, and only their PAIRING fails, at build time.
  // Measured: no workload band ships a pack for Xcode 26.3 (only 26.0, 26.6, 27.0), so `dotnet workload
  // update` merely changes WHICH Xcode is demanded — the answer is to pin a band the Xcode satisfies.
  const sdk = xcodeSdkVersion(target);
  const bands = cfg ? iosBindingBands(target, iosTfmOf(cfg)) : [];
  if (sdk && bands.length > 0) {
    const newest = [...bands].sort(compareVersions).at(-1)!;
    const usable = pickBindingBand(bands, sdk);
    if (compareVersions(newest, sdk) <= 0) {
      line('ios bindings', `${newest} ≤ Xcode SDK ${sdk}`);
    } else if (usable) {
      line('ios bindings',
        `newest is ${newest} but Xcode SDK is ${sdk} — pin <TargetPlatformVersion>${usable}</…> in the csproj`,
        false);
      console.log(`          (a build works pinned to ${usable}; the newest bindings name APIs this Xcode `
        + 'has never shipped.');
      console.log('           In the PROJECT, not -p: on the command line — a global property reaches the');
      console.log('           non-platform projects too, and they cannot take one.)');
    } else {
      line('ios bindings',
        `every installed band (${bands.join(', ')}) is newer than Xcode SDK ${sdk} — no build can succeed`, false);
    }
  }

  // ⚠ A devicectl failure is reported as a failure here too, and NOT as `good: false`: doctor answers
  // "can this machine build and deploy", and a device is optional for that — the simulator path works
  // without one. Saying the reader broke is information; failing the whole check over it is not.
  const lookup = devices(target);
  line('device',
    !lookup.ok
      ? `(could not ask — devicectl failed: ${lookup.detail.split('\n')[0] ?? 'no detail'})`
      : lookup.devices.length ? lookup.devices.map((d) => d.name).join(', ')
        : '(none connected — simulator still works)',
    true);

  if (cfg) {
    line('project', cfg.project || '(unset)', Boolean(cfg.project));
    line('bundleId', cfg.bundleId || '(unset)', Boolean(cfg.bundleId));
  } else {
    console.log('  note    config               (none found — run `shenora init`)');
  }

  console.log(ok ? '\nshenora: ready.' : '\nshenora: not ready — see MISSING above.');
  if (!ok) process.exitCode = 1;

  // The invariant, checked last: a report of nothing is never a pass. Written to STDERR on purpose —
  // if stdout is what went missing, saying so on stdout would vanish with it.
  if (written === 0) {
    console.error('\nshenora: doctor examined nothing — this is a BUG in the tool or its packaging, '
      + 'not a clean bill of health.');
    console.error(`  running: ${cliVersion()}  ${cliEntry()}`);
    process.exitCode = 1;
  }
}

/** Compare dotted numeric versions — `26.10` is ABOVE `26.5`, which a string compare gets backwards. */
function compareVersions(a: string, b: string): number {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}

/**
 * The newest installed binding band the Xcode can actually satisfy, or null when none can.
 *
 * 🔴 THE ASYMMETRY IS THE WHOLE FIX, and it is measured rather than reasoned. The SDK picks the NEWEST
 * installed bindings, so on a Mac whose Xcode is older than them every build dies on a wall of
 * `MT4162 … not available in iOS 26.2 (introduced in 26.4)` — and `MtouchLink=SdkOnly` cannot help,
 * because `ManagedRegistrar` walks every binding regardless. But:
 *
 *     bindings NEWER than the SDK → impossible (they name APIs that do not exist)
 *     bindings OLDER than the SDK → fine (everything they name still exists)
 *
 * so pinning `TargetPlatformVersion` to the newest band at-or-below the SDK builds, links, signs and
 * installs on hardware. Verified on Xcode 26.3 with bands 26.0/26.6/27.0 installed: 26.0 works.
 *
 * ⚠ A dev-loop unblock, and the choice must be VISIBLE — silently building against old bindings would
 * hide a missing API until runtime. An App Store build should still match the pair.
 */
export function pickBindingBand(bands: string[], sdkVersion: string): string | null {
  const usable = bands.filter((b) => compareVersions(b, sdkVersion) <= 0);
  if (usable.length === 0) return null;
  return usable.sort(compareVersions)[usable.length - 1]!;
}

/**
 * Can this Mac sign for a DEVICE? Pure, so the rule is testable off macOS — the platform half is two
 * lookups either side of it.
 *
 * 🔴 The three facts are not interchangeable and only one of them is what `security find-identity`
 * answers. A CERTIFICATE proves a key exists; an ACCOUNT is what `-allowProvisioningUpdates` needs to
 * MINT or refresh a profile; a PROFILE is what the device demands at install. Certificate-only is the
 * trap this exists for, because it is also the state a working machine decays into: a free personal
 * team's profile expires after 7 days, and refreshing it needs the account that is missing.
 *
 * @param identities how many codesigning identities the keychain holds; null when `security` failed.
 * @param accounts   Xcode Apple IDs; null when the preference could not be read.
 * @param profiles   installed provisioning profiles across both stores.
 */
export function describeDeviceSigning(
  { identities, accounts, profiles }: { identities: number | null; accounts: number | null; profiles: number },
): { text: string; good: boolean } {
  // Certificate already has its own row and its own remedy; do not double-report it.
  if (identities === null) return { text: '(unknown — the identity check above could not run)', good: true };
  if (identities === 0) return { text: '(no certificate — see the row above)', good: false };
  if (accounts === null) {
    return { text: `(could not read Xcode's account list; ${profiles} profile(s) installed)`, good: true };
  }
  if (accounts === 0) {
    return {
      text: profiles > 0
        // A profile without an account works until it expires and can never be refreshed — worth a
        // distinct message, because "it built yesterday" is exactly how this is discovered.
        ? `❌ no Xcode Apple ID — ${profiles} profile(s) will work until they expire and cannot be refreshed`
        : '❌ certificate but NO Xcode Apple ID and no profiles — a device build dies at signing, after '
          + 'the full compile. Xcode > Settings > Accounts, add your Apple ID.',
      good: false,
    };
  }
  return {
    text: profiles > 0
      ? `${accounts} account(s), ${profiles} profile(s)`
      // An account with no profile is FINE: -allowProvisioningUpdates mints one. Said plainly so the
      // zero does not read as the failure above.
      : `${accounts} account(s), no profile yet — one is minted on the first device build`,
    good: true,
  };
}

/**
 * The iOS binding bands installed for this TFM's .NET version, from the SDK's own packs directory
 * (`Microsoft.iOS.Sdk.net10.0_26.0` → `26.0`).
 *
 * ⚠ Filtered by the NET version on purpose: a machine carrying `net9.0_18.0` beside `net10.0_26.5`
 * would otherwise offer a band that cannot build this app at all.
 */
function iosBindingBands(target: Target, tfm: string): string[] {
  const net = /^net(\d+\.\d+)/.exec(tfm)?.[1];
  const root = target.probe('dirname "$(readlink -f "$(command -v dotnet)")"');
  if (!net || !root) return [];
  const packs = target.join(root, 'packs');
  if (!target.exists(packs)) return [];
  return target.list(packs)
    .map((name) => new RegExp(`^Microsoft\\.iOS\\.Sdk\\.net${net.replace('.', '\\.')}_(\\d+\\.\\d+)$`).exec(name)?.[1])
    .filter((band): band is string => Boolean(band));
}

/** The installed Xcode's iPhoneOS SDK version, or '' when it cannot be asked. */
function xcodeSdkVersion(target: Target): string {
  return target.probe('xcrun --sdk iphoneos --show-sdk-version');
}

/** Xcode's known Apple IDs, or null when the preference cannot be read. */
function xcodeAccountCount(target: Target): number | null {
  const raw = target.probe('defaults read com.apple.dt.Xcode DVTDeveloperAccountManagerAppleIDLists');
  if (!raw) return null;
  // The value is a plist dict of arrays; an empty account list prints as `( )` per key. Counting the
  // entries rather than parsing the plist keeps this to one cheap read.
  const entries = raw.match(/"[^"]+@[^"]+"/g);
  return entries ? entries.length : 0;
}

/** Installed provisioning profiles across BOTH stores Xcode has used. */
function provisioningProfileCount(target: Target): number {
  // `os.homedir()` would answer about THIS machine, not the target's — `echo $HOME` asks the Mac
  // actually doing the build, local or remote alike.
  const home = target.probe('echo $HOME');
  if (!home) return 0;
  const stores = [
    `${home}/Library/Developer/Xcode/UserData/Provisioning Profiles`,
    `${home}/Library/MobileDevice/Provisioning Profiles`,
  ];
  let found = 0;
  for (const dir of stores) {
    // An unreadable store counts as none — `target.exists`/`target.list` already treat failure that
    // way, and the caller's message covers both.
    if (target.exists(dir)) {
      found += target.list(dir).filter((f) => f.endsWith('.mobileprovision')).length;
    }
  }
  return found;
}

/** This CLI's own version, read from the package it was loaded from. `(unknown)` rather than throwing. */
function cliVersion(): string {
  try {
    const manifest = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'package.json');
    return `v${(JSON.parse(fs.readFileSync(manifest, 'utf8')) as { version?: string }).version ?? '?'}`;
  } catch { return '(version unknown)'; }
}

/** Where this CLI is actually running FROM — the answer to "did npx run the copy I just installed?". */
function cliEntry(): string {
  try { return fileURLToPath(import.meta.url); } catch { return '(entry unknown)'; }
}

/**
 * The simulator RID follows THIS MAC's architecture, not the phone's — Apple Silicon runs arm64
 * simulators, Intel runs x64. Getting it wrong builds something that cannot install, with an error that
 * blames the app.
 */
function simulatorRid(): string {
  return process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64';
}

/**
 * The project directory ON THE BUILD MACHINE. Local matches `projectDir(cfg)` exactly — same machine,
 * same answer. A remote target's checkout is a SEPARATE tree the adopter keeps in sync themselves (there
 * is no push step yet), so this takes `cfg.remote.dir` when set, or guesses `~/<basename of cfg.root>` —
 * resolved via `echo $HOME` rather than `os.homedir()`, which would answer about THIS machine.
 */
const remoteHomes = new Map<string, string>();

/**
 * The REPOSITORY ROOT on the build machine — the counterpart of `cfg.root`, not of `projectDir`.
 *
 * 🔴 The distinction cost a real remote build. Collapsed into one helper, the local answer was the
 * PROJECT's directory and the remote answer was the REPO ROOT, and handing the root to `dotnet build`
 * builds whatever solution sits there: the first remote run went off to compile the Windows sample and
 * the test project, and failed with `NETSDK1100: To build a project targeting Windows on this operating
 * system` — an error about a project nobody asked for, on a machine that was working perfectly.
 */
function remoteRoot(cfg: DeployConfig, target: Target): string {
  const dir = cfg.remote?.dir?.trim();
  if (dir) return dir;

  // ⚠ Memoised per host: this is called from findApp, build, publish and the artifact checks, and every
  // miss is a fresh ssh connection at roughly two seconds.
  let home = remoteHomes.get(target.label);
  if (home === undefined) {
    home = target.probe('echo $HOME');
    remoteHomes.set(target.label, home);
  }
  // 🔴 An empty probe must NOT become a path. `''` + '/' + basename is `/MyApp` — an absolute path at the
  // filesystem root, which exists nowhere, so every later check answers "not found" and the command
  // reports a missing build rather than a connection that failed. A wrong answer beats no answer only
  // when it is wrong loudly.
  if (!home) {
    fail(`could not read the home directory on ${target.label}.`,
      '  Set "remote": { "dir": "…" } to the checkout\'s path on that machine, or check the connection'
      + ' with `shenora ios doctor --host`.');
    return '';
  }
  return target.join(home, path.basename(cfg.root));
}

/**
 * What to hand `dotnet build`/`publish` — the PROJECT, never the directory above it.
 *
 * ⚠ `cfg.project` may itself name a directory (the SDK accepts one), which is why this joins rather than
 * assuming a `.csproj`. What it must never do is stop at the repo root: `dotnet build <root>` builds the
 * solution found there, which on this kit's own tree means the Windows sample and the test project.
 */
export function buildProject(cfg: DeployConfig, target: Target): string {
  if (!target.isRemote) return path.join(cfg.root, cfg.project);
  const root = remoteRoot(cfg, target);
  return root ? target.join(root, cfg.project) : '';
}

/** The project's own DIRECTORY on the build machine — where its `bin/` lives. */
export function buildDir(cfg: DeployConfig, target: Target): string {
  if (!target.isRemote) return projectDir(cfg);
  const project = buildProject(cfg, target);
  if (!project) return '';
  // A `.csproj` sits IN the directory that holds `bin/`; a directory-shaped `project` already IS it.
  return /\.[a-z]+proj$/i.test(project) ? target.dirname(project) : project;
}

/**
 * Is the artifact newer than the build claiming it? Same purpose as `findPackage` on the Android side:
 * the output directory is never cleaned between runs, so without this a build that produced nothing
 * hands back the previous run's output.
 *
 * 🔴 **It asks the WHOLE bundle, and the previous version's rule was measured false.** That rule was
 * "a `.app`'s own mtime can survive a rebuild, so read its Info.plist, which is rewritten every build".
 * The first half is true; the second is not. On a real Mac, immediately after a successful incremental
 * build, the `.app` was 34 seconds old and its `Info.plist` was **3.9 days** old — so this refused a
 * perfectly good build with "nothing was produced this time", which is a confident statement about a
 * build that had just succeeded on screen. Neither file is a clock; the newest thing anywhere inside is.
 *
 * ⚠ **The one-second allowance is now a THIRTY-second one, and only for a remote target.** Two machines
 * means two clocks: this stamps `builtAfter` here and reads mtimes there. Measured skew against the
 * Mac in question was 2 s, in the forgiving direction — but nothing guarantees the sign, and an NTP
 * correction mid-build is exactly the kind of thing that would make this reject one build in a hundred
 * for no visible reason. It only has to be tight enough to catch "yesterday's leftover".
 */
function builtBy(target: Target, full: string, builtAfter?: number): boolean {
  if (builtAfter === undefined) return true;
  const mtime = target.newestMtimeMs(full);
  const allowance = target.isRemote ? 30_000 : 1_000;
  // A path that cannot be read is NOT fresh — `newestMtimeMs` answers null rather than throwing, and
  // "unknown" must not be mistaken for "just built".
  return mtime !== null && mtime >= builtAfter - allowance;
}

/** The built .app, FOUND rather than composed: the bundle name follows the assembly, not the project. */
function findApp(target: Target, cfg: DeployConfig, rid: string, builtAfter?: number): string | null {
  // `buildDir`, not `path.dirname` — see `projectDir`'s doc: a `project` naming a DIRECTORY resolved to
  // the PARENT, so this looked for the .app one level too high and answered "not built" about a built
  // app. `buildDir` also redirects to the BUILD MACHINE's own tree when `target` is remote — `cfg.root`
  // names a path on the machine running this CLI, which is the wrong one once that isn't the Mac.
  const dir = target.join(buildDir(cfg, target), 'bin', cfg.configuration, iosTfmOf(cfg), rid);
  if (!target.exists(dir)) return null;
  const app = target.list(dir).find((e) => e.endsWith('.app'));
  if (!app) return null;
  const full = target.join(dir, app);
  return builtBy(target, full, builtAfter) ? full : null;
}

/**
 * 🔴 An app EXTENSION is provisioned separately from its container and will not launch without its own
 * entitlements and embedded profile. Checked BEFORE install, because one that cannot launch installs
 * perfectly happily and then does nothing: a Live Activity shows as an empty capsule while every
 * ActivityKit call reports success. **A simulator cannot catch this** — it does not enforce code signing.
 * This kit shipped that bug and spent three device round-trips finding it.
 */
function checkExtensions(target: Target, app: string): { checked: number; problems: string[] } {
  const plugins = target.join(app, 'PlugIns');
  if (!target.exists(plugins)) return { checked: 0, problems: [] };
  const problems: string[] = [];
  let checked = 0;
  for (const entry of target.list(plugins).filter((e) => e.endsWith('.appex'))) {
    checked++;
    const appex = target.join(plugins, entry);
    if (!target.exists(target.join(appex, 'embedded.mobileprovision'))) {
      problems.push(`${entry}: no embedded.mobileprovision — it installs and never runs.`);
    }
    // `codesign` failing to RUN is not a fact about the extension — the install diagnostic below has
    // the rule. Both outcomes still block the install; only the named cause differs.
    const entitlements = target.sh(`codesign -d --entitlements - ${q(appex)}`, { quiet: true });
    if (entitlements.status !== 0) {
      problems.push(`${entry}: codesign could not read it `
        + `(${entitlements.out.trim().split('\n')[0] || 'no detail'}) — cannot verify it is launchable.`);
    } else if (!entitlements.out.includes('application-identifier')) {
      problems.push(`${entry}: no application-identifier entitlement — the system refuses to launch it.`);
    }
  }
  return { checked, problems };
}


function build(target: Target, cfg: DeployConfig, rid: string, signing: string, extra: string): boolean {
  if (extra) console.log(`shenora: extra build args:${extra}`);
  console.log(`shenora: building ${cfg.project} (${iosTfmOf(cfg)}, ${rid})…`);
  const project = buildProject(cfg, target);
  const dir = buildDir(cfg, target);
  if (!project) return false;      // remoteRoot already reported why
  const command = `dotnet build ${q(project)} -c ${q(cfg.configuration)} `
    + `-f ${q(iosTfmOf(cfg))} -p:RuntimeIdentifier=${q(rid)}${signing}${extra} 2>&1 | tail -40`;
  // 🔴 Signing needs the login keychain, and an ssh session is a different audit session — codesign
  // then fails `errSecInternalComponent` (see `SshTarget.gui`'s own doc for how this was proven). A
  // local Mac is already in a GUI session, so only a REMOTE build that actually signs needs the
  // hand-off; the simulator path calls this with an empty `signing` and never triggers it.
  const r = signing && target.isRemote
    ? target.gui(command, { tag: 'device-build' })
    : target.sh(command, { cwd: dir });

  // 🔴 A GUI build's output must be PRINTED here, because `gui` cannot stream: its script runs detached
  // in another session, so the log only exists as a return value. Missing this, a failed device build
  // printed the single line "the build failed — see the output above" with nothing above it — a tool
  // reporting a failure it declines to explain, which is worse than crashing. Hit on the first real
  // signed build; the publish path already did this and this one did not.
  if (target.isRemote && signing && r.out.trim()) console.log(r.out.trimEnd());

  if (r.status === 0) return true;

  // Signing's own most common failure, and the message is opaque about the cause: it means no profile on
  // this Mac matches THIS bundle id, not that none exist. Without an Apple ID signed into Xcode there is
  // nothing that can create one, which is why `doctor` reports that as a MISSING row.
  if (/Could not find any available provisioning profiles/i.test(r.out)) {
    console.error('\nshenora: no provisioning profile on that Mac covers this app.');
    console.error(`  It means no profile matches ${cfg.bundleId} — not that the Mac has none at all.`);
    console.error('  A profile is created by Xcode once an Apple ID is signed in:');
    console.error('    Xcode → Settings → Accounts → + → your Apple ID,');
    console.error('    then open any project once so it can register this device.');
    console.error('  `shenora ios doctor` reports the account and profile count before a build.');
    return false;
  }
  // The Xcode gate is common enough, and its message specific enough, to name the escape hatch here
  // rather than leave an adopter to find it. Detected from the SDK's own wording.
  // Both shapes of the same mismatch: the up-front gate says "requires Xcode", and past it the linker
  // says MT4162 (a binding naming an API this Xcode never shipped).
  if (/requires Xcode/i.test(r.out) || /MT4162/.test(r.out)) {
    console.error('\nshenora: that is the .NET-for-iOS workload and this machine\'s Xcode disagreeing.');
    const sdk = xcodeSdkVersion(target);
    const band = sdk ? pickBindingBand(iosBindingBands(target, iosTfmOf(cfg)), sdk) : null;
    if (band) {
      // 🔴 A DEVICE build IS possible — measured, not reasoned. The SDK picks the NEWEST bindings, and
      // older ones are fine because every API they name still exists; pinning the band is the fix, and
      // it is named here rather than left as "use the simulator".
      console.error(`  This Mac's Xcode SDK is ${sdk}, and it CAN build against bindings ${band}.`);
      // 🔴 IN THE PROJECT, not on the command line, and the difference is not style. `-p:` sets a GLOBAL
      // MSBuild property, which propagates into every project in the graph — including the plain
      // `net10.0` ones, which have no target platform at all. They then fail with
      // `MSB4184 … "targetPlatformIdentifier" cannot have zero length`, an error naming neither iOS nor
      // the version that caused it. Measured against a real Mac, after this command had confidently
      // recommended exactly that.
      console.error(`  Set it in your iOS head's .csproj, where it applies to that project ALONE:`);
      console.error(`    <TargetPlatformVersion>${band}</TargetPlatformVersion>`);
      console.error('  then build with:');
      console.error('    shenora ios deploy -- -p:ValidateXcodeVersion=false');
      console.error('  ⚠ NOT `-p:TargetPlatformVersion` on the command line — a global property reaches the');
      console.error('    non-platform projects too, and they cannot take one.');
      console.error('  ⚠ A dev-loop unblock, and a VISIBLE one on purpose: you are building against older');
      console.error('    bindings, so an API newer than them is missing at runtime rather than at compile');
      console.error('    time. Match the pair for anything you ship.');
    } else {
      console.error('  Match the pair (install the Xcode it names, or a workload built for the one you have),');
      console.error('  or override per-machine:');
      console.error('    shenora ios deploy --simulator -- -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly');
      console.error('  ⚠ Both flags are needed — the first clears the up-front gate, the second clears MT0180');
      console.error('    from the linker step. It is a dev-loop unblock, NOT a shipping configuration.');
    }
    console.error('  `shenora ios doctor` predicts this before a build.');
  }
  return fail('the build failed — see the output above.');
}

/**
 * `shenora ios build` — a DISTRIBUTABLE, which is the one thing `cap build` does that this CLI did not.
 *
 * <p>
 * 🔴 **It is `dotnet publish`, not `dotnet build`, and the difference is the whole command.** `deploy`
 * builds a debug app and pushes it at a device; this produces the artifact you hand to someone else — a
 * Release build, trimmed and AOT-compiled by the iOS SDK's own defaults, with `ArchiveOnBuild` so the
 * SDK packages an `.ipa` rather than leaving a `.app` tree.
 * </p>
 *
 * <p>
 * ⚠ **Release is the DEFAULT here and Debug is not merely slower — it is a different artifact.** A Debug
 * iOS build carries the interpreter and a development provisioning profile; shipping one is the mistake
 * this command exists to make unlikely. `--configuration` overrides it, and the config's own
 * `configuration` (which defaults to Debug for the dev loop) is deliberately IGNORED.
 * </p>
 *
 * <p>
 * ⚠ **There is no simulator distributable.** `--simulator` is refused by name — measured, not assumed:
 * the SDK answers *"A runtime identifier for a device architecture must be specified in order to publish
 * this project"*, and only after a full restore.
 * </p>
 */
export function cmdBuild(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!requireFields(cfg, ['project'])) return;
  // Refuse a non-iOS TFM HERE rather than letting the SDK say `NETSDK1147: install the android
  // workload` twenty lines into a build — see `platformTfm`.
  if (!platformTfm(cfg, 'ios')) return;

  const { own, passthrough } = splitArgs(args);
  const extra = shellPassthrough(passthrough);
  const configuration = argValue(own, '--configuration') ?? 'Release';

  // 🔴 REFUSED UP FRONT, and this is a MEASURED behaviour rather than a guard on principle: the iOS SDK
  // will not publish a simulator RID at all — *"A runtime identifier for a device architecture must be
  // specified in order to publish this project"*. Passing it through means a full restore and forty
  // lines of MSBuild before that sentence appears. There is no simulator distributable; a simulator
  // build is `shenora ios deploy --simulator`.
  if (own.includes('--simulator')) {
    fail('there is no simulator distributable — the iOS SDK refuses to publish a simulator architecture.',
      '  For the dev loop use `shenora ios deploy --simulator`; `build` produces a DEVICE artifact.');
    return;
  }

  const rid = 'ios-arm64';
  console.log(`shenora: publishing ${cfg.project} (${iosTfmOf(cfg)}, ${rid}, ${configuration})…`);
  // Stamped BEFORE the publish, exactly as the Android side does: the output directory is never
  // cleaned between runs, so an artifact older than this belongs to a previous one.
  const startedAt = Date.now();
  const projDir = buildDir(cfg, target);
  const project = buildProject(cfg, target);
  if (!project) return;            // remoteRoot already reported why
  const publish = `cd ${q(projDir)} && dotnet publish ${q(project)} -c ${q(configuration)} `
    + `-f ${q(iosTfmOf(cfg))} -p:RuntimeIdentifier=${q(rid)} -p:ArchiveOnBuild=true${extra} 2>&1 | tail -40`;

  // 🔴 A DEVICE artifact, so this SIGNS — which means it needs the login keychain, which an ssh session
  // cannot reach (a different audit session; `codesign` answers `errSecInternalComponent`). Same wall as
  // `deploy --device`, and it was missed here at first precisely because this command reads as "just a
  // build": `ios-arm64` is the only RID it accepts, so there is no unsigned path through it at all.
  const r = target.isRemote
    ? target.gui(publish, { tag: 'publish', timeoutMs: 30 * 60_000 })
    : target.sh(publish, { cwd: projDir });
  if (target.isRemote && r.out.trim()) console.log(r.out.trimEnd());
  if (r.status !== 0) {
    fail('the publish failed — see the output above.',
      '  A Release build runs the full linker, so it can fail where `deploy` succeeds.');
    return;
  }

  // 🔴 REPORT THE ARTIFACT, and refuse to claim success without finding one. `dotnet publish` exits 0
  // having produced nothing more than once in this repo's history (a skipped target with a satisfied
  // incremental check), and "publish succeeded" with no file is the least actionable message possible.
  const outDir = target.join(projDir, 'bin', configuration, iosTfmOf(cfg), rid, 'publish');
  const artifact = findArtifact(target, outDir, startedAt) ?? findArtifact(target, target.dirname(outDir), startedAt);
  if (!artifact) {
    // Say STALE when a leftover is what was found — "no artifact appeared" beside a directory visibly
    // holding one is the most confusing message this tool could print (the Android fix, ported).
    const stale = findArtifact(target, outDir) ?? findArtifact(target, target.dirname(outDir));
    fail(stale
      ? `the publish reported success but the only artifact under ${outDir} predates this build `
        + `(${stale}) — it is left over from an earlier run, so nothing was produced this time.`
      : `the publish reported success but no .ipa or .app appeared under ${outDir}.`,
      '  That usually means a target was skipped — try again after `rm -rf bin obj`.');
    return;
  }

  const size = sizeOf(target, artifact);
  console.log(`\nshenora: ${artifact}`);
  console.log(`         ${size}`);
  if (artifact.endsWith('.app')) {
    console.log('\n  ⚠ This is a .app tree, not an .ipa. For a distributable device artifact, build '
      + 'without --simulator and make sure signing is configured (`shenora ios doctor`).');
  }
}

/**
 * The publish output, **`.ipa` first**: an archive is the distributable, and a `.app` beside it is what
 * the SDK leaves when signing could not produce one. Returning the `.app` in that case is deliberate —
 * it lets `cmdBuild` say "this is not distributable, here is why" instead of "nothing was produced",
 * which are different problems with different fixes.
 *
 * @param builtAfter Epoch ms; an artifact older than this is STALE and is not returned — the guard
 *   `findPackage` carries on the Android side, ported after the identical incident class was confirmed
 *   here (the file's own history: a publish "exits 0 having produced nothing"). A stale `.ipa` beside a
 *   fresh `.app` yields the `.app` — this run's real output beats the previous run's archive.
 */
export function findArtifact(target: Target, dir: string, builtAfter?: number): string | null {
  if (!target.exists(dir)) return null;
  const entries = target.list(dir);
  const fresh = (name: string | undefined): string | null => {
    if (!name) return null;
    const full = target.join(dir, name);
    return builtBy(target, full, builtAfter) ? full : null;
  };
  return fresh(entries.find((e) => e.endsWith('.ipa')))
    ?? fresh(entries.find((e) => e.endsWith('.app')));
}

/** Human-readable size — `du -sh` handles a `.app` DIRECTORY, which `stat` would report as ~loose bytes. */
function sizeOf(target: Target, artifact: string): string {
  const out = target.probe(`du -sh ${q(artifact)}`).split(/\s+/)[0];
  return out ? `${out} on disk` : 'size unknown';
}

/**
 * Did `simctl boot` fail only because the device was ALREADY booted?
 *
 * 🔴 That case is a success for our purposes and is the entire reason the old code wrote `|| true` — but
 * `|| true` cannot tell it apart from a name that does not exist, so a typo became a silent install onto
 * some other running simulator. Matching the state message keeps the idempotent case working while
 * letting a real failure through.
 *
 * ⚠ Matched loosely (case-insensitive, on the distinctive phrase) because this is Apple's wording and
 * not a contract: a future rewording must fail LOUDLY — an unrecognised message is treated as a genuine
 * failure, which costs a redundant error on a booted device and never costs a wrong install.
 */
export function isAlreadyBooted(output: string): boolean {
  return /current state:\s*Booted/i.test(output);
}

export function cmdDeploy(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!requireFields(cfg, ['project', 'bundleId'])) return;
  if (!platformTfm(cfg, 'ios')) return;
  const { own, passthrough } = splitArgs(args);
  const extra = shellPassthrough(passthrough);
  if (own.includes('--simulator')) deployToSimulator(target, cfg, own, extra);
  else deployToDevice(target, cfg, own, extra);
}

/**
 * The simulator half — no signing, no provisioning, no 7-day profile. This is the loop most work should
 * use; hardware is for what only hardware can answer (background playback, real codecs, thermals).
 */
function deployToSimulator(target: Target, cfg: DeployConfig, args: string[], extra: string): void {
  const rid = simulatorRid();
  const startedAt = Date.now();
  if (!build(target, cfg, rid, '', extra)) return;

  const app = findApp(target, cfg, rid, startedAt);
  if (!app) {
    const stale = findApp(target, cfg, rid);
    fail(stale
      ? `the build reported success but ${target.basename(stale)} predates it — nothing was produced `
        + 'this time, and installing the leftover would run yesterday\'s code as if it were today\'s.'
      : `the build succeeded but no .app appeared under bin/${cfg.configuration}/${iosTfmOf(cfg)}/${rid}.`);
    return;
  }
  console.log(`shenora: ${target.basename(app)}`);

  // ⚠ `open -a Simulator` is what actually SHOWS the window. A booted device with no UI installs and
  // launches perfectly happily, which looks exactly like nothing happened.
  const name = argValue(args, '--simulator');
  if (name) {
    // 🔴 THE BOOT'S FAILURE USED TO BE SWALLOWED by `|| true`, which was there for a real reason —
    // booting an ALREADY-booted simulator exits non-zero — but it swallowed a MISTYPED NAME with it. The
    // run then carried on to `install booted` and landed on whatever else happened to be running: you
    // debug the wrong build, on a device you did not choose. This CLI refuses to guess in exactly this
    // situation twice already (`resolveDevice` here, `resolveDevice` on the Android side); the simulator
    // path was the one place it guessed.
    const boot = target.sh(`xcrun simctl boot ${q(name)}`, { quiet: true });
    if (boot.status !== 0 && !isAlreadyBooted(boot.out)) {
      fail(`could not boot the simulator ${JSON.stringify(name)}.`,
        `  \`shenora ios simulators\` lists the names this Mac knows.\n\n${boot.out.trim()}`);
      return;
    }
  }
  target.sh('open -a Simulator || true', { quiet: true });

  // 🔴 ADDRESS THE NAMED DEVICE, not `booted`. Even with the boot check above, `booted` is the wrong
  // target whenever a name was given: two simulators can be running, and `booted` then means "whichever
  // simctl picks". Naming one is the only way to be sure the thing you installed is the thing you are
  // looking at.
  const simTarget = name ?? 'booted';
  if (target.sh(`xcrun simctl install ${q(simTarget)} ${q(app)} 2>&1 | tail -10`).status !== 0) {
    fail('install failed.',
      '  If it says no booted device, pass --simulator "iPhone 16 Pro" (`shenora ios simulators`).');
    return;
  }
  const launched = target.sh(`xcrun simctl launch ${q(simTarget)} ${q(cfg.bundleId)} 2>&1 | tail -10`);
  if (launched.status !== 0) {
    fail('launch failed.');
    return;
  }
  if (!stillRunning(target, launched.out, cfg)) return;
  console.log('\nshenora: running in the simulator. Screenshot it with `shenora ios shot`.');
}

/**
 * Did the app SURVIVE its launch? Reported honestly, with the crash if not.
 *
 * 🔴 **`simctl launch` prints a pid and exits 0 for an app that dies immediately**, so "launched" is not
 * evidence of "running" — and this command said *"running in the simulator"* about a build that crashed
 * on startup every single time. Caught by screenshotting the result: the simulator was sitting on its
 * home screen while the CLI reported success. That is precisely the false-success class this tool's own
 * README claims to have closed, arrived at from a direction none of the existing checks watched.
 *
 * ⚠ The pid is a HOST pid — `simctl` runs simulator processes on the Mac itself — so `ps` can answer.
 */
function stillRunning(target: Target, launchOutput: string, cfg: DeployConfig): boolean {
  const pid = /:\s*(\d+)\s*$/m.exec(launchOutput.trim())?.[1];
  if (!pid) return true;      // Nothing to check against; do not invent a failure.

  // A crash-on-startup is over in well under a second; this is long enough to catch it and short enough
  // not to be felt.
  const alive = target.sh(`sleep 3; ps -p ${q(pid)} > /dev/null 2>&1 && echo alive || echo gone`,
    { quiet: true, timeoutMs: 60_000 });
  if (!/gone/.test(alive.out)) return true;

  fail('the app launched and then exited immediately.',
    '  A launch reports a pid whether or not the process survives, so this is checked rather than assumed.');
  const crash = target.probe(
    `xcrun simctl spawn booted log show --last 2m --predicate ${q(simulatorLogPredicate(cfg.bundleId))}`
    + ` 2>/dev/null | tail -25`);

  // 🔴 A metadata-token failure is a MIXED BUILD, and it does not look like one. Hit for real: after
  // pinning TargetPlatformVersion the app died on `Token … is not valid in the scope of module
  // Microsoft.iOS.dll`, which reads as "these bindings are missing an API" — the very thing the pin's own
  // warning primes you to expect. It was neither: `obj/` still held metadata from the previous band, and
  // deleting it fixed it outright. Named here because the plausible reading is the wrong one.
  if (/is not valid in the scope of module|ResolveFullTokenReference/i.test(crash)) {
    console.error('\n  That is a MIXED BUILD, not a missing API — the two look identical from here.');
    console.error('  Something in obj/ was compiled against a different iOS binding version. If you have');
    console.error('  changed TargetPlatformVersion or the workload, the incremental build kept the old');
    console.error('  metadata. Delete the intermediates and build again:');
    console.error(`    rm -rf ${q(target.join(buildDir(cfg, target), 'obj'))}`
      + ` ${q(target.join(buildDir(cfg, target), 'bin'))}`);
  }

  if (crash) {
    console.error('\n  Its last output:\n');
    console.error(crash.split('\n').map((l) => `    ${l}`).join('\n'));
  } else {
    console.error(`  Nothing in its log. \`shenora ios log\` may have more.`);
  }
  return false;
}

function deployToDevice(target: Target, cfg: DeployConfig, args: string[], extra: string): void {
  const device = resolveDevice(target, argValue(args, '--device'));
  if (!device) return;

  // CodesignProvision=Automatic + an Apple Development key is what lets an adopter reach a phone with NO
  // Xcode project of their own — the whole point of this command.
  const signing = ` -p:CodesignProvision=Automatic -p:CodesignKey=${q('Apple Development')}`;
  const startedAt = Date.now();
  if (!build(target, cfg, 'ios-arm64', signing, extra)) return;

  const app = findApp(target, cfg, 'ios-arm64', startedAt);
  if (!app) {
    const stale = findApp(target, cfg, 'ios-arm64');
    fail(stale
      ? `the build reported success but ${target.basename(stale)} predates it — nothing was produced `
        + 'this time, and installing the leftover would run yesterday\'s code as if it were today\'s.'
      : `the build succeeded but no .app appeared under bin/${cfg.configuration}/${iosTfmOf(cfg)}/ios-arm64.`);
    return;
  }
  console.log(`shenora: ${target.basename(app)}`);

  const ext = checkExtensions(target, app);
  if (ext.problems.length > 0) {
    console.error('\nshenora: an app extension is not device-launchable:');
    for (const p of ext.problems) console.error(`  ${p}`);
    fail('refusing to install — it would run with the extension silently dead.',
      '  A simulator cannot catch this: it does not enforce code signing.');
    return;
  }
  if (ext.checked > 0) console.log(`shenora: app extensions ok (${ext.checked})`);

  console.log('\nshenora: installing…');
  const install = target.sh(`xcrun devicectl device install app --device ${q(device.id)} ${q(app)} 2>&1 | tail -20`);
  if (install.status !== 0) {
    // 🔴 NAME THE CAUSE THE OUTPUT ACTUALLY SHOWS. This printed the code-signing hint for every failure,
    // including a Wi-Fi drop mid-transfer ("the peer is no longer reachable") — sending the reader to
    // Settings > Device Management to fix a network problem. A diagnostic that confidently names the
    // wrong cause is worse than none, and this repo has a rule about it.
    if (/no longer reachable|unable to locate a device|ControlChannelConnectionError/i.test(install.out)) {
      fail('the device dropped off mid-install — this is a TRANSPORT failure, not a signing one.',
        '  Wi-Fi pairing is unreliable for transfers this size. Plug the phone in over USB and retry;\n'
        + '  `shenora ios devices` shows how it is currently attached.');
      return;
    }
    fail('install failed.',
      '  If it says the app could not be verified, the certificate is not TRUSTED on the phone yet:\n'
      + '  Settings > General > VPN & Device Management > your developer account > Trust.');
    return;
  }

  console.log('\nshenora: launching…');
  const launch = target.sh(`xcrun devicectl device process launch --device ${q(device.id)} ${q(cfg.bundleId)} 2>&1 | tail -20`);
  if (launch.status !== 0) {
    // 🔴 A LOCKED PHONE IS THE COMMONEST LAUNCH FAILURE AND THE LEAST OBVIOUS FROM THE OUTPUT — the real
    // reason is four nested error frames down, under `FBSOpenApplicationServiceErrorDomain`, while the
    // top line just says the request failed. Everything up to this point (build, sign, install) has
    // already SUCCEEDED, so a bare "launch failed" reads as a code problem.
    if (/could not be, unlocked|BSErrorCodeDescription = Locked/i.test(launch.out)) {
      fail('the phone is locked, so iOS refused to launch the app.',
        '  Unlock it and run this again — everything else (build, sign, install) already succeeded.');
      return;
    }
    fail('launch failed.');
    return;
  }
  console.log(`\nshenora: running on ${device.name}. Read its output with \`shenora ios log\`.`);
}

/**
 * The app's own output.
 *
 * 🔴 **A SIMULATOR, A DEVICE AND THIS MAC ARE THREE DIFFERENT LOGS, and reading the wrong one is silent.**
 * This originally ran a bare `log show`, which reads THE MAC'S OWN unified log — so it answered with a
 * header and nothing under it whichever target you had just deployed to. That reads as "my app logged
 * nothing", the one conclusion guaranteed to send you looking in the wrong place. Caught 2026-08-09 by
 * running it against a real iPhone that was demonstrably logging.
 *
 * ⚠ FILTER BEFORE TAILING. A process-wide predicate is ~99% platform chatter, so `tail -n` over the raw
 * stream shows a screen of noise with none of the app's own lines — which looks exactly like a broken log
 * sink. This kit rebuilt that same mistake once per harness before writing it down.
 */
/**
 * The NSPredicate that finds one app's lines in a booted simulator's unified log.
 *
 * 🔴 **`CONTAINS[c]`, and the `[c]` is the whole command working.** NSPredicate's `CONTAINS` is
 * case-SENSITIVE. The search term comes off a bundle id — lower case by convention — while
 * `processImagePath` carries the ASSEMBLY name, so `com.example.myapp` searched a path spelled
 * `MyApp.app/MyApp` and matched nothing at all. Measured on the simulator 2026-08-09: **1 line of output
 * (the header alone) against 20,352 with `[c]`.**
 *
 * The symptom is the one this command exists to prevent: a header printed with nothing under it reads as
 * *"my app logged nothing"*, not as *"your log reader is broken"* — the same shape as the device-side
 * `| head` trap the README describes.
 *
 * Extracted so it is testable off macOS, like {@link withPipefail}: a guarantee only asserted on the
 * machine that rarely runs the suite is one nobody is watching.
 */
export function simulatorLogPredicate(bundleId: string): string {
  const leaf = bundleId.split('.').pop() || bundleId;
  return `processImagePath CONTAINS[c] "${leaf}"`;
}

/** What a simulator-log read amounted to — see {@link describeLogOutcome}. */
export type SimulatorLogOutcome =
  | { kind: 'failed'; message: string; hint: string }
  | { kind: 'empty'; message: string }
  | { kind: 'ok'; text: string };

/**
 * Turn the log read's exit status and output into the three things that can have happened.
 *
 * 🔴 **The status used to be DISCARDED entirely**, one line below a device branch that carefully
 * distinguishes SIGPIPE from a real failure. So a run with no booted simulator printed the "last N
 * lines from …" header and then nothing, and exited 0 — which reads as *"my app logged nothing"* rather
 * than *"your log reader could not run"*. That is verbatim the confusion
 * {@link simulatorLogPredicate}'s own doc says this command exists to avoid, reproduced for a different
 * cause a few lines away from where it is described.
 *
 * ⚠ **EMPTY is not FAILURE, and the two need different words.** A booted simulator whose app has not
 * run in the window legitimately matches nothing; saying "could not read the log" there would send
 * someone hunting a broken tool. Extracted so all three are testable off macOS, like
 * {@link withPipefail} and {@link simulatorLogPredicate}.
 */
export function describeLogOutcome(status: number, out: string, minutes = 10): SimulatorLogOutcome {
  if (status !== 0) {
    return {
      kind: 'failed',
      message: 'could not read the simulator log.',
      hint: '  Is a simulator booted? `shenora ios deploy --simulator` boots one and installs.',
    };
  }
  const text = out.trimEnd();
  return text.trim().length === 0
    ? { kind: 'empty', message: `  (no matching lines in the last ${minutes}m — the app may not have run since)` }
    : { kind: 'ok', text };
}

export function cmdLog(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!requireFields(cfg, ['bundleId'])) return;
  const lines = argValue(args, '-n') ?? '80';

  if (args.includes('--device')) {
    const device = resolveDevice(target, argValue(args, '--device'));
    if (!device) return;
    // RELAUNCHES, deliberately. A reader that attached to an already-running app misses every line
    // written during startup, which for this kit is where the probes report.
    console.log(`shenora: relaunching ${cfg.bundleId} on ${device.name} with a console attached…\n`);
    // 🔴 STREAMED, not captured: `--console`'s whole point is watching startup happen live, and a
    // captured run prints nothing until the process exits — which for a console attach is never. A
    // streamed run returns no output to parse, so the status check below is all there is.
    const r = target.sh(`xcrun devicectl device process launch --console --terminate-existing `
      + `--device ${q(device.id)} ${q(cfg.bundleId)} 2>&1 | head -${q(lines)}`, { stream: true });
    // ⚠ `head` closing the pipe is what ENDS the stream, so SIGPIPE (141) is the success path, not a
    // failure. Treating it as an error here would print a scary message after a run that worked.
    if (r.status !== 0 && r.status !== 141) {
      fail('could not attach a console to the device.',
        '  Check the app is installed (`shenora ios deploy`), and that the phone is unlocked.');
    }
    return;
  }

  console.log(`shenora: last ${lines} lines from ${cfg.bundleId} (booted simulator)\n`);
  // `simctl spawn booted` runs the query INSIDE the simulator. Without it this is the host's log.
  // QUIET, so the three outcomes below decide what the user sees: `log show`'s own stderr is noisy on
  // a perfectly good run, which is why it was being discarded — but discarding the STATUS with it is
  // what made a missing simulator indistinguishable from a quiet app.
  const r = target.sh(`xcrun simctl spawn booted log show --last 10m --style compact `
    + `--predicate ${q(simulatorLogPredicate(cfg.bundleId))} 2>/dev/null | tail -${q(lines)}`,
    { quiet: true });

  const outcome = describeLogOutcome(r.status, r.out);
  if (outcome.kind === 'failed') fail(outcome.message, outcome.hint);
  else console.log(outcome.kind === 'empty' ? outcome.message : outcome.text);
}

/**
 * `shenora ios push` — put this working tree on the Mac.
 *
 * 🔴 **Without this every other remote command is a claim about code that may not be there.** The Mac
 * built whatever its checkout happened to hold, and nothing said so: a build succeeds, an app installs,
 * and it is last week's. The one failure mode a device loop must not have is being confidently wrong
 * about WHICH code ran.
 */
export function cmdPush(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!target.isRemote) {
    fail('there is nothing to push — the build machine is this one.',
      '  `push` exists to send this tree to a Mac reached with --host.');
    return;
  }
  const dir = remoteRoot(cfg, target);
  if (!dir) return;                       // remoteRoot already reported why
  pushTree(target, cfg.root, dir);
  target.close();
}

export function cmdShot(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  const out = argValue(args, '-o') ?? 'shenora-sim.png';
  // 🔴 The simulator is on the TARGET, so the PNG lands there. Written straight to `out` on a remote Mac
  // it would sit in that Mac's home directory while this command cheerfully printed a local-looking
  // filename — a screenshot you cannot look at, reported as a success. Stage it there, then pull it here.
  const staged = target.isRemote ? `/tmp/shenora-shot-${Date.now()}.png` : out;

  if (target.sh(`xcrun simctl io booted screenshot ${q(staged)}`).status !== 0) {
    fail('no booted simulator to screenshot.', '  Run `shenora ios deploy --simulator` first.');
    return;
  }

  if (target.isRemote) {
    const pulled = target.pull(staged, out);
    target.sh(`rm -f ${q(staged)}`, { quiet: true });
    if (!pulled) {
      fail(`the screenshot was taken on ${target.label} but could not be copied back.`,
        `  It is still there at ${staged}.`);
      return;
    }
  }
  console.log(`shenora: ${out}`);
}
