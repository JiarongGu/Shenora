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

/** Connected iPhones, as devicectl reports them. */
function devices(): Device[] {
  const json = probe('xcrun devicectl list devices --json-output /dev/stdout 2>/dev/null');
  if (!json) return [];
  try {
    const parsed = JSON.parse(json.slice(json.indexOf('{'))) as {
      result?: { devices?: Array<Record<string, any>> };
    };
    return (parsed.result?.devices ?? [])
      .map((d) => ({
        id: String(d['identifier'] ?? ''),
        name: String(d['deviceProperties']?.name ?? '(unnamed)'),
        state: String(d['connectionProperties']?.tunnelState ?? 'unknown'),
        os: String(d['deviceProperties']?.osVersionNumber ?? ''),
      }))
      .filter((d) => d.id);
  } catch {
    return [];
  }
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
  if (sh(`xcrun devicectl device install app --device ${q(target.id)} ${q(app)} 2>&1 | tail -20`).status !== 0) {
    fail('install failed.',
      '  If it says the app could not be verified, the certificate is not TRUSTED on the phone yet:\n'
      + '  Settings > General > VPN & Device Management > your developer account > Trust.');
    return;
  }

  console.log('\nshenora: launching…');
  if (sh(`xcrun devicectl device process launch --device ${q(target.id)} ${q(cfg.bundleId)} 2>&1 | tail -20`).status !== 0) {
    fail('launch failed.');
    return;
  }
  console.log(`\nshenora: running on ${target.name}. Read its output with \`shenora ios log\`.`);
}

/**
 * ⚠ FILTER BEFORE TAILING. A process-wide predicate is ~99% platform chatter, so `tail -n` over the raw
 * stream shows a screen of noise with none of the app's own lines — which looks exactly like a broken log
 * sink. This kit rebuilt that same mistake once per harness before writing it down.
 */
export function cmdLog(cfg: DeployConfig, args: string[]): void {
  if (!assertMac()) return;
  if (!requireFields(cfg, ['bundleId'])) return;
  const lines = argValue(args, '-n') ?? '80';
  const leaf = cfg.bundleId.split('.').pop() ?? cfg.bundleId;
  console.log(`shenora: last ${lines} lines from ${cfg.bundleId}\n`);
  sh(`log show --last 10m --style compact --predicate ${q(`processImagePath CONTAINS "${leaf}"`)} `
    + `2>/dev/null | tail -${q(lines)}`);
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
