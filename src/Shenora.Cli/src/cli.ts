#!/usr/bin/env node
// `shenora` — the kit's CLI. Its job is the LAST MILE: taking a built app all the way onto a simulator or
// a real iPhone, with no Xcode project of the adopter's own.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadConfig, CONFIG_FILE, SAMPLE_CONFIG, type DeployConfig } from './config.js';
import { cmdDevices, cmdDoctor, cmdBuild, cmdDeploy, cmdLog, cmdSimulators, cmdShot, cmdPush, cmdProvision, cmdExec } from './ios.js';
import {
  cmdDoctor as androidDoctor, cmdDevices as androidDevices, cmdDeploy as androidDeploy,
  cmdLog as androidLog, cmdBuild as androidBuild,
} from './android.js';
import { cmdCopy, cmdSync } from './copy.js';
import {
  cmdInspectServe, cmdInspectDevices, cmdInspectReport, cmdInspectEval, positionals,
} from './inspect/commands.js';

const USAGE = `shenora — take a built app onto a simulator or a real iPhone

  shenora init                 write a starter ${CONFIG_FILE}
  shenora copy                 stage the built web bundle into the app head
  shenora sync                 copy, then restore the app head's dependencies

  shenora ios doctor           can this Mac build, sign and install?
  shenora ios devices          connected iPhones
  shenora ios simulators       installed simulators
  shenora ios build [--configuration <name>] [--simulator]
                               a DISTRIBUTABLE: dotnet publish, Release by default, .ipa for a device
  shenora ios deploy --simulator ["iPhone 16 Pro"]
                               build → boot → install → launch (no signing needed)
  shenora ios deploy [--device <name|id>]
                               build → SIGN → verify extensions → install → launch
  shenora ios log [-n <lines>] [--device [<name|id>]]
                               the app's own output — the booted SIMULATOR by default; --device
                               relaunches on the phone with a console attached (startup is the point)
  shenora ios shot [-o <file>] screenshot the booted simulator
  shenora ios push             send this working tree to the remote Mac (uncommitted edits included)
  shenora ios provision [<extra.bundle.id>…] [--verbose]
                               mint the signing profiles a device build needs (app + its extensions).
                               --verbose also prints the team id and the Mac; both identify YOU, so
                               they are left out by default
  shenora ios exec <command>   run a command on the build machine (the Mac) over ssh

  shenora android doctor       can this machine build, install and log? (works on Windows too)
  shenora android devices      attached devices and emulators, including the ones adb calls unauthorized
  shenora android deploy [--device <serial>]
                               build → install → launch
  shenora android log [-n <lines>] [--device <serial>] [--all]
                               the app's own lines, filtered by tag then tailed HERE
  shenora android build [--configuration <name>] [--aab]
                               a distributable: .apk, or .aab for Play

  shenora inspect serve [--port <n>] [--app <origin>]
                               attach to a running app: a DEVICE opens the printed URL and polls
  shenora inspect devices      which devices have checked in
  shenora inspect report [--device <name>]
                               what a device says about itself: shell, viewport, what it can reach
  shenora inspect eval <expr> [--device <name>]
                               run an expression in the device's page and print what came back

iOS work can run on a Mac elsewhere on your network — every \`ios\` verb takes \`--host\`:
  shenora ios doctor --host you@mac.local     (or set SHENORA_IOS_HOST once)

Anything after \`--\` goes straight to \`dotnet build\`, for what is true of YOUR machine only:
  shenora ios deploy --simulator -- -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly

Config: ${CONFIG_FILE}, found here or in any parent directory.
`;

function init(): void {
  const target = path.join(process.cwd(), CONFIG_FILE);
  if (fs.existsSync(target)) {
    console.error(`shenora: ${CONFIG_FILE} already exists — leaving it alone.`);
    process.exitCode = 1;
    return;
  }
  fs.writeFileSync(target, SAMPLE_CONFIG);
  console.log(`shenora: wrote ${CONFIG_FILE}. Set project + bundleId, then run \`shenora ios doctor\`.`);
}

function needConfig(): DeployConfig | null {
  const cfg = loadConfig();
  if (cfg) return cfg;
  console.error(`shenora: no ${CONFIG_FILE} here or in any parent directory.`);
  console.error('  Run `shenora init` to write one.');
  process.exitCode = 1;
  return null;
}

