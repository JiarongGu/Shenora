// The CLI's testable core: the decisions that FAIL SILENTLY when wrong.
//
// Everything here is a claim whose failure mode is a WRONG ANSWER rather than a crash — a rejected
// install reported as success, a simulator booted by the wrong name, a config read as empty. Those are
// the ones a human never notices.
//
// ⚠ This header used to say the suite "deliberately does not test process spawning". That is no longer
// true and the exception earns itself: `describeSpawnFailure`'s two spawning tests use a binary that
// does not exist and a `node -e` that sleeps, so they are fast, need no mock, and run anywhere. They are
// also the only ones that fail when the diagnostic is left UNWIRED — measured, the four pure-function
// tests beside them all passed while `run` ignored it completely.
import { describe, it, expect, afterEach, vi } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { withPipefail, argValue, describeSpawnFailure, run, splitArgs, shellPassthrough } from './exec.js';
import {
  simulatorLogPredicate, describeConnection, findArtifact, describeLogOutcome, parseDeviceList,
  isAlreadyBooted, describeDeviceSigning, pickBindingBand, describeBindings, describeAotCrossPack,
} from './ios.js';
import { parseDevices, findPackage, adbCandidates, resolveJdk } from './android.js';
import {
  loadConfig, projectDir, requireFields, platformTfm, iosTfmOf, CONFIG_FILE, SAMPLE_CONFIG,
  type DeployConfig,
} from './config.js';
import { cmdCopy, lastLines } from './copy.js';
import { main } from './cli.js';
import { LocalTarget } from './remote/target.js';

const temps: string[] = [];
const tempDir = (): string => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'shenora-cli-test-'));
  temps.push(dir);
  return dir;
};
afterEach(() => {
  while (temps.length) fs.rmSync(temps.pop()!, { recursive: true, force: true });
});

describe('withPipefail — the README\'s headline guarantee', () => {
  it('prepends pipefail to a pipeline', () => {
    // Without this, `| tail` decides the exit status and it is ALWAYS 0 — a rejected install
    // reports success and the tool announces an app that was never installed is running.
    expect(withPipefail('xcrun simctl install booted x.app | tail -10')).toBe(
      'set -o pipefail\nxcrun simctl install booted x.app | tail -10',
    );
  });

  it('leaves a pipe-less command completely alone', () => {
    // The QUIET direction: `set -o pipefail` is not POSIX-sh portable, so adding it unconditionally
    // would risk breaking every simple command to protect the piped ones.
    expect(withPipefail('xcrun simctl boot booted')).toBe('xcrun simctl boot booted');
  });

  it('covers a pipe anywhere in the command, not just at the end', () => {
    expect(withPipefail('a | b > c')).toMatch(/^set -o pipefail\n/);
  });
});

describe('describeSpawnFailure — the two ways a command never runs at all', () => {
  // 🔴 spawnSync reports BOTH of these through `error`, leaving stdout/stderr empty and status null.
  // Neither was read, so both arrived as a bare non-zero exit with no output — and the caller's own
  // guess about what a non-zero exit meant became the only thing the user saw.

  it('names a missing tool instead of letting the caller guess', () => {
    const why = describeSpawnFailure('dotnet', Object.assign(new Error('spawnSync ENOENT'), { code: 'ENOENT' }), 1000);

    expect(why).toContain('dotnet');
    expect(why).toContain('PATH');
  });

  it('says a TIMEOUT was a timeout — the case the timeout was added for', () => {
    // `run` gained a timeout because adb hangs on an offline/unauthorized device, its comment noting
    // the user "cannot tell it apart from a slow build". The firing of that timeout was itself silent,
    // so the CLI stopped after 30 minutes saying nothing — most of the value handed straight back.
    const why = describeSpawnFailure('adb', Object.assign(new Error('spawnSync ETIMEDOUT'), { code: 'ETIMEDOUT' }), 90_000);

    expect(why).toContain('adb');
    expect(why).toContain('90s');
  });

  it('does NOT rely on a signal that is never there', () => {
    // Measured: on a timeout Node puts `signal: 'SIGTERM'` on the spawnSync RESULT and the error carries
    // only errno/code/syscall/path/spawnargs. A first version of this helper also tested
    // `error.signal === 'SIGTERM'` as a supposed fallback — dead on arrival, and worse, it read as a
    // covered case. Pinned so nobody adds it back on the same intuition.
    const withSignalButNoCode = Object.assign(new Error('killed'), { signal: 'SIGTERM' });
    expect(describeSpawnFailure('adb', withSignalButNoCode, 1000)).not.toContain('did not finish');
  });

  it('reports an unrecognised failure verbatim rather than swallowing it', () => {
    const why = describeSpawnFailure('adb', Object.assign(new Error('EACCES permission denied'), { code: 'EACCES' }), 1000);

    expect(why).toContain('EACCES permission denied');
  });

  it('stays SILENT when the process actually ran', () => {
    // The quiet direction, and the one that matters most: a command that ran and exited non-zero has
    // real output, and inventing a line here would bury it under a wrong diagnosis.
    expect(describeSpawnFailure('adb', undefined, 1000)).toBe('');
  });

  // 🔴 THE WIRING, not just the rule. The four tests above passed with `run` ignoring the helper
  // entirely — measured by sabotage — because they only exercise the pure function. These two spawn for
  // real, which is cheap and needs no mock, and they are the ones that fail if the call site is removed.

  it('run() actually REPORTS a missing binary', () => {
    const r = run('shenora-no-such-binary-xyz', ['--version'], { quiet: true });

    expect(r.status).not.toBe(0);
    expect(r.out).toContain('was not found on PATH');
  });

  it('run() actually REPORTS a timeout as a timeout', () => {
    // node is guaranteed present — we are running in it.
    const r = run(process.execPath, ['-e', 'setTimeout(() => {}, 60000)'], { quiet: true, timeoutMs: 400 });

    expect(r.status).not.toBe(0);
    expect(r.out).toContain('did not finish within');
  });
});

describe('splitArgs — the `--` passthrough', () => {
  it('routes everything after `--` to the build, and nothing before it', () => {
    const { own, passthrough } = splitArgs(['--simulator', 'iPhone 16 Pro', '--', '-p:Foo=1', '-p:Bar=2']);
    expect(own).toEqual(['--simulator', 'iPhone 16 Pro']);
    expect(passthrough).toEqual(['-p:Foo=1', '-p:Bar=2']);
  });

  it('🔴 does not let a build property be read as a simulator NAME', () => {
    // The trap this function exists for. `argValue` takes the token after a flag, so on a single flat
    // array `deploy --simulator -- -p:Foo=1` boots a simulator called "-p:Foo=1" and then reports that
    // no such device exists — a confusing failure a long way from its cause.
    const { own, passthrough } = splitArgs(['--simulator', '--', '-p:ValidateXcodeVersion=false']);
    expect(argValue(own, '--simulator')).toBeUndefined();
    expect(passthrough).toEqual(['-p:ValidateXcodeVersion=false']);
  });

  it('is a no-op without a separator', () => {
    const { own, passthrough } = splitArgs(['--device', 'my-phone']);
    expect(own).toEqual(['--device', 'my-phone']);
    expect(passthrough).toEqual([]);
  });

  it('treats a trailing `--` with nothing after it as no extra args', () => {
    // Otherwise the build command gains a stray trailing space and, worse, the fragment reads as truthy
    // — which would print an "extra build args:" line naming nothing.
    expect(splitArgs(['--simulator', '--']).passthrough).toEqual([]);
    expect(shellPassthrough(splitArgs(['--simulator', '--']).passthrough)).toBe('');
  });

  it('🔴 keeps an argument containing a SPACE in one piece', () => {
    // Joining the passthrough into one string threw the user's own argument boundaries away: the shell
    // re-split `-p:Foo=a b` into two arguments, and any path with a space in it — the normal case on
    // Windows and macOS both — arrived at dotnet mangled. Quoting each one separately is what survives.
    const { passthrough } = splitArgs(['--', '-p:Title=Hello World', '-p:N=1']);

    expect(passthrough).toEqual(['-p:Title=Hello World', '-p:N=1']);
    // One shell word per argument: the space is inside the quotes, not a separator.
    expect(shellPassthrough(passthrough)).toBe(` '-p:Title=Hello World' '-p:N=1'`);
  });
});

