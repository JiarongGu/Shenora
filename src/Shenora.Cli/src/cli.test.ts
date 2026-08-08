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
import { splitArgs } from './ios.js';
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

describe('requireFields', () => {
  const base = { project: '', tfm: 'net10.0-ios', bundleId: '', team: '', configuration: 'Debug',
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
