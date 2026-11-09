// Does the engine's Chromium decode H.264 + AAC and HEVC? Builds the Chromium sample with the engine package an app would
// reference, runs it with SHENORA_SAMPLE_CODEC_PROBE=1 (samples/Shenora.Sample.Chromium/CodecProbe.cs), and fails unless
// the probe's last line is `CODEC: RESULT PASS`.
//
//   node devtools/dev.mjs chromium-codecs [--standard | --dist <folder> | --source <feed>] [dotnet build arguments…]
//
// No option: the sample references Shenora.Chromium.Codecs.win-x64 (D99, engines/), restored from nuget.org.
//   --dist <folder>   a built CEF distribution (its Release\ and Resources\): packed into a scratch feed first and
//                     restored from there, which is how a build is tried before the package is published.
//   --source <feed>   a folder or URL to restore the package from as well.
//   --standard        CEF's own build under it instead, which fails by design and is how the probe is seen red.
//   --later <folder>  an engine that arrives AFTER the app is laid out, as an installer or a launcher would bring it: the
//                     sample is built on CEF's standard engine, copied aside, and the folder's Release\ and Resources\ (a
//                     built distribution, or the package's tools\engine) are merged into its engine\ before it starts.
//                     The shim loads CEF from engine\ when it holds a libcef.dll, so nothing in the app changes.
// Any other argument is handed to `dotnet build`. It opens the sample's window for a few seconds, so it needs a desktop,
// and the sample's layout needs the native shim (`dev.mjs cef-native`). NOT in `verify`, for the window.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { repo } from './cef-cache.mjs';

if (process.platform !== 'win32') {
  console.error('chromium-codecs: the probe has only been run on Windows.');
  process.exit(2);
}

/** `--name value` out of the arguments, removed from them. */
function take(args, name) {
  const at = args.indexOf(name);
  if (at < 0) return undefined;
  const [, value] = args.splice(at, 2);
  if (!value) throw new Error(`${name} needs a value.`);
  return value;
}

/** Every file under `from` copied into `into`, folders made; a manual loop, because fs.cpSync crashes Node 24 here. */
function copyTree(from, into) {
  fs.mkdirSync(into, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    const source = path.join(from, entry.name);
    if (entry.isDirectory()) copyTree(source, path.join(into, entry.name));
    else fs.copyFileSync(source, path.join(into, entry.name));
  }
}

const args = process.argv.slice(2);
const later = take(args, '--later');
const standard = args.includes('--standard') || later !== undefined;
const dist = take(args, '--dist');
let source = take(args, '--source');
const rest = args.filter((a) => a !== '--standard');

// Each engine has an artifacts folder of its own: one build output holds one layout, and a stale one would be run.
const artifacts = path.join(repo, 'devtools', '_build', standard ? 'chromium-codecs-standard' : 'chromium-codecs');
const project = path.join(repo, 'samples', 'Shenora.Sample.Chromium', 'Shenora.Sample.Chromium.csproj');

if (dist) {
  const feed = path.join(repo, 'devtools', '_build', 'engine-feed');
  fs.rmSync(feed, { recursive: true, force: true });
  const pack = spawnSync('dotnet', ['pack', path.join(repo, 'engines', 'Shenora.Chromium.Codecs.win-x64', 'Shenora.Chromium.Codecs.win-x64.csproj'),
    '-c', 'Release', `-p:CefDist=${path.resolve(dist)}`, '-o', feed, '--artifacts-path', path.join(repo, 'devtools', '_build', 'engine-pack'), '-v', 'minimal'], { stdio: 'inherit' });
  if (pack.status !== 0) {
    console.error('chromium-codecs: the engine package did not pack.');
    process.exit(1);
  }
  // A package of the same id and version is restored once per machine, so the one a rebuild replaces is dropped first.
  fs.rmSync(path.join(process.env.NUGET_PACKAGES ?? path.join(os.homedir(), '.nuget', 'packages'), 'shenora.chromium.codecs.win-x64'), { recursive: true, force: true });
  source = feed;
}

const build = spawnSync('dotnet', ['build', project, '-v', 'minimal', '--artifacts-path', artifacts,
  ...(standard ? [] : ['-p:ShenoraSampleCodecs=true']), ...(source ? [`-p:RestoreAdditionalProjectSources=${source}`] : []), ...rest], { stdio: 'inherit' });
if (build.status !== 0) {
  console.error('chromium-codecs: the sample did not build.');
  process.exitCode = 1;
} else {
  let out = path.join(artifacts, 'bin', 'Shenora.Sample.Chromium', 'debug_win-x64');
  if (later) {
    // What an installer does before the app starts: the app is laid out on CEF's standard engine, and the engine arrives in
    // engine\ beside it. A copy, so the build output stays the standard one; the shim picks engine\ when it holds libcef.dll.
    const merged = path.join(repo, 'devtools', '_build', 'chromium-codecs-later');
    fs.rmSync(merged, { recursive: true, force: true });
    copyTree(out, merged);
    for (const folder of ['Release', 'Resources']) {
      if (!fs.existsSync(path.join(later, folder))) {
        console.error(`chromium-codecs: ${later} has no ${folder}\\ folder, so it is not an engine.`);
        process.exit(1);
      }
      copyTree(path.join(later, folder), path.join(merged, 'engine'));
    }
    out = merged;
  }
  const log = path.join(out, 'codec-probe.log');
  fs.rmSync(log, { force: true });
  const run = spawnSync(path.join(out, 'Shenora.Sample.Chromium.exe'), [],
    { stdio: 'inherit', timeout: 90_000, env: { ...process.env, SHENORA_SAMPLE_CODEC_PROBE: '1', SHENORA_SAMPLE_CODEC_LOG: log } });
  // A missing log is a probe that never ran, which is a FAIL of its own and never a pass.
  const lines = fs.existsSync(log) ? fs.readFileSync(log, 'utf8').trim().split(/\r?\n/) : [];
  for (const line of lines) console.log(line);
  const result = lines.find((l) => l.includes('CODEC: RESULT '));
  if (!result) console.error(`chromium-codecs: the probe wrote no result (the sample exited ${run.status ?? run.signal}).`);
  process.exitCode = result?.includes('CODEC: RESULT PASS') ? 0 : 1;
}