describe('simulatorLogPredicate — the reader that was silent', () => {
  it('🔴 matches case-INSENSITIVELY, because a bundle id and a binary name disagree on case', () => {
    // The whole defect in one assertion. NSPredicate's plain CONTAINS is case-sensitive, so this
    // predicate searched `…/Shenora.Sample.Maui.app/Shenora.Sample.Maui` for "maui" and matched
    // nothing — 1 line of output, which was the header. Measured against 20,352 lines with `[c]`.
    expect(simulatorLogPredicate('com.shenora.sample.maui')).toContain('CONTAINS[c]');
  });

  it('searches for the bundle id\'s last segment', () => {
    expect(simulatorLogPredicate('com.example.myapp')).toBe('processImagePath CONTAINS[c] "myapp"');
  });

  it('falls back to the whole id when there is nothing to split', () => {
    // A single-segment id is unusual but not invalid, and `split('.').pop()` on it must not yield ''.
    expect(simulatorLogPredicate('myapp')).toBe('processImagePath CONTAINS[c] "myapp"');
  });
});

describe('isAlreadyBooted — the reason `|| true` was there, without what it swallowed', () => {
  // 🔴 `simctl boot` exits non-zero for an ALREADY-booted simulator, so the old code wrote
  // `boot … || true`. That kept the idempotent case working and swallowed a MISTYPED NAME with it: the
  // run carried on to `install booted` and landed on whatever else was running. You then debug the wrong
  // build on a device you did not choose — the exact thing `resolveTarget` and the Android
  // `resolveDevice` both refuse to do, in this same CLI.

  it('recognises the already-booted state as the success it is', () => {
    expect(isAlreadyBooted('Unable to boot device in current state: Booted')).toBe(true);
  });

  it('🔴 does NOT recognise a bad device name — that has to fail loudly', () => {
    expect(isAlreadyBooted('Invalid device: iPhone 16 Pr')).toBe(false);
    expect(isAlreadyBooted('Unable to lookup device: no such device')).toBe(false);
  });

  it('treats an UNRECOGNISED message as a real failure', () => {
    // The safe direction if Apple rewords this: an unknown message costs a redundant error on a booted
    // device, where the alternative costs a silent install onto the wrong one.
    expect(isAlreadyBooted('')).toBe(false);
    expect(isAlreadyBooted('some future wording nobody predicted')).toBe(false);
  });
});

describe('lastLines — the trimming that replaced a shell pipe', () => {
  // 🔴 `shenora sync` was UNUSABLE ON WINDOWS: it shelled out to `/bin/sh` solely to reach `| tail -20`,
  // so it failed before `dotnet` was ever reached — and then said "see the output above", above nothing.
  // Windows is not an edge case here; the Android half of this CLI exists because most .NET Android work
  // happens there.

  it('keeps the LAST n lines — where a failed restore puts its error', () => {
    const text = Array.from({ length: 50 }, (_, i) => `line ${i + 1}`).join('\n');
    expect(lastLines(text, 3)).toBe('line 48\nline 49\nline 50');
  });

  it('returns everything when there is less than n', () => {
    expect(lastLines('only\ntwo', 20)).toBe('only\ntwo');
  });

  it('handles CRLF, because this now runs on Windows by design', () => {
    expect(lastLines('a\r\nb\r\nc', 2)).toBe('b\nc');
  });

  it('does not turn empty output into a blank line', () => {
    // The caller prints only when this is non-empty; returning "\n" would add a stray blank line to
    // every successful restore.
    expect(lastLines('', 20)).toBe('');
    expect(lastLines('\n\n', 20)).toBe('');
  });
});

describe('parseDeviceList — "no phone attached" vs "could not ask"', () => {
  // 🔴 Every failure used to collapse to an empty array, so the callers said "no iPhone is connected.
  // Plug it in, unlock it, tap Trust." — a confident claim about the user's HARDWARE made when the
  // truth is that the tool failed. ios.ts's own doc already calls that "the single worst answer this
  // tool can give"; it had fixed ONE cause (the stdout pipe) and left the rest.

  const listing = (...names: string[]) => JSON.stringify({
    result: {
      devices: names.map((name, i) => ({
        identifier: `id-${i}`,
        deviceProperties: { name, osVersionNumber: '18.0' },
        connectionProperties: { pairingState: 'paired', transportType: 'localNetwork' },
      })),
    },
  });

  it('reads the devices when devicectl answered', () => {
    const lookup = parseDeviceList(listing('Test iPhone'));

    expect(lookup.ok).toBe(true);
    if (lookup.ok) {
      expect(lookup.devices).toHaveLength(1);
      expect(lookup.devices[0]).toMatchObject({ name: 'Test iPhone', state: 'paired via localNetwork' });
    }
  });

  it('🔴 an EMPTY device list is a real answer — no phone attached', () => {
    // The direction that must not be over-corrected into a failure: devicectl with nothing plugged in
    // still writes a valid document with an empty array, and that genuinely means "no devices".
    const lookup = parseDeviceList(listing());

    expect(lookup.ok).toBe(true);
    if (lookup.ok) expect(lookup.devices).toEqual([]);
  });

  it('🔴 NOTHING PARSEABLE is a failure, not an empty list', () => {
    // devicectl missing, refusing, or writing nothing at all. Answering "[]" here is what turned a
    // broken reader into a statement about the phone.
    expect(parseDeviceList('').ok).toBe(false);
    expect(parseDeviceList('xcrun: error: unable to find utility').ok).toBe(false);
    expect(parseDeviceList('{ this is not json').ok).toBe(false);
  });

  it('a well-formed document with no result.devices is a failure too', () => {
    // A shape change in devicectl would land here, and silently reporting "no devices" for it is the
    // same bug wearing a different hat.
    const lookup = parseDeviceList(JSON.stringify({ result: {} }));

    expect(lookup.ok).toBe(false);
    if (!lookup.ok) expect(lookup.detail).toContain('result.devices');
  });

  it('tolerates the leading noise devicectl prints before its JSON', () => {
    const lookup = parseDeviceList(`some progress chatter\n${listing('Phone')}`);

    expect(lookup.ok).toBe(true);
    if (lookup.ok) expect(lookup.devices[0]?.name).toBe('Phone');
  });
});