// ⚠ These ask about the MACHINE, so they must not require a config file — "can this Mac do it at all?" is
// the question someone has BEFORE they have a project wired.
const MACHINE_ONLY = new Set(['doctor', 'devices', 'simulators']);

const IOS_VERBS = new Set(['doctor', 'devices', 'simulators', 'build', 'deploy', 'log', 'shot', 'push', 'provision', 'exec']);
const ANDROID_VERBS = new Set(['doctor', 'devices', 'deploy', 'log', 'build']);
const INSPECT_VERBS = new Set(['serve', 'devices', 'report', 'eval']);

/**
 * A verb this group does not have — reported BEFORE the config is looked for.
 *
 * 🔴 Ordering: with `needConfig()` first, `shenora ios` typed in a directory with no config answers *"no
 * shenora.deploy.json here or in any parent directory"* — a true sentence about something the user did not
 * ask about. A typo (`shenora ios delpoy`) gets the same treatment, sending someone to fix a config file
 * that was fine.
 */
function unknownVerb(group: string, verb: string | undefined): void {
  console.error(verb
    ? `shenora: unknown ${group} command ${JSON.stringify(verb)}\n`
    : `shenora: \`shenora ${group}\` needs a command.\n`);
  console.log(USAGE);
  process.exitCode = 1;
}

/**
 * @returns a promise ONLY for the verbs that genuinely need one (`inspect`), so every existing synchronous
 * caller — and every test that asserts on `process.exitCode` right after the call — is unaffected.
 */
export function main(argv: string[]): void | Promise<void> {
  const [group, verb, ...args] = argv;

  if (group === 'init') return init();

  if (group === 'inspect') {
    if (!INSPECT_VERBS.has(verb ?? '')) return unknownVerb('inspect', verb);
    // ⚠ Never `needConfig()`: the inspector is useful with no project at all — a device that will not
    // load anything is exactly the case where nothing else is set up yet.
    const cfg = loadConfig();
    const done = (p: Promise<unknown>): Promise<void> => p.then(() => undefined);
    if (verb === 'serve') return cmdInspectServe(cfg, args);
    if (verb === 'devices') return done(cmdInspectDevices(args));
    if (verb === 'report') return done(cmdInspectReport(args));
    return done(cmdInspectEval(args, positionals(args).join(' ')));
  }

  if (group === 'copy' || group === 'sync') {
    const cfg = needConfig();
    if (!cfg) return;
    return group === 'copy' ? cmdCopy(cfg) : cmdSync(cfg);
  }

  if (group === 'android') {
    if (!ANDROID_VERBS.has(verb ?? '')) return unknownVerb('android', verb);
    // `doctor` and `devices` are machine-only — see `MACHINE_ONLY`.
    if (verb === 'doctor') return androidDoctor();
    if (verb === 'devices') return androidDevices();
    const cfg = needConfig();
    if (!cfg) return;
    if (verb === 'deploy') return androidDeploy(cfg, args);
    if (verb === 'log') return androidLog(cfg, args);
    return androidBuild(cfg, args);
  }

  if (group === 'ios') {
    if (!IOS_VERBS.has(verb ?? '')) return unknownVerb('ios', verb);
    const cfg = MACHINE_ONLY.has(verb ?? '') ? loadConfig() : needConfig();
    if (verb === 'doctor') return cmdDoctor(cfg, args);
    if (verb === 'devices') return cmdDevices(cfg, args);
    if (verb === 'simulators') return cmdSimulators(cfg, args);
    if (!cfg) return;                      // needConfig already said why
    if (verb === 'build') return cmdBuild(cfg, args);
    if (verb === 'deploy') return cmdDeploy(cfg, args);
    if (verb === 'log') return cmdLog(cfg, args);
    if (verb === 'push') return cmdPush(cfg, args);
    if (verb === 'provision') return cmdProvision(cfg, args);
    if (verb === 'exec') { cmdExec(cfg, args); return; }
    return cmdShot(cfg, args);
  }

  // An unknown GROUP is named too: bare usage with no message leaves the reader comparing their command
  // against the whole help text to find the typo.
  if (group) console.error(`shenora: unknown command ${JSON.stringify(group)}\n`);
  console.log(USAGE);
  if (group) process.exitCode = 1;   // bare `shenora` is help, not an error
}

// 🔴 Run ONLY when this file is the program. Called unconditionally at module scope, importing the routing
// above to TEST it would run whatever the test runner's own argv happened to say.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv.slice(2));
}
