// Does the engine's Chromium decode H.264 + AAC and HEVC? Builds the Chromium sample with its layout, runs it with
// SHENORA_SAMPLE_CODEC_PROBE=1 (samples/Shenora.Sample.Chromium/CodecProbe.cs), and fails unless the probe's last line
// is `CODEC: RESULT PASS`.
//
//   node devtools/dev.mjs chromium-codecs [--standard] [dotnet build arguments…]
//
// The engine under test is the kit's codecs build (ShenoraChromiumCodecs=true, D99); `--standard` puts CEF's own build
// under it instead, which fails by design and is how the probe is seen red. `-p:ShenoraCefDist=<folder>` puts an
// extracted build of one's own under it, as a build is tried before it is pinned. Any other argument is handed to
// `dotnet build`. It opens the sample's window for a few seconds, so it needs a desktop, and the sample's layout needs
// the native shim (`dev.mjs cef-native`) and fetches the build once. NOT in `verify`, for the window.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { repo } from './cef-cache.mjs';

if (process.platform !== 'win32') {
  console.error('chromium-codecs: the probe drives a web panel, which is Windows only today.');
  process.exit(2);
}

const args = process.argv.slice(2);
const standard = args.includes('--standard');
// Each engine has an artifacts folder of its own: one build output holds one layout, and a stale one would be run.
const artifacts = path.join(repo, 'devtools', '_build', standard ? 'chromium-codecs-standard' : 'chromium-codecs');
const project = path.join(repo, 'samples', 'Shenora.Sample.Chromium', 'Shenora.Sample.Chromium.csproj');
const build = spawnSync('dotnet', ['build', project, '-v', 'minimal', '--artifacts-path', artifacts,
  ...(standard ? [] : ['-p:ShenoraChromiumCodecs=true']), ...args.filter((a) => a !== '--standard')], { stdio: 'inherit' });
if (build.status !== 0) {
  console.error('chromium-codecs: the sample did not build.');
  process.exitCode = 1;
} else {
  const out = path.join(artifacts, 'bin', 'Shenora.Sample.Chromium', 'debug_win-x64');
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