describe('describeLogOutcome — "logged nothing" vs "could not read the log"', () => {
  // 🔴 The status was DISCARDED, one line below a device branch that carefully separates SIGPIPE from a
  // real failure. A run with no booted simulator printed the header and then nothing, exit 0 — which
  // reads as "my app logged nothing", the exact confusion simulatorLogPredicate's own doc says this
  // command exists to avoid.
  //
  // ⚠ THE DECISION IS COVERED HERE; THE WIRING IN `cmdLog` IS NOT, and that is measured rather than
  // assumed — reinstating the discarded-status version leaves all three of these green. It cannot be
  // closed the way `describeSpawnFailure`'s was: `cmdLog` opens with `assertMac()`, so the whole path is
  // unreachable anywhere this suite runs. macOS/e2e territory, said out loud.

  it('a failed read says the reader failed, and says how to fix it', () => {
    const outcome = describeLogOutcome(1, '');

    expect(outcome.kind).toBe('failed');
    if (outcome.kind === 'failed') expect(outcome.hint).toContain('--simulator');
  });

  it('🔴 EMPTY output is not a failure — it is a quiet app', () => {
    // The direction that matters most for not over-correcting: a booted simulator whose app has not run
    // in the window legitimately matches nothing, and calling that "could not read the log" would send
    // someone hunting a broken tool instead of launching their app.
    const outcome = describeLogOutcome(0, '   \n  \n');

    expect(outcome.kind).toBe('empty');
    if (outcome.kind === 'empty') expect(outcome.message).toContain('10m');
  });

  it('real lines come back verbatim, minus trailing blank space', () => {
    const outcome = describeLogOutcome(0, 'line one\nline two\n\n');

    expect(outcome.kind).toBe('ok');
    if (outcome.kind === 'ok') expect(outcome.text).toBe('line one\nline two');
  });
});

describe('findArtifact — what `shenora ios build` produced', () => {
  // Reuses the file's own tempDir so `afterEach` cleans up — a second cleanup list would be a second
  // thing to forget.
  const make = (...names: string[]) => {
    const dir = tempDir();
    for (const n of names) fs.mkdirSync(path.join(dir, n));
    return dir;
  };

  // ⚠ The build machine, which is normally the Mac. `LocalTarget` here is what makes these cases
  // runnable on the Windows box the gate runs on: the LOGIC being pinned (prefer the .ipa, refuse a
  // stale artifact) is the target's own, and none of it needs a Mac to be true.
  const here = new LocalTarget();

  it('🔴 prefers the .ipa — that is the distributable', () => {
    const dir = make('MyApp.app', 'MyApp.ipa');
    expect(findArtifact(here, dir)).toBe(path.join(dir, 'MyApp.ipa'));
  });

  it('falls back to the .app so the command can say WHY it is not distributable', () => {
    // The SDK leaves a .app when signing could not produce an archive. "Nothing was produced" and
    // "produced, but not signed into an archive" are different problems with different fixes, and a
    // null here would collapse them into the first.
    const dir = make('MyApp.app');
    expect(findArtifact(here, dir)).toBe(path.join(dir, 'MyApp.app'));
  });

  it('answers null for a directory that does not exist, rather than throwing', () => {
    expect(findArtifact(here, path.join(os.tmpdir(), 'shenora-nope-' + Date.now()))).toBeNull();
  });

  it('answers null when the publish left neither', () => {
    expect(findArtifact(here, make('intermediate'))).toBeNull();
  });

  it('🔴 rejects an artifact that PREDATES the build it is supposed to be the output of', () => {
    // The identical incident class findPackage guards on the Android side, and this file's own comment
    // records it happening here: `dotnet publish` exits 0 having produced nothing (a skipped target),
    // and without this the previous run's .ipa is reported — size and all — as this build's output.
    const dir = make('MyApp.ipa');
    const artifact = path.join(dir, 'MyApp.ipa');
    const old = Date.now() - 60 * 60_000;
    fs.utimesSync(artifact, new Date(old), new Date(old));

    expect(findArtifact(here, dir, Date.now() - 5_000)).toBeNull();
    expect(findArtifact(here, dir, old - 5_000)).toBe(artifact);
  });

  it('a stale .ipa beside a fresh .app yields the .app — this run’s real output', () => {
    // Signing broke THIS run but an old archive lies around: the fresh .app is what lets cmdBuild say
    // "produced, but not distributable" instead of reporting yesterday's .ipa as today's build.
    const dir = make('MyApp.app', 'MyApp.ipa');
    const old = Date.now() - 60 * 60_000;
    fs.utimesSync(path.join(dir, 'MyApp.ipa'), new Date(old), new Date(old));
    // The .app clock is its Info.plist when present — a directory's own mtime can survive a rebuild.
    fs.writeFileSync(path.join(dir, 'MyApp.app', 'Info.plist'), '<plist/>');

    expect(findArtifact(here, dir, Date.now() - 5_000)).toBe(path.join(dir, 'MyApp.app'));
  });
});

describe('describeConnection — what "can I deploy to this phone?" actually reads', () => {
  it('🔴 reports a paired LAN device as PAIRED, not as the tunnel it has not opened yet', () => {
    // Measured on a real iPhone 17 Pro: pairingState=paired, transportType=localNetwork, and
    // tunnelState=disconnected because the debug tunnel comes up on demand. Printing the tunnel made a
    // usable phone read as unplugged.
    expect(describeConnection({
      pairingState: 'paired', transportType: 'localNetwork', tunnelState: 'disconnected',
    })).toBe('paired via localNetwork');
  });

  it('names a wired device the same way', () => {
    expect(describeConnection({ pairingState: 'paired', transportType: 'wired' })).toBe('paired via wired');
  });

  it('says unknown rather than inventing a state', () => {
    expect(describeConnection(undefined)).toBe('unknown');
  });
});

describe('argValue', () => {
  it('reads the token after the flag', () => {
    expect(argValue(['-n', '200'], '-n')).toBe('200');
  });

  it('returns undefined when the flag ENDS the list', () => {
    // `--simulator` with no name is valid (use whatever is booted), so this must not read past the end.
    expect(argValue(['--simulator'], '--simulator')).toBeUndefined();
  });

  it('returns undefined when the flag is absent', () => {
    expect(argValue(['--device', 'x'], '--simulator')).toBeUndefined();
  });

  it('🔴 does not read the NEXT FLAG as the value', () => {
    // `ios log --device -n 700` refused with "no connected device matches \"-n\"" against a real phone.
    // Every flag here is optional-valued, so the token after one is only a value if it is not a flag.
    expect(argValue(['--device', '-n', '700'], '--device')).toBeUndefined();
    expect(argValue(['--device', '-n', '700'], '-n')).toBe('700');
  });

  it('still reads a real value that follows the flag', () => {
    expect(argValue(['--device', 'Feedfinger-iPhone', '-n', '700'], '--device')).toBe('Feedfinger-iPhone');
  });
});

