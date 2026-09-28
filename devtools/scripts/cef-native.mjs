// Build the Chromium shell's native shim (src/Shenora.Chromium/native) against the pinned CEF build.
//
//   node devtools/dev.mjs cef-native        → src/Shenora.Chromium/artifacts/runtimes/win-x64/native/shenora_chromium_shim.dll
//
// That staging folder is what the package packs (gitignored, never committed, like the launcher's), and what
// the app-build targets fall back to for an app in this repo that references the project rather than the package.
//
// Windows only today. It needs CMake (on PATH, or the copy Visual Studio ships), the pinned CEF build
// (downloaded and SHA-1-checked by cef-cache.mjs), and the .NET SDK's static nethost for the SDK's own
// runtime version.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { distribution, fail, main, pin, project, repo, run } from './cef-cache.mjs';

function cmake() {
  // A PROBE, so not `run()`: that throws when the command cannot start, which here just means "not on PATH".
  if (spawnSync('cmake', ['--version'], { stdio: 'ignore' }).status === 0) return 'cmake';
  for (const root of [process.env['ProgramFiles'], process.env['ProgramFiles(x86)']].filter(Boolean)) {
    const vs = path.join(root, 'Microsoft Visual Studio');
    if (!fs.existsSync(vs)) continue;
    for (const year of fs.readdirSync(vs))
      for (const edition of ['Community', 'Professional', 'Enterprise', 'BuildTools']) {
        const candidate = path.join(vs, year, edition, 'Common7', 'IDE', 'CommonExtensions', 'Microsoft', 'CMake', 'CMake', 'bin', 'cmake.exe');
        if (fs.existsSync(candidate)) return candidate;
      }
  }
  fail('CMake not found on PATH or in a Visual Studio install.');
}

/** The SDK's host pack for the runtime `dotnet` itself runs on, which is the one the app will target. */
function nethostDir() {
  const version = run('dotnet', ['--list-runtimes']).stdout.split('\n')
    .map((l) => /^Microsoft\.NETCore\.App (\d+\.\d+\.\d+) /.exec(l)?.[1]).filter(Boolean).pop();
  if (!version) fail('no Microsoft.NETCore.App runtime is installed.');
  const packs = path.join(path.dirname(run('where', ['dotnet']).stdout.split(/\r?\n/)[0]), 'packs', 'Microsoft.NETCore.App.Host.win-x64', version);
  const dir = path.join(packs, 'runtimes', 'win-x64', 'native');
  if (!fs.existsSync(path.join(dir, 'libnethost.lib'))) fail(`the .NET SDK has no static nethost for ${version} at ${dir}.`);
  return dir;
}

await main('cef-native', async () => {
  if (process.platform !== 'win32') fail('the shim is Windows-only today.');
  const cef = path.join(await distribution(), 'cef');
  const build = path.join(repo, 'devtools', '_build', 'cef-native');
  const tool = cmake();
  const configure = run(tool, ['-S', path.join(project, 'native'), '-B', build,
    `-DCEF_ROOT=${cef}`, `-DNETHOST_DIR=${nethostDir()}`, `-DCEF_API_VERSION=${pin.apiVersion}`]);
  if (configure.status !== 0) fail(`configure failed:\n${configure.stdout}${configure.stderr}`);
  const compile = run(tool, ['--build', build, '--config', 'Release']);
  if (compile.status !== 0) fail(`build failed:\n${compile.stdout}${compile.stderr}`);
  const dll = path.join(build, 'Release', 'shenora_chromium_shim.dll');
  if (!fs.existsSync(dll)) fail(`the build reported success but ${dll} is missing.`);
  const staged = path.join(project, 'artifacts', 'runtimes', 'win-x64', 'native');
  fs.mkdirSync(staged, { recursive: true });
  fs.copyFileSync(dll, path.join(staged, path.basename(dll)));
  console.log(`cef-native: ${path.relative(repo, path.join(staged, path.basename(dll)))} (${(fs.statSync(dll).size / 1024).toFixed(0)} KB, CEF API ${pin.apiVersion})`);
});
