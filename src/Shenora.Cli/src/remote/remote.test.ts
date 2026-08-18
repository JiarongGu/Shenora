// The remote transport's claims. Every one of these is a trap whose failure mode is a WRONG ANSWER —
// a truncated command reporting success, a build failure that looks like a slow build, "cannot connect"
// standing in for six unrelated causes.
import { describe, it, expect } from 'vitest';
import { SshTarget, guiScript, hasHost, SSH_COMMAND_LIMIT } from './ssh.js';
import { LocalTarget, withCwd } from './target.js';
import { parseHostSpec, classifySshFailure, resolveHost } from './host.js';
import type { DeployConfig } from '../config.js';
import { buildProject, buildDir, countXcodeAccounts } from '../ios.js';
import { filesToPush } from './push.js';
import os from 'node:os';

describe('withCwd', () => {
  it('joins with && so a failed cd cannot run the command somewhere else', () => {
    // 🔴 With `;` a mistyped directory runs the build in HOME instead — against whatever happens to be
    // there, reporting honestly about the wrong tree.
    expect(withCwd('dotnet build', '/x/y')).toBe("cd '/x/y' && dotnet build");
    expect(withCwd('dotnet build')).toBe('dotnet build');
  });

  it('quotes a path containing a space', () => {
    expect(withCwd('ls', '/a b/c')).toContain("'/a b/c'");
  });
});

describe('the ssh command ceiling', () => {
  it('refuses a command past the truncation cliff instead of letting it run', () => {
    const target = new SshTarget({ host: 'nowhere.invalid' });
    // Past ~8 KB ssh truncates SILENTLY and the truncated form can still exit 0 — a redirection falls
    // off the end and the payload prints to stdout instead of landing in a file.
    const huge = 'x'.repeat(SSH_COMMAND_LIMIT + 100);
    const r = target.sh(huge, { quiet: true });
    expect(r.status).toBe(1);
    expect(r.out).toContain('ceiling');
    // The point of the refusal: it did NOT reach out to the network to find that out.
    expect(r.out).not.toContain('Could not resolve');
  });

  it('lets an ordinary command through the same check', () => {
    // Guards the direction that would make the ceiling vacuous: a limit set too low refuses everything
    // and every test above still passes.
    const target = new SshTarget({ host: 'nowhere.invalid' });
    const r = target.sh('echo hi', { quiet: true, timeoutMs: 5_000 });
    expect(r.out).not.toContain('ceiling');
  });
});

describe('the GUI hand-off script', () => {
  const script = guiScript('dotnet build', { done: '/tmp/t.done', log: '/tmp/t.log' });

  it('runs the work in a SUBSHELL, not a brace group', () => {
    // 🔴 The body sets -e. Inside `{ … }` that exits the whole remote shell on the first failure, so the
    // marker write never happens and a FAILED build is indistinguishable from a slow one until the
    // poller times out — sixteen wasted minutes, measured.
    expect(script).toContain('(\nset -e -o pipefail');
    expect(script).not.toContain('{\nset -e');
  });

  it('writes the completion marker OUTSIDE the subshell, after it', () => {
    const closeParen = script.indexOf(') >');
    const marker = script.indexOf('echo $? >');
    expect(closeParen).toBeGreaterThan(-1);
    expect(marker).toBeGreaterThan(closeParen);
  });

  it('captures the log inside the redirect so a failure has evidence', () => {
    expect(script).toContain("> '/tmp/t.log' 2>&1");
  });
});

describe('parseHostSpec', () => {
  it('splits user@host, and takes a bare host as "the same user as here"', () => {
    expect(parseHostSpec('bob@mac.local')).toEqual({ host: 'mac.local', user: 'bob' });
    expect(parseHostSpec('mac.local')).toEqual({ host: 'mac.local' });
    expect(parseHostSpec('  ')).toBeNull();
    expect(parseHostSpec('bob@')).toBeNull();
  });
});