describe('loadConfig', () => {
  it('returns null when there is no config anywhere up the tree', () => {
    const dir = tempDir();
    expect(loadConfig(dir)).toBeNull();
  });

  it('finds a config in a PARENT directory, so a monorepo can run it from anywhere', () => {
    const root = tempDir();
    const nested = path.join(root, 'apps', 'web');
    fs.mkdirSync(nested, { recursive: true });
    fs.writeFileSync(path.join(root, CONFIG_FILE), JSON.stringify({ project: 'a.csproj', bundleId: 'com.x' }));

    const cfg = loadConfig(nested);
    expect(cfg?.project).toBe('a.csproj');
    // `root` must be the config's directory, NOT the cwd — every relative path is resolved against it,
    // so getting this wrong builds a project that does not exist and blames the adopter's config.
    expect(cfg?.root).toBe(fs.realpathSync(root));
  });

  it('applies defaults for anything the adopter left out', () => {
    const dir = tempDir();
    fs.writeFileSync(path.join(dir, CONFIG_FILE), JSON.stringify({ project: 'a.csproj' }));
    const cfg = loadConfig(dir)!;
    expect(cfg.tfm).toBe('net10.0-ios');
    expect(cfg.configuration).toBe('Debug');
  });

  it('lets the adopter override a default rather than merging around it', () => {
    const dir = tempDir();
    fs.writeFileSync(path.join(dir, CONFIG_FILE), JSON.stringify({ project: 'a.csproj', configuration: 'Release' }));
    expect(loadConfig(dir)!.configuration).toBe('Release');
  });

  it('reports malformed JSON as a config problem, not a crash', () => {
    // Unhandled, this surfaced as a bare Node ESM stack trace, which reads as "the CLI is broken"
    // rather than "your config has a typo". Found by writing a broken file on purpose on a real Mac.
    const dir = tempDir();
    fs.writeFileSync(path.join(dir, CONFIG_FILE), '{ "project": oops }');
    const before = process.exitCode;
    try {
      expect(loadConfig(dir)).toBeNull();
      expect(process.exitCode).toBe(1);
    } finally {
      process.exitCode = before;
    }
  });

  it('ships a SAMPLE config that actually parses', () => {
    // `shenora init` writes this verbatim. A typo here breaks the very first command an adopter runs.
    const parsed = JSON.parse(SAMPLE_CONFIG) as Record<string, unknown>;
    expect(parsed.project).toBeTruthy();
    expect(parsed.bundleId).toBeTruthy();
  });
});

describe('parseDevices — adb, and the states that must NOT be filtered out', () => {
  const OUT = [
    'List of devices attached',
    'emulator-5554\tdevice',
    '127.0.0.1:16384\tdevice',
    'R5CT30ABCDE\tunauthorized',
    'ZY223KKKKK\toffline',
    '',
  ].join('\n');

  it('reads serial and state', () => {
    expect(parseDevices(OUT)).toEqual([
      { serial: 'emulator-5554', state: 'device' },
      { serial: '127.0.0.1:16384', state: 'device' },
      { serial: 'R5CT30ABCDE', state: 'unauthorized' },
      { serial: 'ZY223KKKKK', state: 'offline' },
    ]);
  });

  it('🔴 keeps `unauthorized`, because that phone is waiting for a tap', () => {
    // Filtering it would report "no devices" with one plainly plugged in — the developer then debugs
    // the cable instead of tapping "Allow USB debugging".
    expect(parseDevices(OUT).some((d) => d.state === 'unauthorized')).toBe(true);
  });

  it('ignores the header and adb daemon chatter', () => {
    const noisy = ['List of devices attached',
      '* daemon not running; starting now at tcp:5037 *',
      '* daemon started successfully *',
      'emulator-5554\tdevice', ''].join('\n');
    expect(parseDevices(noisy)).toEqual([{ serial: 'emulator-5554', state: 'device' }]);
  });

  it('answers empty when nothing is attached', () => {
    expect(parseDevices('List of devices attached\n\n')).toEqual([]);
  });
});

describe('adbCandidates — where the Android SDK actually is', () => {
  it('🔴 includes %LOCALAPPDATA%\\Android\\Sdk, which sets no environment variable', () => {
    // Visual Studio's Android SDK installs there and exports nothing. Without this fallback,
    // `android doctor` reported "adb NOT FOUND" on a machine that had it — a true statement about
    // PATH, read as a missing SDK. Measured here 2026-08-09.
    const found = adbCandidates({ LOCALAPPDATA: 'C:\\Users\\x\\AppData\\Local' } as NodeJS.ProcessEnv);
    expect(found.some((c) => c.includes('Android') && c.includes('Sdk'))).toBe(true);
  });

  it('prefers ANDROID_HOME when it is set', () => {
    // ⚠ Asserted through `path.join`, not against a literal: this suite runs on Windows, where join
    // normalises separators, and a hand-written '/opt/sdk' fails against '\opt\sdk' for no real reason.
    const found = adbCandidates({ ANDROID_HOME: '/opt/sdk' } as NodeJS.ProcessEnv);
    expect(found[0]).toBe(path.join('/opt/sdk', 'platform-tools',
      process.platform === 'win32' ? 'adb.exe' : 'adb'));
  });

  it('answers empty rather than inventing a path when nothing is set', () => {
    expect(adbCandidates({} as NodeJS.ProcessEnv)).toEqual([]);
  });
});

describe('resolveJdk — the dependency that fails LATE', () => {
  it('takes JAVA_HOME when it points at a real JDK', () => {
    const home = tempDir();
    fs.mkdirSync(path.join(home, 'bin'));
    fs.writeFileSync(path.join(home, 'bin', process.platform === 'win32' ? 'java.exe' : 'java'), '');
    expect(resolveJdk({ JAVA_HOME: home } as NodeJS.ProcessEnv)).toBe(home);
  });

  it('🔴 ignores a JAVA_HOME that has no java in it', () => {
    // An empty or stale JAVA_HOME is worse than none: the build accepts it and fails deep inside an
    // MSBuild target with a Java error, which reads as a broken SDK.
    expect(resolveJdk({ JAVA_HOME: tempDir() } as NodeJS.ProcessEnv)).toBeNull();
  });

  it('answers null when there is nothing to find, so the caller can say what to do', () => {
    expect(resolveJdk({} as NodeJS.ProcessEnv)).toBeNull();
  });

  it('🔴 every dotnet invocation passes the resolved JDK on', () => {
    // The defect this exists for: `android doctor` printed `jdk C:\…\jbr` and `android build` then died
    // `error XA5300: The Java SDK directory could not be found`, because only `cmdDeploy` resolved a JDK
    // and `cmdBuild`'s publish ran with no env at all — so it inherited whatever the shell carried.
    //
    // ⚠ Scanned from the SOURCE rather than run, and that is the point: a publish only proves anything on
    // a box with no JAVA_HOME, and the maintainer's has one — which is exactly why this survived to an
    // adopter. Scrubbing it from `process.env` would not help either, since `resolveJdk` then finds
    // Android Studio's `jbr`. What can be checked anywhere is that no call site is left without the env.
    const source = fs.readFileSync(new URL('./android.ts', import.meta.url), 'utf8');
    const calls: string[] = [];
    for (let at = source.indexOf("run('dotnet'"); at >= 0; at = source.indexOf("run('dotnet'", at + 1)) {
      const end = source.indexOf(');', at);
      calls.push(source.slice(at, end < 0 ? source.length : end));
    }
    // Anti-vacuity: `run` renamed or the calls reshaped makes the loop above find nothing and every
    // assertion below pass. Both commands spawn dotnet, so fewer than two means the scan missed one.
    expect(calls.length).toBeGreaterThanOrEqual(2);
    for (const call of calls) {
      expect(call).toContain('cwd:');                    // the options object really was captured
      expect(call).toContain('env: { JAVA_HOME: jdk }');
    }
  });
});

