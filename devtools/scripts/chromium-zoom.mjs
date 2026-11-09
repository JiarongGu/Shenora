// Does the Chromium shell's zoom guard (ChromiumZoom) hold on a real start on Windows? Builds the Chromium sample and runs its zoom
// probe (ZoomProbe: a page and a frame of another origin that report what they hear and the zoom they are at) twice, with the zoom
// guarded and then allowed (the control: the same input must change the zoom, or a guard that held would prove nothing), driven by
// zoom-windows.ps1.
//
//   node devtools/dev.mjs chromium-zoom [--posted] [dotnet build arguments…]
//
// By default the input is REAL (SendInput: Ctrl and the wheel over the page and over the frame, and Ctrl with plus, minus and 0), which
// is the desktop's input and not the window's, so the sample's window must take the foreground and keep it for about 30 s a run: start
// this from a terminal you are looking at and leave the keyboard and mouse alone, or it stops (zoom-windows.ps1 checks before every
// input, and sends nothing more once the window is not the foreground one). `--posted` sends only the wheel, posted to the page's
// render widget as WM_MOUSEWHEEL, which needs no foreground and moves no cursor, and reaches only the frame (measured). The Linux
// counterpart is tests/Shenora.Tests.Linux/LinuxZoomTests (`dev.mjs linux-tests`). Needs the native shim (`dev.mjs cef-native`). NOT in
// `verify`: it opens a window and, unless --posted, takes the input.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { repo } from './cef-cache.mjs';

if (process.platform !== 'win32') {
  console.error('chromium-zoom: this drives the Windows sample; on Linux run `node devtools/dev.mjs linux-tests --filter FullyQualifiedName~LinuxZoomTests`.');
  process.exit(2);
}

const posted = process.argv.includes('--posted');
const artifacts = path.join(repo, 'devtools', '_build', 'chromium-zoom');
const project = path.join(repo, 'samples', 'Shenora.Sample.Chromium', 'Shenora.Sample.Chromium.csproj');
const exe = path.join(artifacts, 'bin', 'Shenora.Sample.Chromium', 'debug_win-x64', 'Shenora.Sample.Chromium.exe');

const build = spawnSync('dotnet', ['build', project, '-v', 'minimal', '--artifacts-path', artifacts,
  ...process.argv.slice(2).filter((a) => a !== '--posted')], { stdio: 'inherit' });
if (build.status !== 0) { console.error('chromium-zoom: the sample did not build.'); process.exit(1); }

function run(allow) {
  const log = path.join(artifacts, `zoom-${allow ? 'allowed' : 'guarded'}.log`);
  const r = spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'devtools', 'scripts', 'zoom-windows.ps1'),
    '-Exe', exe, '-Log', log, '-Allow', allow ? '1' : '0', ...(posted ? ['-Posted'] : [])], { stdio: 'inherit' });
  return { status: r.status, lines: fs.existsSync(log) ? fs.readFileSync(log, 'utf8').split(/\r?\n/).filter(Boolean) : [], log };
}

const failures = [];
const check = (ok, what) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`); if (!ok) failures.push(what); };
const levels = (lines) => lines.map((l) => /state (main|frame) dpr=([\d.]+) /.exec(l)).filter(Boolean).map((m) => ({ where: m[1], dpr: Number(m[2]) }));

const guarded = run(false);
check(guarded.status === 0, 'the guarded run was driven to its end (a window that loses the foreground stops it)');
const wheels = guarded.lines.filter((l) => / wheel (main|frame) ctrl=1/.test(l));
check(wheels.length > 0, `the guarded run's page or frame heard Ctrl+wheels (${wheels.length})`);
check(wheels.every((l) => l.includes('prevented=1')), 'every Ctrl+wheel was cancelled before the page\'s own listener');
const first = levels(guarded.lines)[0]?.dpr;
check(levels(guarded.lines).every((l) => l.dpr === first), `the zoom stayed at ${first} in both documents`);
if (!posted) check(!guarded.lines.some((l) => /key(down|press|up) (main|frame) key=[+=\-0] code=\w+ ctrl=1/.test(l)), 'no zoom key reached the page in any of its events');

const allowed = run(true);
const seen = levels(allowed.lines);
check(seen.some((l) => l.dpr !== seen[0]?.dpr), 'with zoom allowed the same wheels zoom the page (the probe can see a zoom)');

console.log(`logs: ${guarded.log}, ${allowed.log}`);
process.exitCode = failures.length ? 1 : 0;