describe('resolveHost precedence', () => {
  const cfg = { remote: { host: 'from-config' }, file: 'x' } as unknown as DeployConfig;

  it('prefers --host over everything', () => {
    expect(resolveHost(cfg, ['--host', 'from-flag'])?.host).toBe('from-flag');
  });

  it('falls back to the config when no flag and no environment', () => {
    const saved = process.env.SHENORA_IOS_HOST;
    delete process.env.SHENORA_IOS_HOST;
    try {
      expect(resolveHost(cfg, [])?.host).toBe('from-config');
      expect(resolveHost(null, [])).toBeNull();
    } finally {
      // ⚠ Restored: the suite shares one process, and a leaked variable makes a LATER test read a host
      // nobody configured.
      if (saved === undefined) delete process.env.SHENORA_IOS_HOST;
      else process.env.SHENORA_IOS_HOST = saved;
    }
  });
});

describe('classifying an ssh refusal', () => {
  const host = { host: 'mac.local' };
  const at = (detail: string) => classifySshFailure(detail, host, 'bob@mac.local');

  it('separates the six causes that all read as "cannot connect"', () => {
    expect(at('ssh: Could not resolve hostname mac.local').verdict).toBe('no-name');
    expect(at('ssh: connect to host mac.local port 22: Connection refused').verdict).toBe('refused');
    expect(at('ssh: connect to host mac.local port 22: Operation timed out').verdict).toBe('unreachable');
    expect(at('bob@mac.local: Permission denied (publickey).').verdict).toBe('denied');
    expect(at('Connection closed by 10.0.0.4 port 22').verdict).toBe('throttled');
    expect(at("shenora: 'ssh' was not found on PATH — install it.").verdict).toBe('no-ssh-client');
  });

  it('tells the truth about an .local name rather than blaming DNS', () => {
    const d = at('ssh: Could not resolve hostname mac.local');
    // An mDNS name is answered by the Mac itself, so this is evidence about the MAC, not about a DNS
    // server — the obvious wrong conclusion, and the one that sends an hour in the wrong direction.
    expect(d.fix.join(' ')).toContain('answered by the Mac ITSELF');
    expect(d.fix.join(' ')).toMatch(/asleep|multicast/);
  });

  it('does not claim DNS advice for a plain hostname', () => {
    const d = classifySshFailure('ssh: Could not resolve hostname buildbox', { host: 'buildbox' }, 'buildbox');
    expect(d.fix.join(' ')).not.toContain('multicast');
  });

  /**
   * 🔴 Found the first time this ran against a real Mac, and the reason it must be checked BEFORE the
   * refusal: ssh reports BOTH. It warns that it could not open the key, then reports the denial that
   * follows from having had none to offer. Matched on the denial, the advice is "authorise your key on
   * the Mac" — sending you to configure the wrong computer, for a file missing on this one.
   */
  it('separates a MISSING key file from a key the Mac refused', () => {
    const real = 'Warning: Identity file C:/Users/x/.ssh/gone not accessible: No such file or directory.\n'
      + 'bob@mac.local: Permission denied (publickey,password,keyboard-interactive).';
    const d = at(real);

    expect(d.verdict).toBe('no-key-file');
    expect(d.summary).toContain('C:/Users/x/.ssh/gone');
    expect(d.fix.join(' ')).toContain('Nothing is wrong with the Mac');
    // ...and it must NOT hand out the authorized_keys advice, which is the whole failure.
    expect(d.fix.join(' ')).not.toContain('authorized_keys');
  });

  it('still reports a genuine refusal as one', () => {
    // The other direction: with no missing-file warning, this IS the key-not-authorised case.
    const d = at('bob@mac.local: Permission denied (publickey).');
    expect(d.verdict).toBe('denied');
    expect(d.fix.join(' ')).toContain('authorized_keys');
  });

  it('names Remote Login for a refused connection — the most common cause by far', () => {
    expect(at('Connection refused').fix.join(' ')).toContain('Remote Login');
  });

  it('warns against a retry loop when sshd hung up', () => {
    // MaxAuthTries: retrying makes it worse, which is the opposite of the instinct.
    expect(at('Connection closed by 10.0.0.4 port 22').fix.join(' ')).toContain('worse');
  });

  it('keeps ssh\'s own words rather than paraphrasing them away', () => {
    expect(at('ssh: some novel failure nobody predicted').detail).toContain('novel failure');
    expect(at('ssh: some novel failure nobody predicted').verdict).toBe('unknown');
  });
});

