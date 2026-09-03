// The iOS loop: doctor · devices · simulators · deploy (simulator or hardware) · log · shot.
//
// Runs on this Mac, or on one reached over ssh through the `Target` seam (`./remote/`) — see
// `docs/design/cli-remote.md`. ⚠ Signing is the exception: it needs the login keychain, which an ssh
// session cannot reach (a different audit session), so a DEVICE build on a remote target hands off through
// `target.gui` instead of `target.sh`. A local Mac is already in a GUI session.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { q, fail, argValue, splitArgs, shellPassthrough } from './exec.js';
import { iosTfmOf, platformTfm, projectDir, requireFields, type DeployConfig } from './config.js';
import { resolveTarget, resolveHost, diagnoseHost, reportDiagnosis } from './remote/host.js';
import type { Target } from './remote/target.js';
import { pushTree } from './remote/push.js';
import { provisionBundleIds, teamId } from './remote/provision.js';

interface Device {
  id: string;
  name: string;
  state: string;
  os: string;
}

/**
 * Either the devices, or WHY we could not find out.
 *
 * 🔴 A failure that collapses to an empty array — devicectl missing, erroring, unparseable — makes the
 * callers say "no iPhone is connected. Plug it in, unlock it, tap Trust": a confident statement about the
 * user's HARDWARE made when the truth is that the tool failed.
 */
export type DeviceLookup =
  | { ok: true; devices: Device[] }
  | { ok: false; detail: string };

/**
 * Parse `devicectl list devices --json-output`. An unparseable answer must be distinguishable from an
 * empty one: a run with no phones attached still writes a valid document with an empty array, so nothing
 * parseable is a FAILURE.
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
 * 🔴 **The JSON goes to a TEMP FILE, never `/dev/stdout`.** Over a tty stdout is a terminal and the write
 * lands, so every manual check agrees; under `spawnSync` it is an anonymous pipe, `devicectl` cannot open
 * it and yields nothing — indistinguishable from "no devices attached", the single worst answer this tool
 * can give, because it looks like a fact about your phone rather than a bug in the reader.
 */
function devices(target: Target): DeviceLookup {
  const out = `/tmp/shenora-devicectl-${process.pid}.json`;
  try {
    // ⚠ stderr is CAPTURED, not redirected to /dev/null: it is the only place devicectl says why it
    // refused. `quiet` keeps it off a healthy run.
    const r = target.sh(`xcrun devicectl list devices --json-output ${q(out)}`, { quiet: true });
    if (r.status !== 0) {
      return { ok: false, detail: r.out.trim() || `xcrun devicectl exited ${r.status}` };
    }
    const json = target.probe(`cat ${q(out)} 2>/dev/null`);
    return parseDeviceList(json);
  } catch (error) {
    return { ok: false, detail: `could not run devicectl — ${(error as Error).message}` };
  } finally {
    target.sh(`rm -f ${q(out)}`, { quiet: true });
  }
}

/**
 * How a phone is reachable, in the terms that decide whether you can deploy to it.
 *
 * 🔴 **`pairingState` is the field that answers "can I deploy to this"; `tunnelState` is NOT.** The tunnel
 * is a debug channel brought up ON DEMAND, so an idle device that is paired, powered and on the same LAN
 * reports `disconnected` — and the honest reading of that word is "go find a cable".
 *
 * `transportType` is named alongside it because `localNetwork` is genuinely supported and also the
 * transport that drops mid-operation (`peer is no longer reachable`), so a developer can attribute that
 * failure to the transport instead of to their build.
 */
export function describeConnection(connection: Record<string, any> | undefined): string {
  const pairing = String(connection?.pairingState ?? 'unknown');
  const transport = String(connection?.transportType ?? '');
  return transport ? `${pairing} via ${transport}` : pairing;
}

