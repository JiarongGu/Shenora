#!/usr/bin/env node
// `shenora` — the kit's CLI, in the shape adopters already expect from a hybrid framework
// (`cap run ios`, `electron .`). Its job is the LAST MILE: taking a built app all the way onto a
// simulator or a real iPhone, with no Xcode project of the adopter's own.
import fs from 'node:fs';
import path from 'node:path';
import { loadConfig, CONFIG_FILE, SAMPLE_CONFIG, type DeployConfig } from './config.js';
import { cmdDevices, cmdDoctor, cmdDeploy, cmdLog, cmdSimulators, cmdShot } from './ios.js';
import { cmdCopy, cmdSync } from './copy.js';

const USAGE = `shenora — take a built app onto a simulator or a real iPhone

  shenora init                 write a starter ${CONFIG_FILE}
  shenora copy                 stage the built web bundle into the app head
  shenora sync                 copy, then restore the app head's dependencies

  shenora ios doctor           can this Mac build, sign and install?
  shenora ios devices          connected iPhones
  shenora ios simulators       installed simulators
  shenora ios deploy --simulator ["iPhone 16 Pro"]
                               build → boot → install → launch (no signing needed)
  shenora ios deploy [--device <name|id>]
                               build → SIGN → verify extensions → install → launch
  shenora ios log [-n <lines>] the app's own output
  shenora ios shot [-o <file>] screenshot the booted simulator

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

  if (group === 'ios') {
    const cfg = MACHINE_ONLY.has(verb ?? '') ? loadConfig() : needConfig();
    if (verb === 'doctor') return cmdDoctor(cfg);
    if (verb === 'devices') return cmdDevices();
    if (verb === 'simulators') return cmdSimulators();
    if (!cfg) return;                      // needConfig already said why
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