describe('hasHost', () => {
  it('treats a blank host as no host', () => {
    expect(hasHost({ host: '' })).toBe(false);
    expect(hasHost({ host: '   ' })).toBe(false);
    expect(hasHost(null)).toBe(false);
    expect(hasHost({ host: 'mac.local' })).toBe(true);
  });
});

describe('LocalTarget', () => {
  it('answers about THIS filesystem', () => {
    const local = new LocalTarget();
    expect(local.isRemote).toBe(false);
    expect(local.exists(process.cwd())).toBe(true);
    expect(local.exists('/definitely/not/here')).toBe(false);
    expect(local.list('/definitely/not/here')).toEqual([]);
    expect(local.mtimeMs('/definitely/not/here')).toBeNull();
    expect(local.mtimeMs(process.cwd())).toBeGreaterThan(0);
  });
});

describe('counting Xcode accounts', () => {
  /**
   * 🔴 Both fixtures are VERBATIM from a real Mac. The signed-in one is why this exists: `doctor`
   * reported "no Xcode Apple ID" on a machine where an account was signed in, because the first version
   * looked for a quoted email and Xcode stores an `identifier` UUID instead.
   */
  it('counts a signed-in account that stores an identifier, not an address', () => {
    const signedIn = [
      '{',
      '    "IDE.Identifiers.Prod" =     (',
      '                {',
      '            identifier = "4F2E7F1A-7049-44C5-8DFA-E10AA8F8A558";',
      '        }',
      '    );',
      '}',
    ].join('\n');
    expect(countXcodeAccounts(signedIn)).toBe(1);
  });

  it('reports NONE when the key exists with an empty list', () => {
    // ⚠ The direction that makes the check meaningful: the preference is present either way, so its
    // existence proves nothing and only the parenthesised list can answer.
    expect(countXcodeAccounts('{\n    "IDE.Identifiers.Prod" =     (\n    );\n}')).toBe(0);
  });
});

describe('remote probes', () => {
  it('does not apply pipefail — a probe treats failure as an ANSWER', () => {
    // 🔴 `xcodebuild -version | head -1`: head closes the pipe after one line, xcodebuild dies of
    // SIGPIPE, and pipefail promotes that to the pipeline's status — so the probe returns '' and doctor
    // says MISSING Xcode about a Mac running Xcode 26.3. Racy too, so the same Mac answered correctly
    // one minute and "not installed" the next. `exec.ts`'s LOCAL probe never did this; the remote one
    // must match it, or one capability probe gives two answers depending on transport.
    const source = String(SshTarget.prototype.probe);
    expect(source).toContain('false');          // the pipefail argument
    expect(source).not.toContain('this.sh(');   // routing through sh would re-introduce it
  });
});