/**
 * Pick the device to act on. REFUSES to guess when several are connected — silently taking the first one
 * deploys to the wrong phone and you then debug the wrong build.
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
  // empty list are both '' — and "no simulators installed" beside a broken xcode-select sends the reader
  // to install components they already have.
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

  // 🔴 THE TRANSPORT IS DIAGNOSED FIRST. Every probe below asks the Mac a question and reads silence as
  // "not installed" — so against a Mac that is merely ASLEEP this command prints `MISSING Xcode`,
  // `MISSING .NET SDK`, `MISSING ios workload`: a confident and completely wrong report about a machine
  // that is fine.
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
  // 🔴 COUNTED, because the worst outcome this command has is silence plus exit 0 — it reads as "nothing
  // to report, everything is fine" and the operator moves on. Seen for real: `npx shenora ios doctor`
  // printed nothing and succeeded while the same code through the binary printed the full report. A
  // doctor that reported NOTHING has not examined anything, and must not say so with a zero exit.
  let written = 0;
  const line = (label: string, value: string, good = true): void => {
    console.log(`  ${good ? 'ok     ' : 'MISSING'} ${label.padEnd(20)} ${value}`);
    written++;
    if (!good) ok = false;
  };

  // WHICH binary is answering — `npx` may resolve a different copy than the one just installed.
  line('shenora cli', `${cliVersion()}  ${cliEntry()}`);

  const xcode = target.probe('xcodebuild -version | head -1');
  line('Xcode', xcode || '(not found — install it from the App Store)', Boolean(xcode));

  const dotnet = target.probe('dotnet --version');
  line('.NET SDK', dotnet || '(not found)', Boolean(dotnet));

  const workload = target.probe('dotnet workload list 2>/dev/null | grep -i ios | head -1');
  line('ios workload', workload || '(run `dotnet workload install maui-ios`)', Boolean(workload));

  // Counted HERE, not with `grep -c`: grep exits 1 for zero matches, so a locked keychain (`security`
  // failing outright) and a genuinely empty identity list are the same '' — and "none, go to Xcode
  // Settings" about a keychain problem sends the reader to a screen that is already correct.
  const identity = target.sh('security find-identity -v -p codesigning', { quiet: true });
  const identityCount = identity.status === 0 ? (identity.out.match(/Apple Development/g)?.length ?? 0) : 0;
  line('signing identity',
    identity.status !== 0
      ? '(could not ask — `security` failed; a locked login keychain does this. Unlock it and retry)'
      : identityCount > 0 ? `${identityCount} found` : '(none — Xcode > Settings > Accounts)',
    identityCount > 0);

  // 🔴 A CERTIFICATE IS NOT AN ACCOUNT, AND NEITHER IS A PROFILE — all three decide whether a DEVICE
  // build can sign. Measured on a Mac reporting `1 found` → `ready` that could not sign at all: valid
  // certificate, free personal team, no Xcode Apple ID, no profiles. The build dies on `No Accounts: Add
  // a new account in Accounts settings` — after a full compile.
  const signing = describeDeviceSigning({
    identities: identity.status === 0 ? identityCount : null,
    accounts: xcodeAccountCount(target),
    profiles: provisioningProfileCount(target),
  });
  line('device signing', signing.text, signing.good);

  // 🔴 PREDICTED, not discovered at minute twenty. Every row above can say `ok` on a Mac that cannot
  // build at all: the workload and Xcode are each fine and only their PAIRING fails, at build time.
  // `dotnet workload update` merely changes WHICH Xcode is demanded; pin a band the Xcode satisfies.
  const sdk = xcodeSdkVersion(target);
  const bands = cfg ? iosBindingBands(target, iosTfmOf(cfg)) : [];
  if (sdk && bands.length > 0) {
    // ⚠ The PROJECT's pin, not just the machine's bands — a correctly pinned csproj is the FIXED state,
    // and a row that cannot say so leaves an adopter who complied still reading `not ready`.
    const bindings = describeBindings({
      bands, sdk, pinned: cfg ? msbuildProperty(target, cfg, 'TargetPlatformVersion') : null,
    });
    line('ios bindings', bindings.text, bindings.good);
    for (const advice of bindings.advice) console.log(`          ${advice}`);
  }

  // 🔴 THE PACKS DECIDE WHETHER A BUILD CAN RUN AT ALL, and until this row nothing looked at them: a Mac
  // reported `ready` and died in `AOTCompile` on a `mono-aot-cross` that was installed at a different
  // version. Same class as the bindings row above — a PAIRING that only fails at build time.
  if (cfg) {
    const expected = msbuildProperty(target, cfg, 'BundledNETCoreAppPackageVersion');
    const pack = describeAotCrossPack({ expected, packs: aotCrossPacks(target, expected) });
    line('aot cross pack', pack.text, pack.good);
  }

  // ⚠ A devicectl failure is REPORTED but not `good: false` — doctor answers "can this machine build and
  // deploy", and a device is optional for that: the simulator path works without one.
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

  // A report of nothing is never a pass. On STDERR: if stdout is what went missing, saying so on stdout
  // would vanish with it.
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
 * 🔴 The SDK picks the NEWEST installed bindings, so on a Mac whose Xcode is older than them every build
 * dies on a wall of `MT4162 … not available in iOS 26.2 (introduced in 26.4)` — and `MtouchLink=SdkOnly`
 * cannot help, because `ManagedRegistrar` walks every binding regardless. The asymmetry is the fix:
 *
 *     bindings NEWER than the SDK → impossible (they name APIs that do not exist)
 *     bindings OLDER than the SDK → fine (everything they name still exists)
 *
 * Verified on Xcode 26.3 with bands 26.0/26.6/27.0 installed: pinning `TargetPlatformVersion` to 26.0
 * builds, links, signs and installs on hardware — **but only with `-p:ValidateXcodeVersion=false`**, and
 * that is a SECOND constraint this function does not model. No installed pack was cut for Xcode 26.3, so
 * the pack's EXACT-Xcode assertion is unsatisfiable whatever band is picked (see {@link describeBindings}).
 *
 * ⚠ The choice must be VISIBLE — silently building against old bindings hides a missing API until runtime.
 */
export function pickBindingBand(bands: string[], sdkVersion: string): string | null {
  const usable = bands.filter((b) => compareVersions(b, sdkVersion) <= 0);
  if (usable.length === 0) return null;
  return usable.sort(compareVersions)[usable.length - 1]!;
}

/**
 * What the `ios bindings` row should say, given the installed bands, the Xcode SDK, and what the PROJECT
 * has actually pinned.
 *
 * 🔴 **The row has to reflect the PROJECT, not only the machine.** It used to compare installed bands to
 * the SDK and never read the csproj, so an adopter who pinned `TargetPlatformVersion` exactly as this row
 * instructed still saw `MISSING` and `shenora: not ready`. A red row that does not reflect success is the
 * mirror image of a green one that does not predict failure, and it is just as useless — worse, because
 * the person seeing it has already done the work.
 *
 * ⚠ **TWO CONSTRAINTS ARE IN PLAY AND ONLY THE BAND IS ONE OF THEM, which is why `good` can be true while
 * `advice` is not empty.** The band answers *do these bindings name APIs this SDK has?* Separately, the
 * .NET-for-iOS PACK asserts an EXACT Xcode, and no choice of band can satisfy it when no installed pack
 * was cut for the Mac's Xcode — the ordinary case on a machine that updates Xcode. Measured on Xcode 26.3
 * with bands 26.0/26.6/27.0 installed, whose packs demanded Xcode 26.0/26.6/27.0: every one was
 * unsatisfiable, so `-p:ValidateXcodeVersion=false` was MANDATORY for every choice. It is a validation
 * POLICY, not a capability limit — that Xcode builds the 26.0 bindings perfectly well, and did.
 */
