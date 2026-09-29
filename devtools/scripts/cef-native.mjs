// Build the Chromium shell's native half (src/Shenora.Chromium/native) for one RID.
//
//   node devtools/dev.mjs cef-native                  → src/Shenora.Chromium/artifacts/runtimes/win-x64/native/shenora_chromium_shim.dll
//   node devtools/dev.mjs cef-native --rid win-arm64  → …/runtimes/win-arm64/native/…, cross-compiled from x64
//   node devtools/dev.mjs cef-native --rid osx-x64    → …/runtimes/osx-x64/native/shenora_chromium_helper, on a Mac
//
// That staging folder is what the package packs (gitignored, never committed, like the launcher's), and what
// the app-build targets fall back to for an app in this repo that references the project rather than the package.
//
// Windows: the shim CEF's bootstrap.exe loads. It needs CMake (on PATH, or the copy Visual Studio ships), MSVC for
// the target (win-arm64 needs Visual Studio's "C++ ARM64 build tools" component), the pinned CEF build for the RID
// (downloaded and SHA-1-checked by cef-cache.mjs), and the .NET SDK's static nethost for the RID.
// macOS: the helper every CEF subprocess runs, built with clang against no headers, on a Mac.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { distribution, fail, main, pin, project, repo, run } from './cef-cache.mjs';

/**
 * The RIDs this builds for: CEF's platform name, the compiler's architecture, and the machine the binary must
 * turn out to be — a PE Machine on Windows, a Mach-O CPU type on macOS.
 */
const targets = {
  'win-x64': { os: 'win32', platform: 'windows64', arch: 'x64', machine: 0x8664 },
  'win-arm64': { os: 'win32', platform: 'windowsarm64', arch: 'ARM64', machine: 0xaa64 },
  'osx-x64': { os: 'darwin', platform: 'macosx64', arch: 'x86_64', machine: 0x01000007 },
  'osx-arm64': { os: 'darwin', platform: 'macosarm64', arch: 'arm64', machine: 0x0100000c },
};

/** What the binary was really compiled for, whatever the build said: a PE's Machine, or a Mach-O's CPU type. */
function machineOf(binary) {
  const bytes = fs.readFileSync(binary);
  if (bytes.readUInt32LE(0) === 0xfeedfacf) return bytes.readUInt32LE(4);
  return bytes.readUInt16LE(bytes.readUInt32LE(0x3c) + 4);
}

/** The macOS helper: one C file, no CEF headers, the oldest macOS CEF supports. */
function buildHelper(rid, arch) {
  const build = path.join(repo, 'devtools', '_build', 'cef-native', rid);
  fs.mkdirSync(build, { recursive: true });
  const binary = path.join(build, 'shenora_chromium_helper');
  const compile = run('clang', ['-O2', '-Wall', '-Wextra', '-Werror', '-arch', arch, '-mmacosx-version-min=12.0',
    `-DCEF_API_VERSION=${pin.apiVersion}`, '-o', binary, path.join(project, 'native', 'helper_mac.c')]);
  if (compile.status !== 0) fail(`build failed:\n${compile.stdout}${compile.stderr}`);
  return binary;
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

/** The Windows shim, through CMake and MSVC. */
async function buildShim(rid, platform, arch) {
  const cef = path.join(await distribution(platform), 'cef');
  const build = path.join(repo, 'devtools', '_build', 'cef-native', rid);
  const tool = cmake();
  // -A picks the Visual Studio generator's target: an x64 host cross-compiles ARM64 with the ARM64 tools.
  const configure = run(tool, ['-S', path.join(project, 'native'), '-B', build, '-A', arch,
    `-DCEF_ROOT=${cef}`, `-DNETHOST_DIR=${nethostDir(rid)}`, `-DCEF_API_VERSION=${pin.apiVersion}`]);
  if (configure.status !== 0) fail(`configure failed:\n${configure.stdout}${configure.stderr}`);
  const compile = run(tool, ['--build', build, '--config', 'Release']);
  if (compile.status !== 0) fail(`build failed:\n${compile.stdout}${compile.stderr}`);
  return path.join(build, 'Release', 'shenora_chromium_shim.dll');
}

await main('cef-native', async () => {
  const rid = ridArgument();
  const { os, platform, arch, machine } = targets[rid];
  if (process.platform !== os) fail(`${rid} builds on ${os === 'darwin' ? 'a Mac' : 'Windows'}; this is ${process.platform}.`);
  const binary = os === 'darwin' ? buildHelper(rid, arch) : await buildShim(rid, platform, arch);
  if (!fs.existsSync(binary)) fail(`the build reported success but ${binary} is missing.`);
  // A stale build directory configured for another architecture builds that one and succeeds.
  if (machineOf(binary) !== machine)
    fail(`${binary} is machine 0x${machineOf(binary).toString(16)}, not ${rid}'s 0x${machine.toString(16)}; delete its build folder and build again.`);
  const staged = path.join(project, 'artifacts', 'runtimes', rid, 'native');
  fs.mkdirSync(staged, { recursive: true });
  fs.copyFileSync(binary, path.join(staged, path.basename(binary)));
  console.log(`cef-native: ${path.relative(repo, path.join(staged, path.basename(binary)))} (${(fs.statSync(binary).size / 1024).toFixed(0)} KB, ${rid}, CEF API ${pin.apiVersion})`);
});