describe('findPackage — the Android artifact', () => {
  const make = (...names: string[]) => {
    const dir = tempDir();
    for (const n of names) fs.writeFileSync(path.join(dir, n), '');
    return dir;
  };

  it('🔴 prefers -Signed.apk — the unsigned one installs nowhere', () => {
    // The SDK leaves both side by side. Handing back the unsigned file is a failure the adopter meets
    // minutes later, on a device, with an error that says nothing about which file they were given.
    const dir = make('com.x.apk', 'com.x-Signed.apk');
    expect(findPackage(dir)).toBe(path.join(dir, 'com.x-Signed.apk'));
  });

  it('takes the .aab when that is what was asked for', () => {
    const dir = make('com.x.aab');
    expect(findPackage(dir, 'aab')).toBe(path.join(dir, 'com.x.aab'));
  });

  it('answers null rather than throwing for a missing directory', () => {
    expect(findPackage(path.join(os.tmpdir(), 'shenora-none-' + Date.now()))).toBeNull();
  });

  it('🔴 an --aab build is NEVER handed an APK left over beside it', () => {
    // The defect: `-Signed.apk` was preferred unconditionally, so `android build --aab` run in a
    // directory still holding an earlier APK reported that APK as the artifact, size and all. The user
    // uploads it to Play believing it is the bundle they just built. An adjacent file is not an answer
    // to a different question.
    const dir = make('com.x.apk', 'com.x-Signed.apk');

    expect(findPackage(dir, 'aab')).toBeNull();
    expect(findPackage(dir, 'apk')).toBe(path.join(dir, 'com.x-Signed.apk'));
  });

  it('an APK build is not handed a bundle either', () => {
    // The same rule in the other direction, which the old fallback chain also got wrong.
    const dir = make('com.x.aab');
    expect(findPackage(dir, 'apk')).toBeNull();
  });

  it('🔴 rejects an artifact that PREDATES the build it is supposed to be the output of', () => {
    // The publish directory is not cleaned between runs, so a build that produced nothing hands back
    // the previous run's file and every downstream step — the size line, an upload, a device install —
    // believes it succeeded. Reported as stale rather than as absent, because a "no .apk appeared"
    // message beside a directory visibly containing one is the most confusing thing this could print.
    const dir = make('com.x-Signed.apk');
    const artifact = path.join(dir, 'com.x-Signed.apk');
    const old = Date.now() - 60 * 60_000;
    fs.utimesSync(artifact, new Date(old), new Date(old));

    expect(findPackage(dir, 'apk', Date.now() - 5_000)).toBeNull();
    // …and the same file IS accepted when the build really did produce it.
    expect(findPackage(dir, 'apk', old - 5_000)).toBe(artifact);
  });
});

describe('pickBindingBand — an Xcode older than the newest bindings can still build', () => {
  it('🔴 picks the newest band AT OR BELOW the SDK, which is the measured unblock', () => {
    // The real machine: Xcode 26.3, packs requiring 26.0 / 26.6 / 27.0. No band ships for 26.3 at all,
    // so `dotnet workload update` only changes WHICH Xcode is demanded — pinning 26.0 is what builds.
    expect(pickBindingBand(['26.0', '26.6', '27.0'], '26.3')).toBe('26.0');
  });

  it('prefers the SDK\'s own band when it is installed, rather than dropping further back', () => {
    expect(pickBindingBand(['26.0', '26.5', '27.0'], '26.5')).toBe('26.5');
  });

  it('answers null when every band is newer — that Mac cannot build at all, and should be told so', () => {
    expect(pickBindingBand(['26.6', '27.0'], '26.3')).toBeNull();
  });

  it('orders versions NUMERICALLY — 26.10 is above 26.5, which a string compare gets backwards', () => {
    expect(pickBindingBand(['26.5', '26.10'], '27.0')).toBe('26.10');
    expect(pickBindingBand(['26.5', '26.10'], '26.9')).toBe('26.5');
  });
});

describe('describeBindings — the row has to reflect the PROJECT, not only the machine', () => {
  // The real Mac an adopter hit on 2026-08-21: Xcode SDK 26.3, bands 26.0/26.6/27.0 installed. They
  // pinned exactly as the row instructed and the row still said MISSING, because it never read the csproj.
  const machine = { bands: ['26.0', '26.6', '27.0'], sdk: '26.3' };

  it('🔴 goes GREEN once the csproj pins a band the Xcode can satisfy — the bug that left it red for ever', () => {
    const r = describeBindings({ ...machine, pinned: '26.0' });
    expect(r.good).toBe(true);
    expect(r.text).toContain('26.0');
  });

  it('still fails an UNPINNED project, because the SDK takes the newest band', () => {
    const r = describeBindings({ ...machine, pinned: null });
    expect(r.good).toBe(false);
    expect(r.text).toContain('<TargetPlatformVersion>26.0');
  });

  it('names the Xcode-validation bypass whenever the band in force is not the Xcode\'s own', () => {
    // 🔴 The SECOND constraint, which no choice of band can satisfy: the pack asserts an EXACT Xcode.
    // Green on the band and still unbuildable without the bypass — so `good` true must not silence it.
    const r = describeBindings({ ...machine, pinned: '26.0' });
    expect(r.good).toBe(true);
    expect(r.advice.join(' ')).toContain('ValidateXcodeVersion=false');
  });

  it('says NOTHING about the bypass when a pack was cut for this exact Xcode — the case that needs none', () => {
    const r = describeBindings({ bands: ['26.0', '26.3'], sdk: '26.3', pinned: '26.3' });
    expect(r.good).toBe(true);
    expect(r.advice).toEqual([]);
  });

  it('refuses a pin that is not installed, rather than trusting the csproj', () => {
    const r = describeBindings({ ...machine, pinned: '26.4' });
    expect(r.good).toBe(false);
    expect(r.text).toContain('not installed');
  });

  it('refuses a pin NEWER than the Xcode SDK — a csproj can be wrong in that direction too', () => {
    const r = describeBindings({ ...machine, pinned: '27.0' });
    expect(r.good).toBe(false);
    expect(r.text).toContain('no build can succeed');
  });
});