describe('choosing what to push', () => {
  it('lists source, not build output', () => {
    // 🔴 `git ls-files -co --exclude-standard` against a directory walk: measured on this repo, 625
    // files versus 23,882 on disk. The difference is bin/, obj/ and node_modules — and copying a
    // Windows obj/ onto a Mac does not merely waste time, it hands that build a stale intermediate
    // stamped for another machine. It also excludes gitignored `local/`, which is private by design.
    const files = filesToPush(process.cwd());
    expect(files).not.toBeNull();
    expect(files!.length).toBeGreaterThan(0);
    expect(files!.some((f) => f.includes('node_modules'))).toBe(false);
    expect(files!.some((f) => /(^|\/)(bin|obj)\//.test(f))).toBe(false);
    expect(files!.some((f) => f.startsWith('local/'))).toBe(false);
    // ...and it really did find this package's own sources.
    expect(files!.some((f) => f.endsWith('push.ts'))).toBe(true);
  });

  it('deletes only what it previously sent, and only what it would no longer send', () => {
    // 🔴 The safety property. `previous MINUS current` can name a file this tool put there and nothing
    // else, so a remote directory holding somebody else's work cannot lose it. Stated as a set operation
    // rather than as a remote `find`, which would have to guess what belongs to us.
    const previous = ['src/Old.cs', 'src/Kept.cs', 'notes.md'];
    const current = ['src/Kept.cs', 'src/New.cs'];
    const stale = previous.filter((f) => !new Set(current).has(f));

    expect(stale).toEqual(['src/Old.cs', 'notes.md']);
    // A file the target has that we never sent is not in `previous`, so it can never appear here.
    expect(stale).not.toContain('their-file.txt');
    // And a file we are about to write again is never deleted first.
    expect(stale).not.toContain('src/Kept.cs');
  });

  it('reports a non-git directory rather than sending everything', () => {
    const saved = process.exitCode;
    try {
      // Fail-closed: with no git there is no way to tell source from output, and the safe answer is to
      // stop rather than to fall back on a walk that would sweep up every build artefact on disk.
      expect(filesToPush(os.tmpdir())).toBeNull();
    } finally {
      process.exitCode = saved;
    }
  });
});

describe('what gets handed to dotnet on a remote build', () => {
  // A target that answers as a Mac would, without one.
  const mac = {
    label: 'you@mac.local', isRemote: true,
    sh: () => ({ status: 0, out: '' }), probe: () => '/Users/you',
    exists: () => true, list: () => [], mtimeMs: () => 1, newestMtimeMs: () => 1,
    join: (...p: string[]) => p.join('/').replace(/\/+/g, '/'),
    basename: (p: string) => p.split('/').pop() ?? p,
    dirname: (p: string) => p.split('/').slice(0, -1).join('/'),
    push: () => true, pull: () => true, gui: () => ({ status: 0, out: '' }), close() {},
  };
  const cfg = {
    // ⚠ A WINDOWS root, because that is the whole point: the CLI runs here and the build runs there, so
    // this path is spelled in one convention and everything derived from it in another.
    root: 'D:\\work\\MyRepo',
    project: 'samples/App/App.csproj',
    configuration: 'Debug',
  } as unknown as DeployConfig;

  it('🔴 names the PROJECT, never the checkout root', () => {
    // The bug this pins cost a real remote build: handed the root, `dotnet build` builds whatever
    // SOLUTION is there — which on this kit's own tree meant the Windows sample and the test project,
    // failing with `NETSDK1100: To build a project targeting Windows on this operating system`. An
    // error about a project nobody asked for, on a Mac that was working perfectly.
    expect(buildProject(cfg, mac)).toBe('/Users/you/MyRepo/samples/App/App.csproj');
    expect(buildProject(cfg, mac)).not.toBe('/Users/you/MyRepo');
  });

  it('puts bin/ beside the project, not beside the solution', () => {
    expect(buildDir(cfg, mac)).toBe('/Users/you/MyRepo/samples/App');
  });

  it('honours an explicit remote dir', () => {
    const pinned = { ...cfg, remote: { host: 'mac.local', dir: '/Volumes/build/Repo' } } as DeployConfig;
    expect(buildProject(pinned, mac)).toBe('/Volumes/build/Repo/samples/App/App.csproj');
  });

  it('treats a directory-shaped project as its own build dir', () => {
    // `cfg.project` MAY name a directory — the SDK accepts one — and then there is no filename to strip.
    const dirProject = { ...cfg, project: 'samples/App' } as DeployConfig;
    expect(buildDir(dirProject, mac)).toBe('/Users/you/MyRepo/samples/App');
  });
});
