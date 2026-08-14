// Android device loop for the MAUI sample — the mobile twin of `dev.mjs sample`.
//
//   android devices                 list attached devices (starts the adb server if needed)
//   android connect <host:port>     attach an emulator's adb bridge
//   android deploy [--device id]    build + install the sample APK
//   android run    [--device id]    deploy, then launch it
//   android log    [--device id] [--all] [-n N]   the app's log (SHENORA tag; --all = everything)
//   android shot   [name] [--device id]           screenshot -> devtools/_android/
//   android eval   "<js>"  [--device id]          run JS in the app's webview and print the VALUE (CDP)
//   android tap    "<sel>" [--device id]          click an element with a TRUSTED event (grants user
//                                                 activation — an injected click() does not)
//
// Everything here exists because the loop was run by hand first and each step cost something:
//
//  * The Android TFM needs a JDK. Unset JAVA_HOME makes MSBuild emit a bare `error XA5300` pointing
//    at an install page, on a machine that already has one because Android Studio ships it. dev.mjs
//    resolves it; this reuses that.
//  * `-p:RuntimeIdentifier=android-x64` is not optional for an emulator. Most are x86_64 while a
//    default build can produce arm64 only, and the install then fails INSTALL_FAILED_NO_MATCHING_ABIS
//    — which reads like a packaging fault rather than the wrong architecture.
//  * A screenshot must NOT be piped. `adb exec-out screencap -p > file.png` is corrupted by
//    PowerShell redirection (BOM + re-encoding); capture on the device and `adb pull` the bytes.
//  * No adb port is hardcoded. An emulator's bridge port comes from ITS OWN manager, so `connect`
//    takes it as an argument rather than this file guessing at a vendor default.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import config from '../project.config.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const outDir = path.join(repo, 'devtools', '_android');

/** The SDK's adb: ANDROID_HOME/ANDROID_SDK_ROOT, then the default install, then PATH. */
function adbPath() {
  const roots = [process.env.ANDROID_HOME, process.env.ANDROID_SDK_ROOT,
    process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, 'Android', 'Sdk') : null];
  for (const root of roots) {
    if (!root) continue;
    const candidate = path.join(root, 'platform-tools', 'adb.exe');
    if (fs.existsSync(candidate)) return candidate;
  }
  return 'adb'; // on PATH, or the caller gets a clear ENOENT
}

const adb = adbPath();

function run(exe, argv, opts = {}) {
  return spawnSync(exe, argv, { stdio: 'inherit', cwd: repo, ...opts }).status === 0;
}
function capture(argv) {
  const r = spawnSync(adb, argv, { encoding: 'utf8', cwd: repo });
  return (r.stdout ?? '') + (r.stderr ?? '');
}

/** Attached device ids, excluding the "List of devices" header and anything offline/unauthorized. */
function devices() {
  return capture(['devices']).split('\n').slice(1)
    .map((line) => line.trim())
    .filter((line) => line.endsWith('\tdevice'))
    .map((line) => line.split('\t')[0]);
}

/**
 * The device to act on. Explicit `--device` wins; otherwise exactly ONE attached device is required.
 * Refusing when several are attached is deliberate — silently picking the first would install to
 * whichever adb happened to list first, and the mistake is invisible until you look at the wrong screen.
 */
function target(args) {
  const flag = args.indexOf('--device');
  if (flag >= 0 && args[flag + 1]) return args[flag + 1];
  const attached = devices();
  if (attached.length === 1) return attached[0];
  if (attached.length === 0) {
    console.error('android: no device attached. Start your emulator (or plug in a phone), then:\n' +
      '  node devtools/dev.mjs android connect <host:port>   (an emulator bridge — its manager reports the port)\n' +
      '  node devtools/dev.mjs android devices');
    return null;
  }
  console.error(`android: ${attached.length} devices attached — name one with --device <id>:\n  ` +
    attached.join('\n  '));
  return null;
}

/**
 * Talk to the app's webview over CDP: evaluate an expression, or click an element with a TRUSTED event.
 *
 * ⚠ The socket is picked by the app's OWN PID rather than by taking the first one listed. A device
 * routinely has several debuggable webviews (Messages ships one here), and picking the wrong one returns
 * perfectly well-formed answers about somebody else's page — the worst kind of wrong.
 */