export function describeBindings(
  { bands, sdk, pinned }: { bands: string[]; sdk: string; pinned: string | null },
): { text: string; good: boolean; advice: string[] } {
  // The caller only asks when bands exist, but this is exported: without the guard the unpinned path
  // compares `undefined` and throws inside a DIAGNOSTIC, which is the one place a crash is least affordable.
  if (bands.length === 0) return { text: '(no binding bands installed for this TFM)', good: false, advice: [] };

  const newest = [...bands].sort(compareVersions).at(-1)!;
  const usable = pickBindingBand(bands, sdk);

  // 🔴 NAMES NO VERSION, deliberately. An earlier draft said "cut for Xcode ${sdk}" and `sdk` is the
  // iPhoneOS SDK version, NOT the Xcode version — measured on a Mac reporting `Xcode 26.3` and
  // `Xcode SDK 26.2`, so the advice sent the reader hunting for a pack cut for an Xcode that does not
  // exist. The pack asserts an Xcode version this tool cannot read, so it describes the ERROR instead of
  // predicting the number.
  const bypass = (band: string): string[] => band === sdk ? [] : [
    '⚠ If the build then fails with "requires Xcode X, the current version is Y", that is the .NET-for-iOS',
    '  PACK asserting an EXACT Xcode — a separate constraint from the band, which no pin can satisfy. It is',
    '  a validation POLICY, not a capability limit (measured: Xcode 26.3 builds the 26.0 bindings fine).',
    '  Clear it with `-p:ValidateXcodeVersion=false`, passed after `--` rather than set in the csproj.',
  ];
  const pinAdvice = (band: string): string[] => [
    `(a build works pinned to ${band}; the newest bindings name APIs this Xcode has never shipped.`,
    ' In the PROJECT, not -p: on the command line — a global property reaches the',
    ' non-platform projects too, and they cannot take one.)',
    ...bypass(band),
  ];

  if (pinned) {
    if (!bands.includes(pinned)) {
      return {
        text: `the csproj pins ${pinned}, which is not installed (have: ${bands.join(', ')})`,
        good: false,
        advice: usable ? pinAdvice(usable) : [],
      };
    }
    if (compareVersions(pinned, sdk) > 0) {
      return {
        text: `the csproj pins ${pinned}, newer than Xcode SDK ${sdk} — no build can succeed`,
        good: false,
        advice: usable ? pinAdvice(usable) : [],
      };
    }
    return { text: `csproj pins ${pinned} ≤ Xcode SDK ${sdk}`, good: true, advice: bypass(pinned) };
  }

  // Unpinned: the SDK takes the NEWEST installed band, so that is the one that has to fit.
  if (compareVersions(newest, sdk) <= 0) {
    return { text: `${newest} ≤ Xcode SDK ${sdk}`, good: true, advice: bypass(newest) };
  }
  if (usable) {
    return {
      text: `newest is ${newest} but Xcode SDK is ${sdk} — pin <TargetPlatformVersion>${usable}</…> in the csproj`,
      good: false,
      advice: pinAdvice(usable),
    };
  }
  return {
    text: `every installed band (${bands.join(', ')}) is newer than Xcode SDK ${sdk} — no build can succeed`,
    good: false,
    advice: [],
  };
}

/**
 * What the `aot cross pack` row should say.
 *
 * 🔴 **`doctor` reported `ready` on a Mac where the build was structurally impossible**, because nothing
 * checked the .NET packs at all. The iOS SDK resolved the AOT cross pack at one version while every pack
 * installed was another, and the build died on `The "AOTCompile" task failed unexpectedly … mono-aot-cross
 * … No such file or directory` — a stack trace naming an MSBuild task where the problem should have been.
 *
 * ⚠ **It bites a Debug SIMULATOR build, which is the loop this CLI tells you to prefer** — the interpreter
 * still shells out to `mono-aot-cross`, so "Debug does not AOT" is not the escape it sounds like. And
 * `dotnet workload restore` reports success and installs nothing, so the obvious repair looks like it
 * worked and changes nothing.
 *
 * ⚠ **An unknown expected version answers `ok`, deliberately.** Saying `MISSING` on a guess would send
 * someone to repair a machine that is fine; `ready` on a guess is the defect this row exists to fix. So
 * "could not ask" says exactly that, and leaves the other rows meaning what they say.
 *
 * **`BundledNETCoreAppPackageVersion` is the property that names the version, confirmed by measurement**
 * (2026-08-21, on the Windows dev box — the SDK mechanics are not Mac-specific): it evaluates to
 * `10.0.10`, which is exactly the version the adopter's `AOTCompile` failure reported the iOS SDK
 * resolving the cross pack at. The pack layout is confirmed the same way —
 * `packs/<pack>/<version>/tools/mono-aot-cross`, beside `llc` and `opt`. ⚠ What is still unmeasured is
 * Mac-only: that an iOS pack is named `…Cross.ios*` the way the android ones are named `…Cross.android*`.
 */
export function describeAotCrossPack(
  { packs, expected }:
  { packs: { pack: string; installed: string[]; compilerPresent: boolean }[]; expected: string | null },
): { text: string; good: boolean } {
  if (packs.length === 0) return { text: '(none installed — `dotnet workload install maui-ios`)', good: false };
  if (!expected) {
    return { text: `${packs.length} pack(s) installed; could not ask MSBuild which version it wants`, good: true };
  }

  // 🔴 EVERY ios cross pack, not the first one found. A Mac carries one per target — device arm64,
  // simulator arm64, simulator x64 — and they version INDEPENDENTLY: measured on an Intel Mac where
  // `Cross.iossimulator-x64` had 10.0.10 and the other two did not, so picking one made the verdict a
  // coin flip on directory order and hid two targets that could not build.
  const broken = packs.filter((p) => !p.compilerPresent);
  if (broken.length === 0) return { text: `${packs.length} pack(s) at ${expected}`, good: true };

  return {
    text: `the SDK resolves ${expected}; ${broken.length} of ${packs.length} pack(s) lack it — `
      + broken.map((p) => `${p.pack.replace(/^Microsoft\.NETCore\.App\.Runtime\.AOT\./, '…')} `
        + `(has ${p.installed.join(', ') || 'nothing'})`).join('; ')
      + ' — a build for those targets dies in the AOTCompile task on a missing `mono-aot-cross`',
    good: false,
  };
}