describe('describeAotCrossPack — the packs `doctor` said `ready` without ever looking at', () => {
  const device = 'Microsoft.NETCore.App.Runtime.AOT.osx-x64.Cross.ios-arm64';
  const simArm = 'Microsoft.NETCore.App.Runtime.AOT.osx-x64.Cross.iossimulator-arm64';
  const simX64 = 'Microsoft.NETCore.App.Runtime.AOT.osx-x64.Cross.iossimulator-x64';

  it('🔴 catches the SKEW that made a `ready` Mac unbuildable, and names both versions', () => {
    // Measured: the iOS SDK resolved 10.0.10; the pack installed was 10.0.11. The build died in
    // AOTCompile naming an MSBuild task rather than the problem.
    const r = describeAotCrossPack({
      expected: '10.0.10',
      packs: [{ pack: device, installed: ['10.0.11'], compilerPresent: false }],
    });
    expect(r.good).toBe(false);
    expect(r.text).toContain('10.0.10');
    expect(r.text).toContain('10.0.11');
  });

  it('🔴 reports EVERY ios cross pack, not the first — the real Mac has three that version apart', () => {
    // Measured on the Intel Mac 2026-08-21: `Cross.iossimulator-x64` carried 10.0.10 (an adopter's
    // symlink) while the device and simulator-arm64 packs did not. Checking one made the verdict a coin
    // flip on directory order, and hid two targets that genuinely cannot build.
    const r = describeAotCrossPack({
      expected: '10.0.10',
      packs: [
        { pack: device, installed: ['10.0.11', '9.0.19'], compilerPresent: false },
        { pack: simArm, installed: ['10.0.11', '9.0.19'], compilerPresent: false },
        { pack: simX64, installed: ['10.0.10', '10.0.11', '9.0.19'], compilerPresent: true },
      ],
    });
    expect(r.good).toBe(false);
    expect(r.text).toContain('2 of 3');
    expect(r.text).toContain('Cross.ios-arm64');
    expect(r.text).toContain('Cross.iossimulator-arm64');
    // The healthy one must NOT be listed as broken.
    expect(r.text).not.toContain('Cross.iossimulator-x64');
  });

  it('passes only when EVERY pack carries the resolved version with its compiler', () => {
    const r = describeAotCrossPack({
      expected: '10.0.11',
      packs: [
        { pack: device, installed: ['10.0.11'], compilerPresent: true },
        { pack: simX64, installed: ['10.0.11'], compilerPresent: true },
      ],
    });
    expect(r.good).toBe(true);
    expect(r.text).toContain('2 pack(s)');
  });

  it('🔴 fails on a present version whose BINARY is missing — the file the task actually opens', () => {
    // Version-matching is not the check; `mono-aot-cross` existing is. A pack directory can be there
    // with nothing usable in it, and that is indistinguishable from the skew at the point of failure.
    const r = describeAotCrossPack({
      expected: '10.0.11',
      packs: [{ pack: device, installed: ['10.0.11'], compilerPresent: false }],
    });
    expect(r.good).toBe(false);
  });

  it('does not GUESS when MSBuild could not be asked — "ready" on a guess is the defect it fixes', () => {
    const r = describeAotCrossPack({
      expected: null,
      packs: [{ pack: device, installed: ['10.0.11'], compilerPresent: false }],
    });
    expect(r.good).toBe(true);
    expect(r.text).toContain('could not ask');
  });

  it('reports a machine with no cross pack at all as MISSING, with the install command', () => {
    const r = describeAotCrossPack({ expected: '10.0.11', packs: [] });
    expect(r.good).toBe(false);
    expect(r.text).toContain('workload install');
  });
});

describe('describeDeviceSigning — a CERTIFICATE is not an ACCOUNT, and neither is a PROFILE', () => {
  // Measured on a Mac that reported `signing identity 1 found` → `shenora: ready` and could not sign at
  // all: valid certificate, free personal team, no Xcode Apple ID, no profiles. The build died on
  // "No Accounts: Add a new account in Accounts settings" AFTER a full compile.
  it('🔴 refuses a certificate with no Apple ID, which is the state that reads as ready', () => {
    const result = describeDeviceSigning({ identities: 1, accounts: 0, profiles: 0 });
    expect(result.good).toBe(false);
    expect(result.text).toContain('NO Xcode Apple ID');
  });

  it('still refuses when profiles exist but no account can refresh them', () => {
    // A free personal team's profile expires after 7 days, so this machine works until it silently
    // does not — and no re-deploy can fix it without the account.
    const result = describeDeviceSigning({ identities: 1, accounts: 0, profiles: 2 });
    expect(result.good).toBe(false);
    expect(result.text).toContain('cannot be refreshed');
  });

  it('an account with NO profile yet is fine — one is minted on the first device build', () => {
    const result = describeDeviceSigning({ identities: 1, accounts: 1, profiles: 0 });
    expect(result.good).toBe(true);
    expect(result.text).toContain('minted');
  });

  it('passes when all three hold', () => {
    expect(describeDeviceSigning({ identities: 1, accounts: 1, profiles: 3 }).good).toBe(true);
  });

  it('does not double-report a missing certificate, and does not guess when a probe failed', () => {
    // The certificate has its own row with its own remedy; and an unreadable preference is not evidence
    // of a broken machine — the `security`-failed case above already taught that.
    expect(describeDeviceSigning({ identities: 0, accounts: 1, profiles: 1 }).text).toContain('see the row above');
    expect(describeDeviceSigning({ identities: null, accounts: null, profiles: 0 }).good).toBe(true);
    expect(describeDeviceSigning({ identities: 1, accounts: null, profiles: 0 }).good).toBe(true);
  });
});

describe('platformTfm — a MAUI app has one TFM per platform', () => {
  const base: DeployConfig = { project: 'a.csproj', tfm: 'net10.0-ios', androidTfm: 'net10.0-android',
    androidLogTag: 'DOTNET', bundleId: 'com.x', team: '', configuration: 'Debug',
    webDir: '', webTarget: '', root: '/tmp', file: '/tmp/x.json' };

  it('🔴 refuses an ANDROID tfm on the iOS path, naming the field and the fix', () => {
    // The measured failure: `tfm` reads as "the tfm" beside `androidTfm`, so an Android-only project
    // sets it, and `ios deploy` dies on NETSDK1147 asking for the android workload — on a Mac, which
    // reads as a broken machine rather than a config that cannot express two heads.
    const errors: string[] = [];
    const spy = vi.spyOn(console, 'error').mockImplementation((m: unknown) => { errors.push(String(m)); });
    // Restored like every other exit-code assertion here: a refusal sets process.exitCode, and leaving
    // it set fails an UNRELATED later test (it failed "bare `shenora` is help, not an error").
    const before = process.exitCode;
    try {
      expect(platformTfm({ ...base, tfm: 'net10.0-android' }, 'ios')).toBeNull();
      expect(process.exitCode).toBe(1);
    } finally { spy.mockRestore(); process.exitCode = before; }
    expect(errors.join('\n')).toContain('not a ios target');
    expect(errors.join('\n')).toContain('iosTfm');
  });

  it('prefers the explicit iosTfm over the unqualified tfm', () => {
    expect(iosTfmOf({ ...base, iosTfm: 'net10.0-ios18.0' })).toBe('net10.0-ios18.0');
    expect(platformTfm({ ...base, tfm: 'net10.0-android', iosTfm: 'net10.0-ios' }, 'ios')).toBe('net10.0-ios');
    // …and falls back, so every existing config keeps working untouched.
    expect(iosTfmOf(base)).toBe('net10.0-ios');
  });

  it('accepts each platform its own', () => {
    expect(platformTfm(base, 'ios')).toBe('net10.0-ios');
    expect(platformTfm(base, 'android')).toBe('net10.0-android');
  });
});

