// The CLI's testable core: the decisions that FAIL SILENTLY when wrong.
//
// This suite deliberately does not test process spawning. Everything here is a claim whose failure mode
// is a WRONG ANSWER rather than a crash — a rejected install reported as success, a simulator booted by
// the wrong name, a config read as empty. Those are the ones a human never notices.
import { describe, it, expect, afterEach } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import { withPipefail, argValue } from './exec.js';
import { splitArgs, simulatorLogPredicate, describeConnection, findArtifact } from './ios.js';
import { parseDevices, findPackage, adbCandidates, resolveJdk } from './android.js';
import { loadConfig, requireFields, CONFIG_FILE, SAMPLE_CONFIG } from './config.js';

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

describe('splitArgs — the `--` passthrough', () => {
  it('routes everything after `--` to the build, and nothing before it', () => {
    const { own, extra } = splitArgs(['--simulator', 'iPhone 16 Pro', '--', '-p:Foo=1', '-p:Bar=2']);
    expect(own).toEqual(['--simulator', 'iPhone 16 Pro']);
    expect(extra).toBe(' -p:Foo=1 -p:Bar=2');
  });

  it('🔴 does not let a build property be read as a simulator NAME', () => {
    // The trap this function exists for. `argValue` takes the token after a flag, so on a single flat
    // array `deploy --simulator -- -p:Foo=1` boots a simulator called "-p:Foo=1" and then reports that
    // no such device exists — a confusing failure a long way from its cause.
    const { own, extra } = splitArgs(['--simulator', '--', '-p:ValidateXcodeVersion=false']);
    expect(argValue(own, '--simulator')).toBeUndefined();
    expect(extra).toBe(' -p:ValidateXcodeVersion=false');
  });

  it('is a no-op without a separator', () => {
    const { own, extra } = splitArgs(['--device', 'my-phone']);
    expect(own).toEqual(['--device', 'my-phone']);
    expect(extra).toBe('');
  });

  it('treats a trailing `--` with nothing after it as no extra args', () => {
    // Otherwise the build command gains a stray trailing space and, worse, `extra` reads as truthy —
    // which would print an "extra build args:" line naming nothing.
    expect(splitArgs(['--simulator', '--']).extra).toBe('');
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

describe('findArtifact — what `shenora ios build` produced', () => {
  // Reuses the file's own tempDir so `afterEach` cleans up — a second cleanup list would be a second
  // thing to forget.
  const make = (...names: string[]) => {
    const dir = tempDir();
    for (const n of names) fs.mkdirSync(path.join(dir, n));
    return dir;
  };

  it('🔴 prefers the .ipa — that is the distributable', () => {
    const dir = make('MyApp.app', 'MyApp.ipa');
    expect(findArtifact(dir)).toBe(path.join(dir, 'MyApp.ipa'));
  });

  it('falls back to the .app so the command can say WHY it is not distributable', () => {
    // The SDK leaves a .app when signing could not produce an archive. "Nothing was produced" and
    // "produced, but not signed into an archive" are different problems with different fixes, and a
    // null here would collapse them into the first.
    const dir = make('MyApp.app');
    expect(findArtifact(dir)).toBe(path.join(dir, 'MyApp.app'));
  });

  it('answers null for a directory that does not exist, rather than throwing', () => {
    expect(findArtifact(path.join(os.tmpdir(), 'shenora-nope-' + Date.now()))).toBeNull();
  });

  it('answers null when the publish left neither', () => {
    expect(findArtifact(make('intermediate'))).toBeNull();
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
    expect(findPackage(dir)).toBe(path.join(dir, 'com.x.aab'));
  });

  it('answers null rather than throwing for a missing directory', () => {
    expect(findPackage(path.join(os.tmpdir(), 'shenora-none-' + Date.now()))).toBeNull();
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