/**
 * Can this Mac sign for a DEVICE?
 *
 * 🔴 The three facts are not interchangeable and only one of them is what `security find-identity`
 * answers. A CERTIFICATE proves a key exists; an ACCOUNT is what `-allowProvisioningUpdates` needs to
 * MINT or refresh a profile; a PROFILE is what the device demands at install. Certificate-only is also
 * the state a working machine decays into: a free personal team's profile expires after 7 days, and
 * refreshing it needs the account that is missing.
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
        // A profile without an account works until it expires and can never be refreshed — "it built
        // yesterday" is how this is discovered.
        ? `❌ no Xcode Apple ID — ${profiles} profile(s) will work until they expire and cannot be refreshed`
        : '❌ certificate but NO Xcode Apple ID and no profiles — a device build dies at signing, after '
          + 'the full compile. Xcode > Settings > Accounts, add your Apple ID.',
      good: false,
    };
  }
  return {
    text: profiles > 0
      ? `${accounts} account(s), ${profiles} profile(s)`
      // An account with no profile is FINE: -allowProvisioningUpdates mints one, so the zero must not
      // read as the failure above.
      : `${accounts} account(s), no profile yet — one is minted on the first device build`,
    good: true,
  };
}

/** The resolved `packs/` directory per target, memoised — see {@link packsDirectory}. */
const packsDirectories = new Map<string, string>();

/**
 * The .NET SDK's `packs/` directory on the target, or null when it cannot be found.
 *
 * ⚠ Memoised because two doctor rows ask for it and the probe is a full ssh round trip on a remote Mac,
 * for an answer that cannot change inside one run. 🔴 A FAILURE IS NOT CACHED, for the reason
 * {@link remoteRoot} gives: caching one makes a single transient probe permanent for the process.
 */
function packsDirectory(target: Target): string | null {
  const cached = packsDirectories.get(target.label);
  if (cached) return cached;

  const root = target.probe('dirname "$(readlink -f "$(command -v dotnet)")"');
  if (!root) return null;
  const packs = target.join(root, 'packs');
  if (!target.exists(packs)) return null;

  packsDirectories.set(target.label, packs);
  return packs;
}

/**
 * The iOS binding bands installed for this TFM's .NET version, from the SDK's own packs directory
 * (`Microsoft.iOS.Sdk.net10.0_26.0` → `26.0`).
 *
 * ⚠ Filtered by the NET version: a machine carrying `net9.0_18.0` beside `net10.0_26.5` would otherwise
 * offer a band that cannot build this app at all.
 */
function iosBindingBands(target: Target, tfm: string): string[] {
  const net = /^net(\d+\.\d+)/.exec(tfm)?.[1];
  const packs = packsDirectory(target);
  if (!net || !packs) return [];
  return target.list(packs)
    .map((name) => new RegExp(`^Microsoft\\.iOS\\.Sdk\\.net${net.replace('.', '\\.')}_(\\d+\\.\\d+)$`).exec(name)?.[1])
    .filter((band): band is string => Boolean(band));
}

/** The installed Xcode's iPhoneOS SDK version, or '' when it cannot be asked. */
function xcodeSdkVersion(target: Target): string {
  return target.probe('xcrun --sdk iphoneos --show-sdk-version');
}

/**
 * One MSBuild property as the PROJECT evaluates it, or null when it cannot be asked.
 *
 * 🔴 Evaluated, never grepped. A `TargetPlatformVersion` can be conditioned, imported from a
 * `Directory.Build.props`, or supplied by the SDK rather than written in the csproj, so reading the file
 * answers a different question from the one the build asks. ⚠ Null is an ANSWER — an unpushed tree or a
 * project needing restore both land here, and a caller must say "could not ask" rather than assume.
 *
 * ⚠ ONE property per call, deliberately, even though doctor asks for two. `-getProperty:A -getProperty:B`
 * would halve the evaluations, but MSBuild answers a multi-property request in JSON — a parser this repo
 * cannot exercise without a Mac, added to the one command whose whole value is not misreporting. A few
 * seconds against a build that fails twenty minutes in is the right side of that trade.
 */
function msbuildProperty(target: Target, cfg: DeployConfig, name: string): string | null {
  const project = buildProject(cfg, target);
  if (!project || !target.exists(project)) return null;
  const r = target.sh(
    `dotnet msbuild ${q(project)} -getProperty:${name} -p:TargetFramework=${q(iosTfmOf(cfg))} -nologo`,
    { quiet: true, cwd: buildDir(cfg, target) });
  if (r.status !== 0) return null;
  const value = r.out.trim();
  // A multi-line answer is a diagnostic that slipped past the status check, not a property value.
  return value.length > 0 && !value.includes('\n') ? value : null;
}

/**
 * The iOS AOT cross pack installed on this machine, its versions, and whether the one the SDK resolves
 * carries its compiler binary.
 *
 * ⚠ The host↔target pair is DISCOVERED from the pack name (`…AOT.osx-arm64.Cross.iossimulator-arm64`)
 * rather than reconstructed: an Intel Mac, an Apple Silicon Mac, a simulator build and a device build each
 * name a different one, and only the installed set knows which this machine has.
 */
function aotCrossPacks(target: Target, expected: string | null):
  { pack: string; installed: string[]; compilerPresent: boolean }[] {
  const packs = packsDirectory(target);
  if (!packs) return [];

  return target.list(packs)
    .filter((name) => /^Microsoft\.NETCore\.App\.Runtime\.AOT\..+\.Cross\.ios/.test(name))
    .map((pack) => ({
      pack,
      installed: target.list(target.join(packs, pack)),
      // The exact path the failing task names, minus its `Sdk/..` detour.
      compilerPresent: Boolean(expected)
        && target.exists(target.join(packs, pack, expected!, 'tools', 'mono-aot-cross')),
    }));
}

/** Xcode's known Apple IDs, or null when the preference cannot be read. */
function xcodeAccountCount(target: Target): number | null {
  const raw = target.probe('defaults read com.apple.dt.Xcode DVTDeveloperAccountManagerAppleIDLists');
  return raw ? countXcodeAccounts(raw) : null;
}

/**
 * How many accounts that preference actually names.
 *
 * 🔴 **It counts ENTRIES, not email addresses.** Xcode does not store the address here — a signed-in
 * account prints as `identifier = "4F2E7F1A-…"`, a UUID — so a regex looking for `"…@…"` finds nothing and
 * `doctor` reports **no Apple ID on a Mac where one is signed in**, sending someone to fix what is
 * already fixed.
 *
 * ⚠ The key EXISTS with an empty list when no account is signed in (`"IDE.Identifiers.Prod" = ( );`), so
 * presence of the preference proves nothing and the parenthesised list has to be read.
 */