async function cdp(device, mode, argument) {
  const sockets = capture(['-s', device, 'shell', 'cat', '/proc/net/unix'])
    .split('\n').map((l) => l.match(/@(webview_devtools_remote_\d+)/)).filter(Boolean).map((m) => m[1]);
  const pid = capture(['-s', device, 'shell', 'pidof', config.androidPackageId]).trim().split(/\s+/)[0];
  const socket = sockets.find((s) => s.endsWith(`_${pid}`));
  if (!socket) {
    console.error(`android: no debuggable webview for ${config.androidPackageId} (pid ${pid || 'not running'}).\n`
      + '  The app must be RUNNING, and the webview is only debuggable in a Debug build.');
    return false;
  }

  // A distinct port, for the family's one-dev-port-per-tool rule — 9222 is what every other Chromium
  // tool on the machine grabs, and the collision presents as a valid answer from the wrong target.
  const port = 9421;
  spawnSync(adb, ['-s', device, 'forward', '--remove', `tcp:${port}`], { encoding: 'utf8' });
  const forwarded = spawnSync(adb, ['-s', device, 'forward', `tcp:${port}`, `localabstract:${socket}`],
    { encoding: 'utf8' });
  if (forwarded.status !== 0) {
    console.error(`android: adb forward failed — ${forwarded.stderr?.trim()}`);
    return false;
  }

  try {
    const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
    const page = targets.find((t) => t.type === 'page' && t.webSocketDebuggerUrl);
    if (!page) { console.error('android: the webview exposes no page target.'); return false; }

    const ws = new WebSocket(page.webSocketDebuggerUrl);
    let nextId = 1;
    const call = (method, params) => new Promise((resolve, reject) => {
      const id = nextId++;
      const onMessage = (evt) => {
        const m = JSON.parse(evt.data);
        if (m.id === id) { ws.removeEventListener('message', onMessage); resolve(m); }
      };
      ws.addEventListener('message', onMessage);
      ws.send(JSON.stringify({ id, method, params }));
      setTimeout(() => reject(new Error(`CDP timed out: ${method}`)), 15000);
    });
    await new Promise((resolve, reject) => {
      ws.addEventListener('open', resolve);
      ws.addEventListener('error', () => reject(new Error('CDP websocket refused')));
    });

    // awaitPromise, so an `async` expression resolves before we read it.
    const evaluate = async (expression) => {
      const r = await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
      const details = r.result?.exceptionDetails;
      if (details) throw new Error(details.exception?.description ?? JSON.stringify(details));
      return r.result?.result?.value;
    };

    if (mode === 'eval') {
      const value = await evaluate(argument);
      console.log(typeof value === 'string' ? value : JSON.stringify(value));
    } else {
      const box = await evaluate(
        `(function(){var e=document.querySelector(${JSON.stringify(argument)});if(!e)return null;`
        + 'var r=e.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()');
      if (!box) { console.error(`android: no element matches ${argument}`); ws.close(); return false; }
      for (const type of ['mousePressed', 'mouseReleased']) {
        await call('Input.dispatchMouseEvent',
          { type, x: box.x, y: box.y, button: 'left', clickCount: 1, buttons: type === 'mousePressed' ? 1 : 0 });
      }
      console.log(`android: tapped ${argument} at (${box.x.toFixed(0)}, ${box.y.toFixed(0)}) css px — a TRUSTED event`);
    }
    ws.close();
    return true;
  } catch (e) {
    console.error(`android: ${e.message ?? e}`);
    return false;
  } finally {
    spawnSync(adb, ['-s', device, 'forward', '--remove', `tcp:${port}`], { encoding: 'utf8' });
  }
}

const [sub, ...rest] = process.argv.slice(2);
const project = path.join(repo, ...config.androidSampleProject.split('/'));
const csproj = path.join(project, path.basename(project) + '.csproj');

