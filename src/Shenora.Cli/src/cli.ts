#!/usr/bin/env node
// `shenora` — the kit's CLI, in the shape adopters already expect from a hybrid framework
// (`cap run ios`, `electron .`). Its job is the LAST MILE: taking a built app all the way onto a
// simulator or a real iPhone, with no Xcode project of the adopter's own.
import fs from 'node:fs';
import path from 'node:path';
import { loadConfig, CONFIG_FILE, SAMPLE_CONFIG, type DeployConfig } from './config.js';
import { cmdDevices, cmdDoctor, cmdBuild, cmdDeploy, cmdLog, cmdSimulators, cmdShot } from './ios.js';
import {
  cmdDoctor as androidDoctor, cmdDevices as androidDevices, cmdDeploy as androidDeploy,
  cmdLog as androidLog, cmdBuild as androidBuild,
} from './android.js';
import { cmdCopy, cmdSync } from './copy.js';

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

  shenora android doctor       can this machine build, install and log? (works on Windows too)
  shenora android devices      attached devices and emulators, including the ones adb calls unauthorized
  shenora android deploy [--device <serial>]
                               build → install → launch
  shenora android log [-n <lines>] [--device <serial>] [--all]
                               the app's own lines, filtered by tag then tailed HERE
  shenora android build [--configuration <name>] [--aab]
                               a distributable: .apk, or .aab for Play

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

// ⚠ These ask about the MACHINE, so they must not require a config file. Gating them would hide the
// answer behind the setup — and "can this Mac do it at all?" is the question someone has BEFORE they
// have a project wired.
const MACHINE_ONLY = new Set(['doctor', 'devices', 'simulators']);

function main(argv: string[]): void {
  const [group, verb, ...args] = argv;

  if (group === 'init') return init();

  if (group === 'copy' || group === 'sync') {
    const cfg = needConfig();
    if (!cfg) return;
    return group === 'copy' ? cmdCopy(cfg) : cmdSync(cfg);
  }

  if (group === 'android') {
    // `doctor` and `devices` ask about the MACHINE, so they must not be gated on a config — same rule
    // as the iOS half, for the same reason.
    if (verb === 'doctor') return androidDoctor();
    if (verb === 'devices') return androidDevices();
    const cfg = needConfig();
    if (!cfg) return;
    if (verb === 'deploy') return androidDeploy(cfg, args);
    if (verb === 'log') return androidLog(cfg, args);
    if (verb === 'build') return androidBuild(cfg, args);

    console.error(`shenora: unknown android command ${JSON.stringify(verb ?? '')}\n`);
    console.log(USAGE);
    process.exitCode = 1;
    return;
  }

  if (group === 'ios') {
    const cfg = MACHINE_ONLY.has(verb ?? '') ? loadConfig() : needConfig();
    if (verb === 'doctor') return cmdDoctor(cfg);
    if (verb === 'devices') return cmdDevices();
    if (verb === 'simulators') return cmdSimulators();
    if (!cfg) return;                      // needConfig already said why
    if (verb === 'build') return cmdBuild(cfg, args);
    if (verb === 'deploy') return cmdDeploy(cfg, args);
    if (verb === 'log') return cmdLog(cfg, args);
    if (verb === 'shot') return cmdShot(cfg, args);

    console.error(`shenora: unknown ios command ${JSON.stringify(verb ?? '')}\n`);
    console.log(USAGE);
    process.exitCode = 1;
    return;
  }

  console.log(USAGE);
  if (group) process.exitCode = 1;   // an unknown group is an error; bare `shenora` is help
}

main(process.argv.slice(2));