describe('requireFields', () => {
  const base = { project: '', tfm: 'net10.0-ios', androidTfm: 'net10.0-android',
    androidLogTag: 'DOTNET', bundleId: '', team: '', configuration: 'Debug',
    webDir: '', webTarget: '', root: '/tmp', file: '/tmp/x.json' };

  it('passes when every named field is present', () => {
    expect(requireFields({ ...base, project: 'a.csproj', bundleId: 'com.x' }, ['project', 'bundleId'])).toBe(true);
  });

  it('fails when one is missing, and sets a non-zero exit code', () => {
    const before = process.exitCode;
    try {
      expect(requireFields({ ...base, project: 'a.csproj' }, ['project', 'bundleId'])).toBe(false);
      expect(process.exitCode).toBe(1);
    } finally {
      process.exitCode = before;
    }
  });

  it('treats WHITESPACE as missing', () => {
    // `"bundleId": " "` would otherwise reach `xcrun simctl launch` and fail there instead of here.
    const before = process.exitCode;
    try {
      expect(requireFields({ ...base, project: 'a.csproj', bundleId: '   ' }, ['bundleId'])).toBe(false);
    } finally {
      process.exitCode = before;
    }
  });
});

// ── `shenora copy` — the only command in this package that DELETES ────────────────────────────────
// 🔴 It had no coverage at all, and it owns the one destructive operation. Measured against the code
// before these guards, with a config root of `D:/adopter`:
//   webTarget ""              -> deletes the app-head project directory, .csproj included
//   webTarget "Resources/Raw" -> deletes every MAUI raw asset the adopter has
//   webTarget "../../.."      -> rmSync("D:\")
//   project   "../x/A.csproj" -> escapes the config root entirely
// None of them is an attack; each is an adopter answering "where does the app head serve its bundle
// from?" slightly wrong. Every case below runs against a REAL temp tree, so a regression deletes a
// fixture rather than passing.
// 🔴 The fix that was made ONCE and needed making FOUR times. `copy.ts` learned that `project` may
// name a directory; the three BUILD commands each kept `path.dirname(cfg.project)` and so looked for
// their artifact one level too high — reporting "the publish reported success but no .apk appeared"
// about a folder that could never hold one. That message is the CLI's recurring failure shape: a
// signal meaning "I looked in the wrong place" presented as a fact about your build.
describe('projectDir — one answer to "where is the app head?", shared by all four call sites', () => {
  const stageProject = () => {
    const root = path.join(tempDir(), 'app');
    fs.mkdirSync(path.join(root, 'src', 'MyApp'), { recursive: true });
    fs.writeFileSync(path.join(root, 'src', 'MyApp', 'MyApp.csproj'), '<Project/>');
    return root;
  };
  const cfgFor = (root: string, project: string) =>
    ({ root, file: path.join(root, CONFIG_FILE), project } as Parameters<typeof projectDir>[0]);

  it('a project naming a DIRECTORY resolves to that directory, not its parent', () => {
    const root = stageProject();
    expect(projectDir(cfgFor(root, 'src/MyApp'))).toBe(path.join(root, 'src', 'MyApp'));
  });

  it('a project naming the .csproj resolves to the folder holding it', () => {
    const root = stageProject();
    expect(projectDir(cfgFor(root, 'src/MyApp/MyApp.csproj'))).toBe(path.join(root, 'src', 'MyApp'));
  });

  it('a project that does not exist is treated as a FILE, so the message names the folder', () => {
    // Not a directory on disk, so `dirname` is the only honest reading — and the caller then reports a
    // missing artifact under a path the user recognises, rather than crashing on a stat.
    const root = stageProject();
    expect(projectDir(cfgFor(root, 'src/Gone/Gone.csproj'))).toBe(path.join(root, 'src', 'Gone'));
  });
});

