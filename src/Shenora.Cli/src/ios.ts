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
import { sh, probe, q, fail, argValue } from './exec.js';
import { requireFields, type DeployConfig } from './config.js';

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
 * Connected iPhones, as devicectl reports them.
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
 * everywhere it actually runs.** Prefer a file an implementation cannot be clever about.
 */
function devices(): Device[] {
  const out = path.join(os.tmpdir(), `shenora-devicectl-${process.pid}.json`);
  let json = '';
  try {
    probe(`xcrun devicectl list devices --json-output ${q(out)} >/dev/null 2>&1`);
    json = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : '';
  } catch {
    return [];
  } finally {
    try { fs.rmSync(out, { force: true }); } catch { /* a leftover temp file is not worth failing over */ }
  }
  if (!json.trim()) return [];
  try {
    const parsed = JSON.parse(json.slice(json.indexOf('{'))) as {
      result?: { devices?: Array<Record<string, any>> };
    };
    return (parsed.result?.devices ?? [])
      .map((d) => ({
        id: String(d['identifier'] ?? ''),
        name: String(d['deviceProperties']?.name ?? '(unnamed)'),
        state: describeConnection(d['connectionProperties']),
        os: String(d['deviceProperties']?.osVersionNumber ?? ''),
      }))
      .filter((d) => d.id);
  } catch {
    return [];
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
  const found = devices();
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
  const found = devices();
  if (found.length === 0) {
    console.log('shenora: no devices. Plug a phone in, unlock it, tap Trust.');
    return;
  }
  for (const d of found) console.log(`  ${d.name}  iOS ${d.os}  ${d.state}  ${d.id}`);
}

export function cmdSimulators(): void {
  if (!assertMac()) return;
  const out = probe(`xcrun simctl list devices available | grep -E "^    " | sed 's/^ *//'`);
  console.log(out || 'shenora: no simulators installed — Xcode > Settings > Components.');
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
  const identities = probe('security find-identity -v -p codesigning 2>/dev/null | grep -c "Apple Development"');
  const hasIdentity = Boolean(identities) && identities !== '0';
  line('signing identity', hasIdentity ? `${identities} found` : '(none — Xcode > Settings > Accounts)', hasIdentity);

  const found = devices();
  line('device', found.length ? found.map((d) => d.name).join(', ') : '(none connected — simulator still works)', true);

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

/** The built .app, FOUND rather than composed: the bundle name follows the assembly, not the project. */
function findApp(cfg: DeployConfig, rid: string): string | null {
  const dir = path.join(cfg.root, path.dirname(cfg.project), 'bin', cfg.configuration, cfg.tfm, rid);
  if (!fs.existsSync(dir)) return null;
  const app = fs.readdirSync(dir).find((e) => e.endsWith('.app'));
  return app ? path.join(dir, app) : null;
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
    if (!probe(`codesign -d --entitlements - ${q(appex)} 2>/dev/null`).includes('application-identifier')) {
      problems.push(`${entry}: no application-identifier entitlement — the system refuses to launch it.`);
    }
  }
  return { checked, problems };
}

/**
 * Anything after a bare `--` is passed straight to `dotnet build`.
 *
 * 🔴 IT IS A COMMAND-LINE FLAG AND NOT A CONFIG FIELD, deliberately. The case that forced it is an Xcode
 * whose version the installed .NET-for-iOS workload refuses ("requires Xcode 26.0, the current version is
 * 26.3"), cleared with `-p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly`. **Which Xcode a machine
 * happens to have is a fact about THAT MACHINE**, so writing the override into a committed file would
 * silence the mismatch for everyone who clones the repo, permanently — including whoever hits it when it
 * is the real problem. On the command line it stays visible and per-machine.
 */
export function splitArgs(args: readonly string[]): { own: string[]; extra: string } {
  const i = args.indexOf('--');
  if (i < 0) return { own: [...args], extra: '' };
  const rest = args.slice(i + 1);
  // ⚠ `own` MUST stop at the separator. `argValue` scans for a flag and takes the next token, so with a
  // single flat array `deploy --simulator -- -p:Foo=1` reads the simulator's NAME as `-p:Foo=1` and then
  // tries to boot a device by that name. Splitting once, here, is why the flag readers can stay naive.
  return { own: args.slice(0, i), extra: rest.length ? ` ${rest.join(' ')}` : '' };
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

  const { own, extra } = splitArgs(args);
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
  const dir = path.join(cfg.root, path.dirname(cfg.project), 'bin', configuration, cfg.tfm, rid, 'publish');
  const artifact = findArtifact(dir) ?? findArtifact(path.dirname(dir));
  if (!artifact) {
    fail(`the publish reported success but no .ipa or .app appeared under ${dir}.`,
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
 */
export function findArtifact(dir: string): string | null {
  if (!fs.existsSync(dir)) return null;
  const entries = fs.readdirSync(dir);
  const ipa = entries.find((e) => e.endsWith('.ipa'));
  if (ipa) return path.join(dir, ipa);
  const app = entries.find((e) => e.endsWith('.app'));
  return app ? path.join(dir, app) : null;
}

/** Human-readable size — `du -sh` handles a `.app` DIRECTORY, which `stat` would report as ~loose bytes. */
function sizeOf(target: string): string {
  const out = probe(`du -sh ${q(target)}`).split(/\s+/)[0];
  return out ? `${out} on disk` : 'size unknown';
}

export function cmdDeploy(cfg: DeployConfig, args: string[]): void {
  if (!assertMac()) return;
  if (!requireFields(cfg, ['project', 'bundleId'])) return;
  const { own, extra } = splitArgs(args);
  if (own.includes('--simulator')) deployToSimulator(cfg, own, extra);
  else deployToDevice(cfg, own, extra);
}

/**
 * The simulator half — no signing, no provisioning, no 7-day profile. This is the loop most work should
 * use; hardware is for what only hardware can answer (background playback, real codecs, thermals).
 */
function deployToSimulator(cfg: DeployConfig, args: string[], extra: string): void {
  const rid = simulatorRid();
  if (!build(cfg, rid, '', extra)) return;

  const app = findApp(cfg, rid);
  if (!app) {
    fail(`the build succeeded but no .app appeared under bin/${cfg.configuration}/${cfg.tfm}/${rid}.`);
    return;
  }
  console.log(`shenora: ${path.basename(app)}`);

  // ⚠ `open -a Simulator` is what actually SHOWS the window. A booted device with no UI installs and
  // launches perfectly happily, which looks exactly like nothing happened.
  const name = argValue(args, '--simulator');
  if (name) sh(`xcrun simctl boot ${q(name)} 2>/dev/null || true`, { quiet: true });
  sh('open -a Simulator || true', { quiet: true });

  if (sh(`xcrun simctl install booted ${q(app)} 2>&1 | tail -10`).status !== 0) {
    fail('install failed.',
      '  If it says no booted device, pass --simulator "iPhone 16 Pro" (`shenora ios simulators`).');
    return;
  }
  if (sh(`xcrun simctl launch booted ${q(cfg.bundleId)} 2>&1 | tail -10`).status !== 0) {
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
  if (!build(cfg, 'ios-arm64', signing, extra)) return;

  const app = findApp(cfg, 'ios-arm64');
  if (!app) {
    fail(`the build succeeded but no .app appeared under bin/${cfg.configuration}/${cfg.tfm}/ios-arm64.`);
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
  sh(`xcrun simctl spawn booted log show --last 10m --style compact `
    + `--predicate ${q(simulatorLogPredicate(cfg.bundleId))} 2>/dev/null | tail -${q(lines)}`);
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
