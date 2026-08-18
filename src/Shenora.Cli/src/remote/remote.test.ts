// The remote transport's claims. Every one of these is a trap whose failure mode is a WRONG ANSWER —
// a truncated command reporting success, a build failure that looks like a slow build, "cannot connect"
// standing in for six unrelated causes.
import { describe, it, expect } from 'vitest';
import { SshTarget, guiScript, hasHost, SSH_COMMAND_LIMIT } from './ssh.js';
import { LocalTarget, withCwd } from './target.js';
import { parseHostSpec, classifySshFailure, resolveHost } from './host.js';
import type { DeployConfig } from '../config.js';

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
