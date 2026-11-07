// Does the Chromium shell's startup screen (ShenoraChromiumStartupScreen, D103) show on a real start, and go at the app's
// first window? Builds the Chromium sample with a screen stamped into its launcher, starts it, and reads its windows from
// outside (watch-windows.ps1) as they appear.
//
//   node devtools/dev.mjs chromium-startup-screen [dotnet build arguments…]
//
// Two starts of the same build:
//   1. as a person starts it: the screen is a window of the app's process, is on screen before the app's own first window,
//      has the box the build asked for (a DPI-scaled 5:3), and is gone shortly after the main window shows, which is the
//      app closing it through --startup-screen, never the timeout (the build's is 60 s);
//   2. as the update launcher starts it, with --startup-screen already on the command line: the launcher's screen is the
//      one on screen, so this process draws none.
// It opens the sample's window for a few seconds, so it needs a desktop, and the sample's layout needs the native shim
// (`dev.mjs cef-native`). NOT in `verify`, for the window. Windows only: that is where the shim draws it.
import { spawn, spawnSync } from 'node:child_process';
import path from 'node:path';
import { repo } from './cef-cache.mjs';

if (process.platform !== 'win32') {
  console.error('chromium-startup-screen: the shim draws the screen on Windows only.');
  process.exit(2);
}

const artifacts = path.join(repo, 'devtools', '_build', 'chromium-startup-screen');
const project = path.join(repo, 'samples', 'Shenora.Sample.Chromium', 'Shenora.Sample.Chromium.csproj');
const out = path.join(artifacts, 'bin', 'Shenora.Sample.Chromium', 'debug_win-x64');
const exe = path.join(out, 'Shenora.Sample.Chromium.exe');
const name = 'Shenora.Sample.Chromium';
const WIDTH = 300;
const HEIGHT = 180;

const build = spawnSync('dotnet', ['build', project, '-v', 'minimal', '--artifacts-path', artifacts,
  `-p:ShenoraChromiumStartupScreen=${path.join(repo, 'src', 'Shenora.Launcher', 'tests', 'screen.png')}`,
  `-p:ShenoraChromiumStartupScreenWidth=${WIDTH}`, `-p:ShenoraChromiumStartupScreenHeight=${HEIGHT}`,
  '-p:ShenoraChromiumStartupScreenProgressBar=true', '-p:ShenoraChromiumStartupScreenTimeoutSeconds=60',
  ...process.argv.slice(2)], { stdio: 'inherit' });
if (build.status !== 0) {
  console.error('chromium-startup-screen: the sample did not build.');
  process.exit(1);
}

/**
 * The windows of the app's processes over `seconds` while `start()` runs, as the watcher saw them: [{t, hwnd, class, visible,
 * rect}|{t, hwnd, gone}], t in ms from the moment the app was started; and `created`, when the first process was created
 * on that clock (the OS's own timestamp, so a window's `t` less it is time spent in the app, not in starting it).
 */
async function watch(seconds, start) {
  const watcher = spawn('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repo, 'devtools', 'scripts', 'watch-windows.ps1'),
    '-ProcessName', name, '-Seconds', String(seconds)], { stdio: ['ignore', 'pipe', 'inherit'] });
  const events = [];
  let created;
  let began = 0;
  let app;
  let buffer = '';
  watcher.stdout.on('data', (chunk) => {
    buffer += chunk;
    for (let i; (i = buffer.indexOf('\n')) >= 0;) {
      const line = buffer.slice(0, i).trim();
      buffer = buffer.slice(i + 1);
      if (!line) continue;
      const event = JSON.parse(line);
      if (event.process) created ??= event.started - began;
      else if (event.ready) {
        began = Date.now();
        app = start();
      } else events.push({ ...event, t: event.t - began });
    }
  });
  await new Promise((resolve) => watcher.on('exit', resolve));
  // The app and everything it started, by the tree: this build's own, never another by its name.
  if (app?.pid) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
  await new Promise((resolve) => setTimeout(resolve, 1500));
  events.created = created;
  return events;
}

const failures = [];
const check = (ok, what) => {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`);
  if (!ok) failures.push(what);
};

// 1. A person's start.
const normal = await watch(12, () => spawn(exe, [], { stdio: 'ignore' }));
const screen = normal.find((e) => e.class === 'ShenoraStartupScreen');
const main = normal.find((e) => e.class === 'Chrome_WidgetWin_1' && e.visible === 1);
const screenGone = screen && normal.find((e) => e.gone && e.hwnd === screen.hwnd);
check(!!screen, 'the startup screen is a window of the app');
if (screen) {
  const [, , w, h] = screen.rect.split(',').map(Number);
  check(normal.some((e) => e.hwnd === screen.hwnd && e.visible === 1), 'it becomes visible');
  check(Math.abs(w / h - WIDTH / HEIGHT) < 0.02, `it has the build's box, ${WIDTH}x${HEIGHT} DIPs (${w}x${h} px)`);
  console.log(`      first seen ${screen.t} ms after the start was asked for, ${screen.t - normal.created} ms after the process was created (an upper bound: the watcher polls, and a busy machine slows it)`);
}
check(!!main, "the app's main window shows");
if (screen && main) {
  check(screen.t < main.t, `the screen is up before the main window (${screen.t} ms, then ${main.t} ms)`);
  check(!!screenGone && screenGone.t >= main.t, `the screen goes once the main window is shown (${screenGone ? screenGone.t + ' ms' : 'it stayed'})`);
  check(!!screenGone && screenGone.t - main.t < 5000, 'the app closed it: it went within 5 s of the window, where the timeout is 60 s');
}

// 2. As the update launcher starts it: its screen is already up.
const handed = await watch(8, () => spawn(exe, ['--startup-screen', '1'], { stdio: 'ignore' }));
check(handed.some((e) => e.class === 'Chrome_WidgetWin_1'), 'handed over: the app still starts');
check(!handed.some((e) => e.class === 'ShenoraStartupScreen'), "handed over: the shim draws no second screen beside the launcher's");

console.log(failures.length === 0 ? 'STARTUP SCREEN: RESULT PASS' : `STARTUP SCREEN: RESULT FAIL (${failures.length})`);
process.exitCode = failures.length === 0 ? 0 : 1;