switch (sub) {
  case 'devices':
    run(adb, ['start-server']);
    run(adb, ['devices', '-l']);
    break;

  case 'connect': {
    const endpoint = rest[0];
    if (!endpoint) {
      console.error('usage: android connect <host:port>   (your emulator manager reports its adb port)');
      process.exitCode = 1;
      break;
    }
    if (!run(adb, ['connect', endpoint])) process.exitCode = 1;
    run(adb, ['devices', '-l']);
    break;
  }

  case 'deploy':
  case 'run': {
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }

    // Reuse dev.mjs's JDK resolution rather than duplicating the probe: one owner for "where is the
    // JDK", the same reason the kit has one owner for UI marshalling.
    const jdk = spawnSync('node', [path.join(repo, 'devtools', 'dev.mjs'), 'android-jdk'],
      { encoding: 'utf8', cwd: repo });
    const javaHome = (jdk.stdout ?? '').trim();
    if (!javaHome) { process.exitCode = 1; break; }
    const env = { ...process.env, JAVA_HOME: javaHome };

    console.log(`android: deploying to ${device}…`);
    const ok = run('dotnet', ['build', csproj, '-f', config.androidTfm, '-t:Install',
      `-p:AdbTarget=-s ${device}`, `-p:RuntimeIdentifier=${config.androidRuntimeIdentifier}`,
      '-v', 'minimal'], { env });
    if (!ok) { process.exitCode = 1; break; }

    if (sub === 'run') {
      console.log('android: launching…');
      run(adb, ['-s', device, 'shell', 'monkey', '-p', config.androidPackageId,
        '-c', 'android.intent.category.LAUNCHER', '1'], { stdio: 'ignore' });
      console.log(`android: launched. Follow the log with:\n  node devtools/dev.mjs android log`);
    }
    break;
  }

  case 'log': {
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }
    const lines = rest.includes('-n') ? Number(rest[rest.indexOf('-n') + 1]) : 0;
    // One tag by default: the sample logs everything under it, so the run reads as a story instead
    // of being buried in platform chatter. --all when you need the platform's side too.
    const filter = rest.includes('--all') ? [] : ['-s', `${config.androidLogTag}:V`, 'AndroidRuntime:E'];

    if (!lines) { run(adb, ['-s', device, 'logcat', ...filter]); break; }

    // Tail in HERE, not with logcat's -t. `-t N` prints the last N lines of the RAW buffer and the
    // filterspec is applied afterwards, so `-t 60 -s SHENORA:V` reliably prints NOTHING once 60
    // lines of platform chatter have gone by — which is exactly how it behaved the first time.
    const dump = capture(['-s', device, 'logcat', '-d', ...filter]).trimEnd().split('\n');
    console.log(dump.slice(-lines).join('\n'));
    break;
  }

  case 'shot': {
    const named = rest.find((a) => !a.startsWith('-')) ?? 'android';
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }
    fs.mkdirSync(outDir, { recursive: true });
    const remote = `/sdcard/${config.shotPrefix}-${named}.png`;
    const local = path.join(outDir, `${named}.png`);
    // Capture ON the device then pull: a piped `exec-out screencap` is corrupted by the shell's
    // text redirection on Windows (BOM + re-encoding), producing a PNG nothing can open.
    if (!run(adb, ['-s', device, 'shell', 'screencap', '-p', remote], { stdio: 'ignore' })) {
      process.exitCode = 1; break;
    }
    if (!run(adb, ['-s', device, 'pull', remote, local], { stdio: 'ignore' })) { process.exitCode = 1; break; }
    run(adb, ['-s', device, 'shell', 'rm', remote], { stdio: 'ignore' });
    console.log(`android: ${path.relative(repo, local)}`);
    break;
  }

  // ---------------------------------------------------------------- bench
  //
  // Cold start, measured twice over because the two numbers answer different questions:
  //
  //   TotalTime      the PLATFORM's view — process spawn to the first frame of the activity. It is
  //                  what `am start -W` reports and what a user calls "the app opened".
  //   time-to-READY  OUR view — the app's first log line to the client's IPC handshake. This is the
  //                  one that matters for this kit, because until the handshake lands the page
  //                  cannot call anything; a shell that paints instantly and answers nothing for a
  //                  second is not fast.
  //
  // The first run after an install is always slower (the runtime is warming caches on disk), so the
  // first result is DISCARDED rather than averaged in — averaging it in makes every A/B comparison
  // depend on how recently you deployed.
  case 'bench': {
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }
    const runs = rest.includes('--runs') ? Number(rest[rest.indexOf('--runs') + 1]) : 5;

    const activity = capture(['-s', device, 'shell', 'cmd', 'package', 'resolve-activity', '--brief',
      config.androidPackageId]).trim().split('\n').pop().trim();
    if (!activity.includes('/')) {
      console.error(`android: could not resolve a launcher activity for ${config.androidPackageId} — is it installed?`);
      process.exitCode = 1;
      break;
    }

    /** logcat's "MM-DD HH:MM:SS.mmm" as milliseconds. Same day throughout a run. */
    const stamp = (line) => {
      const m = line.match(/^\d\d-\d\d (\d\d):(\d\d):(\d\d)\.(\d\d\d)/);
      if (!m) return null;
      return ((+m[1] * 60 + +m[2]) * 60 + +m[3]) * 1000 + +m[4];
    };

    const samples = [];
    for (let i = 0; i <= runs; i++) {
      spawnSync(adb, ['-s', device, 'shell', 'am', 'force-stop', config.androidPackageId]);
      spawnSync(adb, ['-s', device, 'logcat', '-c']);
      const started = capture(['-s', device, 'shell', 'am', 'start', '-W', '-n', activity]);
      const total = Number(started.match(/TotalTime:\s*(\d+)/)?.[1] ?? 0);

      // Poll for the handshake rather than sleeping a fixed amount: a fixed wait either wastes time
      // or truncates a slow run, and truncating one silently drops the very sample that matters.
      let ready = null;
      for (let waited = 0; waited < 20000 && ready === null; waited += 250) {
        spawnSync('node', ['-e', 'setTimeout(()=>{},250)']);   // no sleep binary on this path
        const log = capture(['-s', device, 'logcat', '-d', '-s', `${config.androidLogTag}:V`]).split('\n');
        const first = log.find((l) => stamp(l) !== null);
        const done = log.find((l) => l.includes('client READY'));
        if (first && done) ready = stamp(done) - stamp(first);
      }

      if (i === 0) { console.log(`  (discarding the first run after deploy: ${total} ms)`); continue; }
      samples.push({ total, ready });
      console.log(`  run ${i}: first frame ${total} ms · app start -> IPC ready ${ready ?? '(timeout)'} ms`);
    }

    // min/median/max, never a bare median. Session-to-session spread here is ~20%, so a single
    // number invites a false-precision claim like "46% faster" off two runs that happened to fall at
    // opposite ends of the noise. Show the range and the comparison stays honest.
    const spread = (xs) => {
      const ok = xs.filter((x) => typeof x === 'number').sort((a, b) => a - b);
      if (!ok.length) return '(no samples)';
      return `${ok[0]}–${ok[ok.length - 1]} ms (median ${ok[Math.floor(ok.length / 2)]})`;
    };
    console.log(`\nandroid bench (${samples.length} runs):`);
    console.log(`  first frame            ${spread(samples.map((s) => s.total))}`);
    console.log(`  app start -> IPC ready ${spread(samples.map((s) => s.ready))}`);
    console.log('\n  ⚠ An EMULATOR on a desktop is not a phone, and the spread above is real —');
    console.log('    compare MEDIANS across builds, treat anything under ~15% as noise, and do not');
    console.log('    quote these as what a user sees.');
    break;
  }

  // ---------------------------------------------------------------- eval / tap (CDP)
  //
  // 🔴 READ THE PAGE'S STATE AS TEXT, AND PRESS ITS BUTTONS FOR REAL. This is the Android peer of
  // `mac safari-eval`, and its absence cost a whole task entry: `UI-PLAY` was filed as an Android/iOS
  // shell divergence for a day because nothing here could ask the page a question or produce a gesture
  // the platform would trust. Two commands answered it in minutes.
  //
  // The webview is debuggable in a Debug build, so CDP is already there — `@webview_devtools_remote_<pid>`
  // in /proc/net/unix, forwarded to a local port. No dependency, no instrumentation in the app.
  //
  // ⚠ `tap` dispatches through `Input.dispatchMouseEvent`, which enters via the browser's own input
  // pipeline and is therefore TRUSTED: the renderer sees `isTrusted:true` and GRANTS USER ACTIVATION.
  // That is the difference that matters — an injected `element.click()` grants none, so anything gated on
  // a user gesture (autoplay being the obvious one) refuses it and looks like a fault. `adb shell input
  // tap <x> <y>` is the even more faithful arm when you want a real touch; both were used to settle this.
  case 'eval':
  case 'tap': {
    const device = target(rest);
    if (!device) { process.exitCode = 1; break; }
    const argument = rest.filter((a) => a !== '--device' && a !== device).join(' ').trim();
    if (!argument) {
      console.error(sub === 'eval'
        ? 'usage: android eval "<javascript expression>"'
        : 'usage: android tap "<css selector>"');
      process.exitCode = 1;
      break;
    }
    const ok = await cdp(device, sub, argument);
    if (!ok) process.exitCode = 1;
    break;
  }

  default:
    console.error('usage: node devtools/dev.mjs android <devices|connect <host:port>|deploy|run|log|shot [name]|eval <js>|tap <selector>|bench [--runs N]> [--device id]');
    process.exitCode = sub ? 1 : 0;
}
