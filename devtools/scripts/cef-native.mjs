// Build the Chromium shell's native shim (src/Shenora.Chromium/native) against the pinned CEF build.
//
//   node devtools/dev.mjs cef-native                  → src/Shenora.Chromium/artifacts/runtimes/win-x64/native/shenora_chromium_shim.dll
//   node devtools/dev.mjs cef-native --rid win-arm64  → …/runtimes/win-arm64/native/…, cross-compiled from x64
//
// That staging folder is what the package packs (gitignored, never committed, like the launcher's), and what
// the app-build targets fall back to for an app in this repo that references the project rather than the package.
//
// Windows only today. It needs CMake (on PATH, or the copy Visual Studio ships), MSVC for the target (win-arm64
// needs Visual Studio's "C++ ARM64 build tools" component), the pinned CEF build for the RID (downloaded and
// SHA-1-checked by cef-cache.mjs), and the .NET SDK's static nethost for the RID and the SDK's own runtime version.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { distribution, fail, main, pin, project, repo, run } from './cef-cache.mjs';

/** The RIDs the shim builds for: CEF's platform name, and the Visual Studio generator's architecture. */
const targets = {
  'win-x64': { platform: 'windows64', arch: 'x64', machine: 0x8664 },
  'win-arm64': { platform: 'windowsarm64', arch: 'ARM64', machine: 0xaa64 },
};

/** The PE header's Machine field: what the DLL was really compiled for, whatever the build said. */
function machineOf(dll) {
  const bytes = fs.readFileSync(dll);
  return bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4);
}

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

/**
 * The static nethost for `rid`, at the version of the runtime `dotnet` itself runs on (the one the app will
 * target). The SDK ships host packs for the Windows RIDs; one it lacks is restored into the NuGet cache.
 */
function nethostDir(rid) {
  const version = run('dotnet', ['--list-runtimes']).stdout.split('\n')
    .map((l) => /^Microsoft\.NETCore\.App (\d+\.\d+\.\d+) /.exec(l)?.[1]).filter(Boolean).pop();
  if (!version) fail('no Microsoft.NETCore.App runtime is installed.');
  const inPacks = path.join(path.dirname(run('where', ['dotnet']).stdout.split(/\r?\n/)[0]), 'packs',
    `Microsoft.NETCore.App.Host.${rid}`, version, 'runtimes', rid, 'native');
  if (fs.existsSync(path.join(inPacks, 'libnethost.lib'))) return inPacks;

  const probe = path.join(repo, 'devtools', '_build', `nethost-${rid}`);
  fs.mkdirSync(probe, { recursive: true });
  fs.writeFileSync(path.join(probe, 'probe.csproj'), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net${version.split('.').slice(0, 2).join('.')}</TargetFramework>
    <RuntimeIdentifier>${rid}</RuntimeIdentifier>
    <RuntimeFrameworkVersion>${version}</RuntimeFrameworkVersion>
  </PropertyGroup>
</Project>
`);
  const restore = run('dotnet', ['restore', path.join(probe, 'probe.csproj')]);
  if (restore.status !== 0) fail(`restoring the ${rid} host pack failed:\n${restore.stdout}${restore.stderr}`);
  const packages = /global-packages:\s*(.+)/.exec(run('dotnet', ['nuget', 'locals', 'global-packages', '--list']).stdout)?.[1]?.trim();
  const inCache = packages && path.join(packages, `microsoft.netcore.app.host.${rid}`, version, 'runtimes', rid, 'native');
  if (!inCache || !fs.existsSync(path.join(inCache, 'libnethost.lib')))
    fail(`the .NET SDK has no static nethost for ${rid} ${version}, in its packs or the NuGet cache.`);
  return inCache;
}

function ridArgument() {
  const at = process.argv.indexOf('--rid');
  const rid = at < 0 ? 'win-x64' : process.argv[at + 1];
  if (!targets[rid]) fail(`--rid must be one of ${Object.keys(targets).join(', ')}; got ${rid ?? 'nothing'}.`);
  return rid;
}

await main('cef-native', async () => {
  if (process.platform !== 'win32') fail('the shim is Windows-only today.');
  const rid = ridArgument();
  const { platform, arch, machine } = targets[rid];
  const cef = path.join(await distribution(platform), 'cef');
  const build = path.join(repo, 'devtools', '_build', 'cef-native', rid);
  const tool = cmake();
  // -A picks the Visual Studio generator's target: an x64 host cross-compiles ARM64 with the ARM64 tools.
  const configure = run(tool, ['-S', path.join(project, 'native'), '-B', build, '-A', arch,
    `-DCEF_ROOT=${cef}`, `-DNETHOST_DIR=${nethostDir(rid)}`, `-DCEF_API_VERSION=${pin.apiVersion}`]);
  if (configure.status !== 0) fail(`configure failed:\n${configure.stdout}${configure.stderr}`);
  const compile = run(tool, ['--build', build, '--config', 'Release']);
  if (compile.status !== 0) fail(`build failed:\n${compile.stdout}${compile.stderr}`);
  const dll = path.join(build, 'Release', 'shenora_chromium_shim.dll');
  if (!fs.existsSync(dll)) fail(`the build reported success but ${dll} is missing.`);
  // A stale build directory configured for another architecture builds that one and succeeds.
  if (machineOf(dll) !== machine)
    fail(`${dll} is machine 0x${machineOf(dll).toString(16)}, not ${rid}'s 0x${machine.toString(16)}; delete ${build} and build again.`);
  const staged = path.join(project, 'artifacts', 'runtimes', rid, 'native');
  fs.mkdirSync(staged, { recursive: true });
  fs.copyFileSync(dll, path.join(staged, path.basename(dll)));
  console.log(`cef-native: ${path.relative(repo, path.join(staged, path.basename(dll)))} (${(fs.statSync(dll).size / 1024).toFixed(0)} KB, ${rid}, CEF API ${pin.apiVersion})`);
});