describe('cmdCopy — refuses to delete what it did not create', () => {
  const stage = (webTarget: string, project = 'src/MyApp/MyApp.csproj') => {
    // The config root is a CHILD of the temp dir, so a `..` escape lands somewhere afterEach cleans.
    // ⚠ Learned by leaving one behind: an unguarded run wrote to the system temp root, and the next
    // run of the same test then found it and failed — correctly, but for the previous run's reason.
    const root = path.join(tempDir(), 'app');
    fs.mkdirSync(root, { recursive: true });
    fs.mkdirSync(path.join(root, 'dist'), { recursive: true });
    fs.writeFileSync(path.join(root, 'dist', 'index.html'), '<html></html>');
    fs.mkdirSync(path.join(root, 'src', 'MyApp'), { recursive: true });
    fs.writeFileSync(path.join(root, 'src', 'MyApp', 'MyApp.csproj'), '<Project/>');
    return {
      root,
      cfg: {
        root, file: path.join(root, CONFIG_FILE), project, webDir: 'dist', webTarget,
        bundleId: 'com.x', configuration: 'Debug',
      } as Parameters<typeof cmdCopy>[0],
    };
  };
  const quietly = (body: () => void) => {
    const before = process.exitCode;
    try { body(); } finally { process.exitCode = before; }
  };

  it('copies into the app head on the happy path, and leaves its marker', () => {
    const { root, cfg } = stage('Resources/Raw/wwwroot');
    quietly(() => cmdCopy(cfg));

    const bundle = path.join(root, 'src', 'MyApp', 'Resources', 'Raw', 'wwwroot');
    expect(fs.existsSync(path.join(bundle, 'index.html'))).toBe(true);
    expect(fs.existsSync(path.join(bundle, '.shenora-bundle'))).toBe(true);
  });

  it('🔴 a `project` naming a DIRECTORY stages into that directory, not its parent', () => {
    // `dotnet restore`/`publish` both accept a directory, and this repo hands `cfg.project` straight to
    // them — so a directory is legitimate config. But `path.dirname` on a directory silently yields its
    // PARENT, so the bundle went to `src/` instead of `src/MyApp/`: one level too high, every
    // containment check still passing, and a cheerful success line. The app then ships with no web
    // assets at all, and the wrong directory has been DELETED to make room for them.
    const { root, cfg } = stage('Resources/Raw/wwwroot', 'src/MyApp');
    quietly(() => cmdCopy(cfg));

    const rightPlace = path.join(root, 'src', 'MyApp', 'Resources', 'Raw', 'wwwroot', 'index.html');
    const oneLevelTooHigh = path.join(root, 'src', 'Resources', 'Raw', 'wwwroot', 'index.html');
    expect(fs.existsSync(rightPlace)).toBe(true);
    expect(fs.existsSync(oneLevelTooHigh)).toBe(false);
  });

  it('a `project` naming the .csproj still resolves to the folder holding it', () => {
    // The other form, unchanged — the fix must not swap one wrong answer for another.
    const { root, cfg } = stage('Resources/Raw/wwwroot', 'src/MyApp/MyApp.csproj');
    quietly(() => cmdCopy(cfg));

    expect(fs.existsSync(path.join(root, 'src', 'MyApp', 'Resources', 'Raw', 'wwwroot', 'index.html')))
      .toBe(true);
  });

  it('REPLACES its own bundle, so a deleted file does not survive', () => {
    const { root, cfg } = stage('Resources/Raw/wwwroot');
    quietly(() => cmdCopy(cfg));
    const bundle = path.join(root, 'src', 'MyApp', 'Resources', 'Raw', 'wwwroot');
    fs.writeFileSync(path.join(bundle, 'stale.js'), 'old');

    quietly(() => cmdCopy(cfg));

    // The whole reason it deletes rather than merges: a stale asset is still served and still embedded.
    expect(fs.existsSync(path.join(bundle, 'stale.js'))).toBe(false);
    expect(fs.existsSync(path.join(bundle, 'index.html'))).toBe(true);
  });

  it('refuses an existing directory it did not create, rather than emptying it', () => {
    // The `Resources/Raw` case: a well-formed path, one level too high. No path check can see that,
    // which is why the marker exists.
    const { root, cfg } = stage('Resources/Raw');
    const assets = path.join(root, 'src', 'MyApp', 'Resources', 'Raw');
    fs.mkdirSync(assets, { recursive: true });
    fs.writeFileSync(path.join(assets, 'OpenSans.ttf'), 'font bytes');

    quietly(() => cmdCopy(cfg));

    expect(fs.existsSync(path.join(assets, 'OpenSans.ttf'))).toBe(true);
    expect(fs.existsSync(path.join(assets, 'index.html'))).toBe(false);
  });

  it.each([
    ['an empty webTarget, which resolves to the project directory itself', ''],
    ['a webTarget that walks out of the app head', '../../..'],
    ['a webTarget that is absolute', path.resolve(path.sep, 'somewhere-else')],
  ])('refuses %s', (_why, webTarget) => {
    const { root, cfg } = stage(webTarget);
    quietly(() => cmdCopy(cfg));

    // The app head must still be intact — the .csproj is the canary the old code deleted.
    expect(fs.existsSync(path.join(root, 'src', 'MyApp', 'MyApp.csproj'))).toBe(true);
    expect(fs.existsSync(path.join(root, 'src', 'MyApp', 'index.html'))).toBe(false);
  });

  it('refuses a project that escapes the config root', () => {
    const { root, cfg } = stage('Resources/Raw/wwwroot', '../sibling/A.csproj');
    quietly(() => cmdCopy(cfg));

    expect(fs.existsSync(path.resolve(root, '..', 'sibling'))).toBe(false);
  });

  // 🔴 The stamp is what lets a shell know which version its OWN packaged client is — the enabling cause
  // of a defect that is otherwise silent and permanent (a fetched bundle outranking the packaged one for
  // ever, because the comparison could not be written). See `ResourcePackJournal`.
  describe('the packaged-version stamp', () => {
    // ⚠ No leading dot, and this literal is the point of the assertion rather than incidental to it: a
    // dot-prefixed MauiAsset is discarded on its way into an Android app, silently, so a hidden stamp
    // ships everywhere except the platform with the app stores.
    const bundleOf = (root: string) =>
      path.join(root, 'src', 'MyApp', 'Resources', 'Raw', 'wwwroot', 'shenora-pack.json');

    it('carries the web app\'s OWN declared version, copied rather than invented', () => {
      const { root, cfg } = stage('Resources/Raw/wwwroot');
      fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ version: '2.4.1' }));

      quietly(() => cmdCopy(cfg));

      expect(JSON.parse(fs.readFileSync(bundleOf(root), 'utf8'))).toEqual({ version: '2.4.1' });
    });

    it('writes NO stamp when the app declares no version, rather than inventing one', () => {
      // ⚠ A stamp the tool made up is worse than none: it would compare as a real version and be wrong
      // in whichever direction it happened to sort.
      const { root, cfg } = stage('Resources/Raw/wwwroot');
      fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ name: 'no-version-here' }));

      quietly(() => cmdCopy(cfg));

      expect(fs.existsSync(bundleOf(root))).toBe(false);
    });

    it('survives a malformed package.json instead of failing the copy', () => {
      // The copy is the valuable half; a stamp is an addition to it. Taking the whole command down over
      // unparseable JSON would break a build that was working before this feature existed.
      const { root, cfg } = stage('Resources/Raw/wwwroot');
      fs.writeFileSync(path.join(root, 'package.json'), '{ not json');

      quietly(() => cmdCopy(cfg));

      const bundle = path.join(root, 'src', 'MyApp', 'Resources', 'Raw', 'wwwroot');
      expect(fs.existsSync(path.join(bundle, 'index.html'))).toBe(true);
      expect(fs.existsSync(bundleOf(root))).toBe(false);
    });

    it('is REPLACED on the next copy, so a downgrade cannot leave the old number behind', () => {
      const { root, cfg } = stage('Resources/Raw/wwwroot');
      fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ version: '2.0.0' }));
      quietly(() => cmdCopy(cfg));

      fs.writeFileSync(path.join(root, 'package.json'), JSON.stringify({ version: '1.9.0' }));
      quietly(() => cmdCopy(cfg));

      expect(JSON.parse(fs.readFileSync(bundleOf(root), 'utf8'))).toEqual({ version: '1.9.0' });
    });
  });
});

// 🔴 THE ROUTING HAD NO TEST AT ALL — every command in this CLI goes through it, and it could not be
// exercised because `cli.ts` called `main` at module scope: importing it to test it would have run
// whatever argv the test runner happened to carry. It runs conditionally now, which is what makes the
// cases below possible.
describe('main — the group/verb routing, and what it BLAMES when it cannot proceed', () => {
  const capture = (argv: string[]) => {
    const out: string[] = [];
    const err: string[] = [];
    const log = console.log;
    const error = console.error;
    const exit = process.exitCode;
    console.log = (...a: unknown[]) => { out.push(a.join(' ')); };
    console.error = (...a: unknown[]) => { err.push(a.join(' ')); };
    // From the temp dir, so a shenora.deploy.json in this repo cannot be found by the parent walk and
    // quietly turn a "no config" case into a configured one.
    const cwd = process.cwd();
    const scratch = tempDir();
    try {
      process.chdir(scratch);
      main(argv);
      return { out: out.join('\n'), err: err.join('\n'), code: process.exitCode };
    } finally {
      process.chdir(cwd);
      console.log = log;
      console.error = error;
      process.exitCode = exit;
    }
  };

  it('`shenora ios` with no verb asks for a command — it does NOT blame the config', () => {
    // The defect: `needConfig()` ran first, so typing a group name to see its verbs answered "no
    // shenora.deploy.json here or in any parent directory" — true, and about something else entirely.
    const { err, code } = capture(['ios']);

    expect(err).toContain('needs a command');
    expect(err).not.toContain(CONFIG_FILE);
    expect(code).toBe(1);
  });

  it('a MISTYPED verb names the typo rather than the missing config', () => {
    const { err } = capture(['ios', 'delpoy']);

    expect(err).toContain('unknown ios command');
    expect(err).toContain('delpoy');
    expect(err).not.toContain(CONFIG_FILE);
  });

  it('the android half behaves identically — the two used to diverge', () => {
    expect(capture(['android']).err).toContain('needs a command');
    expect(capture(['android', 'buidl']).err).toContain('unknown android command');
  });

  it('an unknown GROUP says which word it did not understand', () => {
    const { err, out, code } = capture(['ois']);

    expect(err).toContain('unknown command');
    expect(err).toContain('ois');
    expect(out).toContain('shenora — take a built app');   // usage still printed
    expect(code).toBe(1);
  });

  it('bare `shenora` is help, not an error', () => {
    const { out, code } = capture([]);

    expect(out).toContain('shenora — take a built app');
    expect(code).toBeUndefined();
  });
});
