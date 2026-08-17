// The iOS loop: doctor · devices · simulators · deploy (simulator or hardware) · log · shot.
//
// 🔴 WHY THIS SHIPS. A hybrid framework's real measure is how little native work an adopting app has to
// redo — and the device loop is part of that. Every check below exists because this kit hit the failure
// it catches, on real hardware, and each one costs a day to rediscover.
//
// ⚠ It runs LOCALLY on macOS, which removes the hardest part of this kit's own loop: signing needs the
// login keychain, and an ssh session cannot reach it, so the repo's devtools tunnel their build through a
// GUI session. On your own Mac you are already in one.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { sh, probe, q, fail, argValue, splitArgs, shellPassthrough } from './exec.js';
import { projectDir, requireFields, type DeployConfig } from './config.js';

interface Device {
  id: string;
  name: string;
  state: string;
  os: string;
}

export function assertMac(): boolean {
  if (process.platform === 'darwin') return true;
  return fail(
    'iOS work needs macOS — Xcode, codesign, simctl and devicectl are Apple-only.',
    '  There is no way around it: even a cross-built app has to be signed and installed from a Mac.',
  );
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
function devices(): DeviceLookup {
  const out = path.join(os.tmpdir(), `shenora-devicectl-${process.pid}.json`);
  try {
    // ⚠ stderr is CAPTURED rather than redirected to /dev/null: it is the only place devicectl says
    // why it refused, and the callers now have somewhere to put that. `quiet` keeps it off a healthy run.
    const r = sh(`xcrun devicectl list devices --json-output ${q(out)}`, { quiet: true });
    if (r.status !== 0) {
      return { ok: false, detail: r.out.trim() || `xcrun devicectl exited ${r.status}` };
    }
    const json = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : '';
    return parseDeviceList(json);
  } catch (error) {
    return { ok: false, detail: `could not run devicectl — ${(error as Error).message}` };
  } finally {
    try { fs.rmSync(out, { force: true }); } catch { /* a leftover temp file is not worth failing over */ }
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
function resolveTarget(wanted: string | undefined): Device | null {
  const lookup = devices();
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

export function cmdDevices(): void {
  if (!assertMac()) return;
  const lookup = devices();
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

export function cmdSimulators(): void {
  if (!assertMac()) return;
  // The list and the filter are SEPARATE steps: piped through grep, a failed `xcrun` and a genuinely
  // empty list both came back as '' — and "no simulators installed" beside a broken xcode-select sends
  // the reader to install components they already have.
  const list = sh('xcrun simctl list devices available', { quiet: true });
  if (list.status !== 0) {
    fail('could not list simulators — `xcrun simctl` itself failed.',
      `  Usually xcode-select points at a missing or stale Xcode; \`xcode-select -p\` shows which.\n\n${list.out.trim()}`);
    return;
  }
  const rows = list.out.split('\n').filter((l) => /^ {4}\S/.test(l)).map((l) => l.trimStart());
  console.log(rows.length ? rows.join('\n') : 'shenora: no simulators installed — Xcode > Settings > Components.');
}

export function cmdDoctor(cfg: DeployConfig | null): void {
  if (!assertMac()) return;
  let ok = true;
  const line = (label: string, value: string, good = true): void => {
    console.log(`  ${good ? 'ok     ' : 'MISSING'} ${label.padEnd(20)} ${value}`);
    if (!good) ok = false;
  };

  const xcode = probe('xcodebuild -version | head -1');
  line('Xcode', xcode || '(not found — install it from the App Store)', Boolean(xcode));

  const dotnet = probe('dotnet --version');
  line('.NET SDK', dotnet || '(not found)', Boolean(dotnet));

  const workload = probe('dotnet workload list 2>/dev/null | grep -i ios | head -1');
  line('ios workload', workload || '(run `dotnet workload install maui-ios`)', Boolean(workload));

  // An "Apple Development" identity is what a DEVICE build signs with. Absent, the build fails late with
  // a codesign error that reads as a project problem rather than a machine one.
  // Counted HERE, not with `grep -c`: grep exits 1 for zero matches, so a locked keychain (`security`
  // failing outright) and a genuinely empty identity list were the same '' — and "none, go to Xcode
  // Settings" about a keychain problem sends the reader to a screen that is already correct.
  const identity = sh('security find-identity -v -p codesigning', { quiet: true });
  const identityCount = identity.status === 0 ? (identity.out.match(/Apple Development/g)?.length ?? 0) : 0;
  line('signing identity',
    identity.status !== 0
      ? '(could not ask — `security` failed; a locked login keychain does this. Unlock it and retry)'
      : identityCount > 0 ? `${identityCount} found` : '(none — Xcode > Settings > Accounts)',
    identityCount > 0);

  // ⚠ A devicectl failure is reported as a failure here too, and NOT as `good: false`: doctor answers
  // "can this machine build and deploy", and a device is optional for that — the simulator path works
  // without one. Saying the reader broke is information; failing the whole check over it is not.
  const lookup = devices();
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
 * Is the artifact newer than the build claiming it? Same rule (and the same one-second filesystem
 * allowance) as `findPackage` on the Android side: the output directory is never cleaned between runs,
 * so without this a build that produced nothing hands back the previous run's output. A `.app` is a
 * DIRECTORY whose own mtime can survive a rebuild of its contents — its Info.plist is rewritten every
 * build and is the honest clock.
 */
function builtBy(full: string, builtAfter?: number): boolean {
  if (builtAfter === undefined) return true;
  const plist = path.join(full, 'Info.plist');
  const clock = full.endsWith('.app') && fs.existsSync(plist) ? plist : full;
  return fs.statSync(clock).mtimeMs >= builtAfter - 1000;
}

/** The built .app, FOUND rather than composed: the bundle name follows the assembly, not the project. */
function findApp(cfg: DeployConfig, rid: string, builtAfter?: number): string | null {
  // `projectDir`, not `path.dirname` — see its doc: a `project` naming a DIRECTORY resolved to the
  // PARENT, so this looked for the .app one level too high and answered "not built" about a built app.
  const dir = path.join(projectDir(cfg), 'bin', cfg.configuration, cfg.tfm, rid);
  if (!fs.existsSync(dir)) return null;
  const app = fs.readdirSync(dir).find((e) => e.endsWith('.app'));
  if (!app) return null;
  const full = path.join(dir, app);
  return builtBy(full, builtAfter) ? full : null;
}

/**
 * 🔴 An app EXTENSION is provisioned separately from its container and will not launch without its own
 * entitlements and embedded profile. Checked BEFORE install, because one that cannot launch installs
 * perfectly happily and then does nothing: a Live Activity shows as an empty capsule while every
 * ActivityKit call reports success. **A simulator cannot catch this** — it does not enforce code signing.
 * This kit shipped that bug and spent three device round-trips finding it.
 */
function checkExtensions(app: string): { checked: number; problems: string[] } {
  const plugins = path.join(app, 'PlugIns');
  if (!fs.existsSync(plugins)) return { checked: 0, problems: [] };
  const problems: string[] = [];
  let checked = 0;
  for (const entry of fs.readdirSync(plugins).filter((e) => e.endsWith('.appex'))) {
    checked++;
    const appex = path.join(plugins, entry);
    if (!fs.existsSync(path.join(appex, 'embedded.mobileprovision'))) {
      problems.push(`${entry}: no embedded.mobileprovision — it installs and never runs.`);
    }
    // `codesign` failing to RUN is not a fact about the extension — the install diagnostic below has
    // the rule. Both outcomes still block the install; only the named cause differs.
    const entitlements = sh(`codesign -d --entitlements - ${q(appex)}`, { quiet: true });
    if (entitlements.status !== 0) {
      problems.push(`${entry}: codesign could not read it `
        + `(${entitlements.out.trim().split('\n')[0] || 'no detail'}) — cannot verify it is launchable.`);
    } else if (!entitlements.out.includes('application-identifier')) {
      problems.push(`${entry}: no application-identifier entitlement — the system refuses to launch it.`);
    }
  }
  return { checked, problems };
}


function build(cfg: DeployConfig, rid: string, signing: string, extra: string): boolean {
  if (extra) console.log(`shenora: extra build args:${extra}`);
  console.log(`shenora: building ${cfg.project} (${cfg.tfm}, ${rid})…`);
  const r = sh(
    `dotnet build ${q(path.join(cfg.root, cfg.project))} -c ${q(cfg.configuration)} `
    + `-f ${q(cfg.tfm)} -p:RuntimeIdentifier=${q(rid)}${signing}${extra} 2>&1 | tail -40`,
    { cwd: cfg.root },
  );
  if (r.status === 0) return true;
  // The Xcode gate is common enough, and its message specific enough, to name the escape hatch here
  // rather than leave an adopter to find it. Detected from the SDK's own wording.
  if (/requires Xcode/i.test(r.out)) {
    console.error('\nshenora: that is the .NET-for-iOS workload refusing this machine\'s Xcode version.');
    console.error('  Match the pair (install the Xcode it names, or a workload built for the one you have),');
    console.error('  or override per-machine:');
    console.error('    shenora ios deploy --simulator -- -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly');
    console.error('  ⚠ Both flags are needed — the first clears the up-front gate, the second clears MT0180');
    console.error('    from the linker step. It is a dev-loop unblock, NOT a shipping configuration.');
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
  if (!assertMac()) return;
  if (!requireFields(cfg, ['project'])) return;

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
  console.log(`shenora: publishing ${cfg.project} (${cfg.tfm}, ${rid}, ${configuration})…`);
  // Stamped BEFORE the publish, exactly as the Android side does: the output directory is never
  // cleaned between runs, so an artifact older than this belongs to a previous one.
  const startedAt = Date.now();
  const r = sh(
    `dotnet publish ${q(path.join(cfg.root, cfg.project))} -c ${q(configuration)} `
    + `-f ${q(cfg.tfm)} -p:RuntimeIdentifier=${q(rid)} -p:ArchiveOnBuild=true${extra} 2>&1 | tail -40`,
    { cwd: cfg.root },
  );
  if (r.status !== 0) {
    fail('the publish failed — see the output above.',
      '  A Release build runs the full linker, so it can fail where `deploy` succeeds.');
    return;
  }

  // 🔴 REPORT THE ARTIFACT, and refuse to claim success without finding one. `dotnet publish` exits 0
  // having produced nothing more than once in this repo's history (a skipped target with a satisfied
  // incremental check), and "publish succeeded" with no file is the least actionable message possible.
  const dir = path.join(projectDir(cfg), 'bin', configuration, cfg.tfm, rid, 'publish');
  const artifact = findArtifact(dir, startedAt) ?? findArtifact(path.dirname(dir), startedAt);
  if (!artifact) {
    // Say STALE when a leftover is what was found — "no artifact appeared" beside a directory visibly
    // holding one is the most confusing message this tool could print (the Android fix, ported).
    const stale = findArtifact(dir) ?? findArtifact(path.dirname(dir));
    fail(stale
      ? `the publish reported success but the only artifact under ${dir} predates this build `
        + `(${stale}) — it is left over from an earlier run, so nothing was produced this time.`
      : `the publish reported success but no .ipa or .app appeared under ${dir}.`,
      '  That usually means a target was skipped — try again after `rm -rf bin obj`.');
    return;
  }

  const size = sizeOf(artifact);
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
export function findArtifact(dir: string, builtAfter?: number): string | null {
  if (!fs.existsSync(dir)) return null;
  const entries = fs.readdirSync(dir);
  const fresh = (name: string | undefined): string | null => {
    if (!name) return null;
    const full = path.join(dir, name);
    return builtBy(full, builtAfter) ? full : null;
  };
  return fresh(entries.find((e) => e.endsWith('.ipa')))
    ?? fresh(entries.find((e) => e.endsWith('.app')));
}

/** Human-readable size — `du -sh` handles a `.app` DIRECTORY, which `stat` would report as ~loose bytes. */
function sizeOf(target: string): string {
  const out = probe(`du -sh ${q(target)}`).split(/\s+/)[0];
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
  if (!assertMac()) return;
  if (!requireFields(cfg, ['project', 'bundleId'])) return;
  const { own, passthrough } = splitArgs(args);
  const extra = shellPassthrough(passthrough);
  if (own.includes('--simulator')) deployToSimulator(cfg, own, extra);
  else deployToDevice(cfg, own, extra);
}

/**
 * The simulator half — no signing, no provisioning, no 7-day profile. This is the loop most work should
 * use; hardware is for what only hardware can answer (background playback, real codecs, thermals).
 */
function deployToSimulator(cfg: DeployConfig, args: string[], extra: string): void {
  const rid = simulatorRid();
  const startedAt = Date.now();
  if (!build(cfg, rid, '', extra)) return;

  const app = findApp(cfg, rid, startedAt);
  if (!app) {
    const stale = findApp(cfg, rid);
    fail(stale
      ? `the build reported success but ${path.basename(stale)} predates it — nothing was produced `
        + 'this time, and installing the leftover would run yesterday\'s code as if it were today\'s.'
      : `the build succeeded but no .app appeared under bin/${cfg.configuration}/${cfg.tfm}/${rid}.`);
    return;
  }
  console.log(`shenora: ${path.basename(app)}`);

  // ⚠ `open -a Simulator` is what actually SHOWS the window. A booted device with no UI installs and
  // launches perfectly happily, which looks exactly like nothing happened.
  const name = argValue(args, '--simulator');
  if (name) {
    // 🔴 THE BOOT'S FAILURE USED TO BE SWALLOWED by `|| true`, which was there for a real reason —
    // booting an ALREADY-booted simulator exits non-zero — but it swallowed a MISTYPED NAME with it. The
    // run then carried on to `install booted` and landed on whatever else happened to be running: you
    // debug the wrong build, on a device you did not choose. This CLI refuses to guess in exactly this
    // situation twice already (`resolveTarget` here, `resolveDevice` on the Android side); the simulator
    // path was the one place it guessed.
    const boot = sh(`xcrun simctl boot ${q(name)}`, { quiet: true });
    if (boot.status !== 0 && !isAlreadyBooted(boot.out)) {
      fail(`could not boot the simulator ${JSON.stringify(name)}.`,
        `  \`shenora ios simulators\` lists the names this Mac knows.\n\n${boot.out.trim()}`);
      return;
    }
  }
  sh('open -a Simulator || true', { quiet: true });

  // 🔴 ADDRESS THE NAMED DEVICE, not `booted`. Even with the boot check above, `booted` is the wrong
  // target whenever a name was given: two simulators can be running, and `booted` then means "whichever
  // simctl picks". Naming one is the only way to be sure the thing you installed is the thing you are
  // looking at.
  const target = name ?? 'booted';
  if (sh(`xcrun simctl install ${q(target)} ${q(app)} 2>&1 | tail -10`).status !== 0) {
    fail('install failed.',
      '  If it says no booted device, pass --simulator "iPhone 16 Pro" (`shenora ios simulators`).');
    return;
  }
  if (sh(`xcrun simctl launch ${q(target)} ${q(cfg.bundleId)} 2>&1 | tail -10`).status !== 0) {
    fail('launch failed.');
    return;
  }
  console.log('\nshenora: running in the simulator. Screenshot it with `shenora ios shot`.');
}

function deployToDevice(cfg: DeployConfig, args: string[], extra: string): void {
  const target = resolveTarget(argValue(args, '--device'));
  if (!target) return;

  // CodesignProvision=Automatic + an Apple Development key is what lets an adopter reach a phone with NO
  // Xcode project of their own — the whole point of this command.
  const signing = ` -p:CodesignProvision=Automatic -p:CodesignKey=${q('Apple Development')}`;
  const startedAt = Date.now();
  if (!build(cfg, 'ios-arm64', signing, extra)) return;

  const app = findApp(cfg, 'ios-arm64', startedAt);
  if (!app) {
    const stale = findApp(cfg, 'ios-arm64');
    fail(stale
      ? `the build reported success but ${path.basename(stale)} predates it — nothing was produced `
        + 'this time, and installing the leftover would run yesterday\'s code as if it were today\'s.'
      : `the build succeeded but no .app appeared under bin/${cfg.configuration}/${cfg.tfm}/ios-arm64.`);
    return;
  }
  console.log(`shenora: ${path.basename(app)}`);

  const ext = checkExtensions(app);
  if (ext.problems.length > 0) {
    console.error('\nshenora: an app extension is not device-launchable:');
    for (const p of ext.problems) console.error(`  ${p}`);
    fail('refusing to install — it would run with the extension silently dead.',
      '  A simulator cannot catch this: it does not enforce code signing.');
    return;
  }
  if (ext.checked > 0) console.log(`shenora: app extensions ok (${ext.checked})`);

  console.log('\nshenora: installing…');
  const install = sh(`xcrun devicectl device install app --device ${q(target.id)} ${q(app)} 2>&1 | tail -20`);
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
  const launch = sh(`xcrun devicectl device process launch --device ${q(target.id)} ${q(cfg.bundleId)} 2>&1 | tail -20`);
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
  console.log(`\nshenora: running on ${target.name}. Read its output with \`shenora ios log\`.`);
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
  if (!assertMac()) return;
  if (!requireFields(cfg, ['bundleId'])) return;
  const lines = argValue(args, '-n') ?? '80';

  if (args.includes('--device')) {
    const target = resolveTarget(argValue(args, '--device'));
    if (!target) return;
    // RELAUNCHES, deliberately. A reader that attached to an already-running app misses every line
    // written during startup, which for this kit is where the probes report.
    console.log(`shenora: relaunching ${cfg.bundleId} on ${target.name} with a console attached…\n`);
    const r = sh(`xcrun devicectl device process launch --console --terminate-existing `
      + `--device ${q(target.id)} ${q(cfg.bundleId)} 2>&1 | head -${q(lines)}`);
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
  const r = sh(`xcrun simctl spawn booted log show --last 10m --style compact `
    + `--predicate ${q(simulatorLogPredicate(cfg.bundleId))} 2>/dev/null | tail -${q(lines)}`,
    { quiet: true });

  const outcome = describeLogOutcome(r.status, r.out);
  if (outcome.kind === 'failed') fail(outcome.message, outcome.hint);
  else console.log(outcome.kind === 'empty' ? outcome.message : outcome.text);
}

export function cmdShot(_cfg: DeployConfig, args: string[]): void {
  if (!assertMac()) return;
  const out = argValue(args, '-o') ?? 'shenora-sim.png';
  if (sh(`xcrun simctl io booted screenshot ${q(out)}`).status !== 0) {
    fail('no booted simulator to screenshot.', '  Run `shenora ios deploy --simulator` first.');
    return;
  }
  console.log(`shenora: ${out}`);
}