export function countXcodeAccounts(raw: string): number {
  // ⚠ Records inside the LIST, not brace groups anywhere: the value is a plist dict, so its own outer
  // braces are always there and counting those reports one account for an empty list.
  const open = raw.indexOf('(');
  const close = raw.lastIndexOf(')');
  if (open < 0 || close < open) return 0;
  // A record is `{ … }`; the field names inside are Xcode's business and may change.
  return (raw.slice(open, close).match(/\{/g) ?? []).length;
}

/** Installed provisioning profiles across BOTH stores Xcode has used. */
function provisioningProfileCount(target: Target): number {
  // `os.homedir()` would answer about THIS machine; `echo $HOME` asks the Mac doing the build.
  const home = target.probe('echo $HOME');
  if (!home) return 0;
  const stores = [
    `${home}/Library/Developer/Xcode/UserData/Provisioning Profiles`,
    `${home}/Library/MobileDevice/Provisioning Profiles`,
  ];
  let found = 0;
  for (const dir of stores) {
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

/** `$HOME` per remote host, memoised — see {@link remoteRoot}. */
const remoteHomes = new Map<string, string>();

/**
 * The REPOSITORY ROOT on the build machine — the counterpart of `cfg.root`, not of `projectDir`.
 *
 * 🔴 Handing the root to `dotnet build` builds whatever solution sits there: on this kit's own tree that
 * is the Windows sample and the test project, failing with `NETSDK1100: To build a project targeting
 * Windows on this operating system` — an error about a project nobody asked for, on a machine that is
 * working perfectly. Takes `cfg.remote.dir` when set, else `~/<basename of cfg.root>`.
 */
function remoteRoot(cfg: DeployConfig, target: Target): string {
  const dir = cfg.remote?.dir?.trim();
  if (dir) return dir;

  // ⚠ Memoised per host: called from findApp, build, publish and the artifact checks, and every miss is
  // a fresh ssh connection at roughly two seconds.
  //
  // 🔴 **A FAILURE IS NOT CACHED.** Caching one makes a single transient probe — a dropped connection, a
  // Mac still waking — permanent for the life of the process: every later call reads the cached empty and
  // reports "could not read the home directory" about a machine that is now answering perfectly.
  let home = remoteHomes.get(target.label);
  if (!home) {
    home = target.probe('echo $HOME');
    if (home) remoteHomes.set(target.label, home);
  }
  // 🔴 An empty probe must NOT become a path. `''` + '/' + basename is `/MyApp` — an absolute path at the
  // filesystem root, which exists nowhere, so every later check answers "not found" and the command
  // reports a missing build rather than a connection that failed.
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
 * ⚠ `cfg.project` may itself name a directory (the SDK accepts one), so this joins rather than assuming a
 * `.csproj`. It must never stop at the repo root — see {@link remoteRoot}.
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
 * Is the artifact newer than the build claiming it? The output directory is never cleaned between runs,
 * so without this a build that produced nothing hands back the previous run's output.
 *
 * 🔴 **It asks the WHOLE bundle, not the `.app` or its `Info.plist`.** Measured on a real Mac immediately
 * after a successful incremental build: the `.app` was 34 seconds old and its `Info.plist` **3.9 days** —
 * so reading either one refuses a build that had just succeeded on screen. Neither file is a clock; the
 * newest thing anywhere inside is.
 *
 * ⚠ **The allowance is THIRTY seconds for a remote target, one locally.** Two machines means two clocks:
 * `builtAfter` is stamped here and the mtimes are read there. Measured skew was 2 s in the forgiving
 * direction, but nothing guarantees the sign. It only has to catch "yesterday's leftover".
 */
export function builtBy(target: Target, full: string, builtAfter?: number): boolean {
  if (builtAfter === undefined) return true;
  const mtime = target.newestMtimeMs(full);
  const allowance = target.isRemote ? 30_000 : 1_000;
  // A path that cannot be read is NOT fresh — `newestMtimeMs` answers null rather than throwing, and
  // "unknown" must not be mistaken for "just built".
  return mtime !== null && mtime >= builtAfter - allowance;
}

/** The built .app, FOUND rather than composed: the bundle name follows the assembly, not the project. */
export function findApp(target: Target, cfg: DeployConfig, rid: string, builtAfter?: number): string | null {
  // `buildDir`, not `path.dirname` — see `projectDir`'s doc: a `project` naming a DIRECTORY resolves to
  // the PARENT, which looks for the .app one level too high and answers "not built" about a built app.
  // `buildDir` also redirects to the BUILD MACHINE's tree when `target` is remote; `cfg.root` names a
  // path on the machine running this CLI.
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
 */
export function checkExtensions(target: Target, app: string): { checked: number; problems: string[] } {
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
    // `codesign` failing to RUN is not a fact about the extension. Both outcomes block the install; only
    // the named cause differs.
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


export function build(target: Target, cfg: DeployConfig, rid: string, signing: string, extra: string): boolean {
  if (extra) console.log(`shenora: extra build args:${extra}`);
  console.log(`shenora: building ${cfg.project} (${iosTfmOf(cfg)}, ${rid})…`);
  const project = buildProject(cfg, target);
  const dir = buildDir(cfg, target);
  if (!project) return false;      // remoteRoot already reported why
  const command = `dotnet build ${q(project)} -c ${q(cfg.configuration)} `
    + `-f ${q(iosTfmOf(cfg))} -p:RuntimeIdentifier=${q(rid)}${signing}${extra} 2>&1 | tail -40`;
  // 🔴 Signing needs the login keychain, and an ssh session is a different audit session — codesign fails
  // `errSecInternalComponent` (see `SshTarget.gui`). Only a REMOTE build that signs needs the hand-off;
  // the simulator path passes an empty `signing`.
  const r = signing && target.isRemote
    ? target.gui(command, { tag: 'device-build' })
    : target.sh(command, { cwd: dir });

  // 🔴 A GUI build's output must be PRINTED here, because `gui` cannot stream: its script runs detached
  // in another session, so the log only exists as a return value. Without this a failed device build
  // prints "the build failed — see the output above" with nothing above it — a tool reporting a failure
  // it declines to explain.
  if (target.isRemote && signing && r.out.trim()) console.log(r.out.trimEnd());

  if (r.status === 0) return true;

  // The message is opaque about its cause: it means no profile on this Mac matches THIS bundle id, not
  // that none exist. Without an Apple ID signed into Xcode there is nothing that can create one.
  if (/Could not find any available provisioning profiles/i.test(r.out)) {
    console.error('\nshenora: no provisioning profile on that Mac covers this app.');
    console.error(`  It means no profile matches ${cfg.bundleId} — not that the Mac has none at all.`);
    console.error('  A profile is created by Xcode once an Apple ID is signed in:');
    console.error('    Xcode → Settings → Accounts → + → your Apple ID,');
    console.error('    then open any project once so it can register this device.');
    console.error('  `shenora ios doctor` reports the account and profile count before a build.');
    return false;
  }
  // Both shapes of the same mismatch: the up-front gate says "requires Xcode", and past it the linker
  // says MT4162 (a binding naming an API this Xcode never shipped).
  if (/requires Xcode/i.test(r.out) || /MT4162/.test(r.out)) {
    console.error('\nshenora: that is the .NET-for-iOS workload and this machine\'s Xcode disagreeing.');
    const sdk = xcodeSdkVersion(target);
    const band = sdk ? pickBindingBand(iosBindingBands(target, iosTfmOf(cfg)), sdk) : null;
    if (band) {
      // 🔴 A DEVICE build IS possible — see `pickBindingBand`.
      console.error(`  This Mac's Xcode SDK is ${sdk}, and it CAN build against bindings ${band}.`);
      // 🔴 IN THE PROJECT, not on the command line. `-p:` sets a GLOBAL MSBuild property, which
      // propagates into every project in the graph — including the plain `net10.0` ones, which have no
      // target platform at all. They then fail with `MSB4184 … "targetPlatformIdentifier" cannot have
      // zero length`, an error naming neither iOS nor the version that caused it.
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
 * `shenora ios build` — a DISTRIBUTABLE.
 *
 * 🔴 **`dotnet publish`, not `dotnet build`.** `deploy` builds a debug app and pushes it at a device; this
 * produces the artifact you hand to someone else — Release, trimmed and AOT-compiled by the iOS SDK's own
 * defaults, with `ArchiveOnBuild` so the SDK packages an `.ipa` rather than leaving a `.app` tree.
 *
 * ⚠ **Release is the DEFAULT and Debug is a different artifact**, carrying the interpreter and a
 * development provisioning profile. `--configuration` overrides it; the config's own `configuration`
 * (Debug, for the dev loop) is IGNORED.
 */
export function cmdBuild(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!requireFields(cfg, ['project'])) return;
  // Refuse a non-iOS TFM HERE rather than letting the SDK say `NETSDK1147: install the android workload`
  // twenty lines into a build — see `platformTfm`.
  if (!platformTfm(cfg, 'ios')) return;

  const { own, passthrough } = splitArgs(args);
  const extra = shellPassthrough(passthrough);
  const configuration = argValue(own, '--configuration') ?? 'Release';

  // 🔴 THERE IS NO SIMULATOR DISTRIBUTABLE, and this is measured: the iOS SDK will not publish a
  // simulator RID at all — *"A runtime identifier for a device architecture must be specified in order to
  // publish this project"* — and only says so after a full restore and forty lines of MSBuild.
  if (own.includes('--simulator')) {
    fail('there is no simulator distributable — the iOS SDK refuses to publish a simulator architecture.',
      '  For the dev loop use `shenora ios deploy --simulator`; `build` produces a DEVICE artifact.');
    return;
  }

  const rid = 'ios-arm64';
  console.log(`shenora: publishing ${cfg.project} (${iosTfmOf(cfg)}, ${rid}, ${configuration})…`);
  // Stamped BEFORE the publish: the output directory is never cleaned between runs, so an artifact older
  // than this belongs to a previous one.
  const startedAt = Date.now();
  const projDir = buildDir(cfg, target);
  const project = buildProject(cfg, target);
  if (!project) return;            // remoteRoot already reported why
  const publish = `cd ${q(projDir)} && dotnet publish ${q(project)} -c ${q(configuration)} `
    + `-f ${q(iosTfmOf(cfg))} -p:RuntimeIdentifier=${q(rid)} -p:ArchiveOnBuild=true${extra} 2>&1 | tail -40`;

  // 🔴 A DEVICE artifact, so this SIGNS and needs the login keychain an ssh session cannot reach — the
  // same wall as `deploy --device`. `ios-arm64` is the only RID this command accepts, so there is no
  // unsigned path through it at all.
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
  // having produced nothing when a target is skipped on a satisfied incremental check, and "publish
  // succeeded" with no file is the least actionable message possible.
  const outDir = target.join(projDir, 'bin', configuration, iosTfmOf(cfg), rid, 'publish');
  const artifact = findArtifact(target, outDir, startedAt) ?? findArtifact(target, target.dirname(outDir), startedAt);
  if (!artifact) {
    // Say STALE when a leftover is what was found — "no artifact appeared" beside a directory visibly
    // holding one is the most confusing message this tool could print.
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
 * the SDK leaves when signing could not produce one. The `.app` is still returned, so `cmdBuild` can say
 * "this is not distributable, here is why" instead of "nothing was produced" — different problems with
 * different fixes.
 *
 * @param builtAfter Epoch ms; an artifact older than this is STALE and is not returned. A stale `.ipa`
 *   beside a fresh `.app` yields the `.app` — this run's real output beats the previous run's archive.
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
 * 🔴 That case is a success here, but `|| true` cannot tell it apart from a name that does not exist — so
 * a typo becomes a silent install onto some other running simulator. Matching the state message keeps the
 * idempotent case working while letting a real failure through.
 *
 * ⚠ Matched loosely (case-insensitive, on the distinctive phrase) because this is Apple's wording and not
 * a contract: an unrecognised message is treated as a genuine failure, which costs a redundant error on a
 * booted device and never costs a wrong install.
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
 * The simulator half — no signing, no provisioning, no 7-day profile.
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

  // ⚠ `open -a Simulator` is what SHOWS the window. A booted device with no UI installs and launches
  // perfectly happily, which looks exactly like nothing happened.
  const name = argValue(args, '--simulator');
  if (name) {
    // 🔴 THE BOOT'S FAILURE IS CHECKED, not swallowed by `|| true`. Booting an ALREADY-booted simulator
    // exits non-zero, but so does a MISTYPED NAME — and swallowing both carries on to `install booted`
    // and lands on whatever else happened to be running: you debug the wrong build, on a device you did
    // not choose.
    const boot = target.sh(`xcrun simctl boot ${q(name)}`, { quiet: true });
    if (boot.status !== 0 && !isAlreadyBooted(boot.out)) {
      fail(`could not boot the simulator ${JSON.stringify(name)}.`,
        `  \`shenora ios simulators\` lists the names this Mac knows.\n\n${boot.out.trim()}`);
      return;
    }
  }
  target.sh('open -a Simulator || true', { quiet: true });

  // 🔴 ADDRESS THE NAMED DEVICE, not `booted`. Two simulators can be running, and `booted` then means
  // "whichever simctl picks" — so what you installed need not be what you are looking at.
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
 * Did the app SURVIVE its launch? Reported with the crash if not.
 *
 * 🔴 **`simctl launch` prints a pid and exits 0 for an app that dies immediately**, so "launched" is not
 * evidence of "running": this command said *"running in the simulator"* about a build that crashed on
 * startup every single time, while the simulator sat on its home screen.
 *
 * ⚠ The pid is a HOST pid — `simctl` runs simulator processes on the Mac itself — so `ps` can answer.
 */
function stillRunning(target: Target, launchOutput: string, cfg: DeployConfig): boolean {
  const pid = /:\s*(\d+)\s*$/m.exec(launchOutput.trim())?.[1];
  if (!pid) return true;      // Nothing to check against; do not invent a failure.

  // A crash-on-startup is over in well under a second.
  const alive = target.sh(`sleep 3; ps -p ${q(pid)} > /dev/null 2>&1 && echo alive || echo gone`,
    { quiet: true, timeoutMs: 60_000 });
  if (!/gone/.test(alive.out)) return true;

  fail('the app launched and then exited immediately.',
    '  A launch reports a pid whether or not the process survives, so this is checked rather than assumed.');
  const crash = target.probe(
    `xcrun simctl spawn booted log show --last 2m --predicate ${q(simulatorLogPredicate(cfg.bundleId))}`
    + ` 2>/dev/null | tail -25`);

  // 🔴 A metadata-token failure is a MIXED BUILD, and the plausible reading is the wrong one. After
  // pinning TargetPlatformVersion the app dies on `Token … is not valid in the scope of module
  // Microsoft.iOS.dll`, which reads as "these bindings are missing an API" — the very thing the pin's own
  // warning primes you to expect. It is `obj/` still holding metadata from the previous band.
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

export function deployToDevice(target: Target, cfg: DeployConfig, args: string[], extra: string): void {
  const device = resolveDevice(target, argValue(args, '--device'));
  if (!device) return;

  // CodesignProvision=Automatic + an Apple Development key is what lets an adopter reach a phone with NO
  // Xcode project of their own.
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
    // 🔴 NAME THE CAUSE THE OUTPUT ACTUALLY SHOWS. A single code-signing hint covers a Wi-Fi drop
    // mid-transfer ("the peer is no longer reachable") too, sending the reader to Settings > Device
    // Management to fix a network problem.
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
    // top line says only that the request failed. Build, sign and install have already SUCCEEDED, so a
    // bare "launch failed" reads as a code problem.
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
 * The NSPredicate that finds one app's lines in a booted simulator's unified log.
 *
 * 🔴 **`CONTAINS[c]`, and the `[c]` is the whole command working.** NSPredicate's `CONTAINS` is
 * case-SENSITIVE. The search term comes off a bundle id — lower case by convention — while
 * `processImagePath` carries the ASSEMBLY name, so `com.example.myapp` searches a path spelled
 * `MyApp.app/MyApp` and matches nothing at all. Measured on the simulator: **1 line of output (the header
 * alone) against 20,352 with `[c]`.**
 *
 * A header printed with nothing under it reads as *"my app logged nothing"*, not as *"your log reader is
 * broken"* — which is the failure this whole command exists to prevent.
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
 * 🔴 **The STATUS must not be discarded.** A run with no booted simulator prints the "last N lines from …"
 * header, then nothing, and exits 0 — which reads as *"my app logged nothing"* rather than *"your log
 * reader could not run"*.
 *
 * ⚠ **EMPTY is not FAILURE, and the two need different words.** A booted simulator whose app has not run
 * in the window legitimately matches nothing; "could not read the log" there sends someone hunting a
 * broken tool.
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

/** How long a device console stays attached. Startup is what it exists to show. */
const LOG_CONSOLE_SECONDS = 25;

export function cmdLog(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!requireFields(cfg, ['bundleId'])) return;
  const lines = argValue(args, '-n') ?? '80';

  if (args.includes('--device')) {
    const device = resolveDevice(target, argValue(args, '--device'));
    if (!device) return;
    // RELAUNCHES: a reader that attaches to an already-running app misses every line written during
    // startup, which is where the probes report.
    console.log(`shenora: relaunching ${cfg.bundleId} on ${device.name} with a console attached…\n`);
    // 🔴 STREAMED, not captured: a captured run prints nothing until the process exits, which for a
    // console attach is never. A streamed run returns no output to parse, so the status check below is
    // all there is.
    // 🔴 **BOUNDED BY TIME, because `head` does not stop it.** `head -N` closes the pipe after N lines and
    // `devicectl --console` does not die of the SIGPIPE — it stays attached to the phone, so the pipeline
    // never ends. Measured: five minutes of hanging after printing its 40 lines.
    const r = target.sh(`timeout ${LOG_CONSOLE_SECONDS} xcrun devicectl device process launch --console `
      + `--terminate-existing --device ${q(device.id)} ${q(cfg.bundleId)} 2>&1 | head -${q(lines)}`,
      { stream: true, timeoutMs: (LOG_CONSOLE_SECONDS + 30) * 1000 });

    // ⚠ THREE statuses mean success here, and a short list prints a scary message after a run that
    // worked. `124` is `timeout` firing, the NORMAL end; `141` is SIGPIPE from `head` closing first, when
    // the app is chatty; `0` is the app exiting on its own.
    if (r.status !== 0 && r.status !== 141 && r.status !== 124) {
      fail('could not attach a console to the device.',
        '  Check the app is installed (`shenora ios deploy`), and that the phone is unlocked.');
    }
    return;
  }

  console.log(`shenora: last ${lines} lines from ${cfg.bundleId} (booted simulator)\n`);
  // 🔴 `simctl spawn booted` runs the query INSIDE the simulator; without it this reads THIS MAC's own
  // unified log and answers with a header and nothing under it, whichever target you deployed to.
  // QUIET, so the three outcomes below decide what the user sees — `log show`'s stderr is noisy on a
  // perfectly good run, but discarding the STATUS with it makes a missing simulator look like a quiet app.
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
 * builds whatever its checkout happens to hold and nothing says so: a build succeeds, an app installs,
 * and it is last week's.
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

/**
 * `shenora ios provision` — mint the profiles a device build needs.
 *
 * ⚠ **Extensions are included by DEFAULT.** An app extension is provisioned separately from its
 * container and forgetting it fails at the very END of a device install, with an error naming the app.
 * Extra ids can be named as arguments; `cfg.bundleId` is always first.
 */
/**
 * The line `provision` opens with.
 *
 * 🔴 **IDENTITY IS OPT-IN, and the default leaves it out.** The Apple TEAM ID names a developer account
 * and the ssh target names a machine and often a home network. Neither is a credential, and both are
 * exactly the class of value that must not reach a public repo, a CI log or an assistant transcript —
 * none of which this command can see it is writing to. Reported by an adopter, 2026-09-04, who had
 * already been careful enough downstream to pipe `application-identifier` through `cut` for the sole
 * purpose of stripping the same team id before printing it.
 *
 * ⚠ **The count and the RESULT are what an operator needs**, and the per-id `ok`/`MISSING` lines below
 * carry the result already. `--verbose` adds the identity back for the case it genuinely diagnoses: a
 * profile minted against the wrong account.
 *
 * ⚠ Pure, so the redaction is testable without a Mac, an ssh target or a developer account. The command
 * itself cannot be driven from a test — `resolveTarget` builds a real `SshTarget` — which is precisely
 * why the part that must not leak is a function rather than an inline template.
 */
export function provisionBanner(
  count: number, team: string, targetLabel: string, verbose = false,
): string {
  const head = `shenora: provisioning ${count} bundle id(s)`;
  return verbose ? `${head} for team ${team} on ${targetLabel}` : head;
}

export function cmdProvision(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  if (!requireFields(cfg, ['bundleId'])) return;
  // ⚠ NOT `requireFields(… 'team')`: a team id identifies a developer account, and this config file is
  // normally tracked. It is read off the Mac's own certificate instead — see `teamId`.
  const team = teamId(target, cfg.team);
  if (!team) return;

  const { own } = splitArgs(args);
  const extra = own.filter((a) => !a.startsWith('-') && a.includes('.'));
  const ids = [cfg.bundleId, ...extra.filter((id) => id !== cfg.bundleId)];

  console.log(provisionBanner(ids.length, team, target.label, own.includes('--verbose')));
  const result = provisionBundleIds(target, team, ids);
  target.close();
  if (!result) return;

  console.log('');
  for (const id of ids) {
    const ok = !result.missing.includes(id);
    console.log(`  ${ok ? 'ok     ' : 'MISSING'} ${id}`);
  }
  if (result.missing.length > 0) {
    fail(`no profile was created for: ${result.missing.join(', ')}`,
      '  If Xcode asked to sign in, do it once on the Mac and run this again — the request needs an\n'
      + '  account it can authorise with, and it cannot ask you from here.');
    return;
  }
  console.log('\nshenora: profiles are in place. `shenora ios deploy --device` can sign now.');
}

/**
 * `shenora ios exec <command…>` — run something on the build machine. Resolves its target exactly as
 * `build` and `deploy` do: same `--host`, same config, same diagnosis when the Mac will not answer.
 *
 * Runs DIRECTLY rather than through the inspect service, so it needs nothing listening. The service
 * carries the same capability for its operator page, gated to loopback.
 */
export function cmdExec(cfg: DeployConfig | null, args: string[]): boolean {
  const { own } = splitArgs(args);
  // Everything that is not a flag or a flag's value — the command to run.
  const valued = new Set(['--host', '--key', '--device']);
  const words: string[] = [];
  for (let i = 0; i < own.length; i++) {
    const a = own[i]!;
    if (valued.has(a)) { i++; continue; }
    if (a.startsWith('--')) continue;
    words.push(a);
  }
  const command = words.join(' ');
  if (!command.trim()) return fail('nothing to run.', '  shenora ios exec "xcodebuild -version"');

  const target = resolveTarget(cfg, args);
  if (!target) return false;
  const r = target.sh(command);
  target.close();
  if (r.status !== 0) process.exitCode = 1;
  return r.status === 0;
}

export function cmdShot(cfg: DeployConfig, args: string[]): void {
  const target = resolveTarget(cfg, args);
  if (!target) return;
  const out = argValue(args, '-o') ?? 'shenora-sim.png';
  // 🔴 The simulator is on the TARGET, so the PNG lands there. Written straight to `out` on a remote Mac
  // it sits in that Mac's home directory while this command prints a local-looking filename — a
  // screenshot you cannot look at, reported as a success. Stage it there, then pull it here.
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
