// A Mac on the LAN, driven over ssh. The traps here were each measured before they were written down.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { describeSpawnFailure, withPipefail, type RunResult } from '../exec.js';
import { withCwd, type GuiRunOptions, type Target, type TargetRunOptions } from './target.js';

export interface RemoteHost {
  /** Hostname or address. An `.local` name is answered by the device itself — see {@link diagnoseHost}. */
  host: string;
  /** Account on that machine. Defaults to the local username, which is right surprisingly often. */
  user?: string;
  /** Private key path. Omitted means ssh's own default resolution (agent, `~/.ssh/id_*`). */
  key?: string;
  /** Where this project is checked out ON THE MAC, relative to its home unless absolute. */
  dir?: string;
}

const q = (s: string): string => `'${String(s).replace(/'/g, `'\\''`)}'`;

/**
 * 🔴 **A remote command past this is SILENTLY TRUNCATED AND CAN STILL EXIT 0.** Bisected to the byte in
 * this family: 8185 B runs, 8195 B does not — and past the cliff a `printf … | base64 -d > file` loses its
 * redirection, prints the payload to stdout and reports success. Anything larger has to be a file
 * {@link SshTarget.push}ed across, so this refuses rather than letting a truncated command run.
 */
export const SSH_COMMAND_LIMIT = 8192;

/** How long a `gui` script may run before it is declared lost. Device builds are genuinely this slow. */
const GUI_DEFAULT_TIMEOUT_MS = 20 * 60_000;
const GUI_POLL_MS = 5_000;

/** Block this thread. The `gui` hand-off has nothing to await — its script is detached in another session. */
function sleepSync(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/**
 * The body of a detached GUI script: run the work, then record that it finished.
 *
 * 🔴 **A SUBSHELL `( … )`, never a brace group `{ … }`.** The body sets `-e`; inside a brace group that
 * exits the whole remote shell on the first failing command, so the marker write below never runs — and a
 * FAILED build then looks exactly like a slow one until the poller gives up. Measured in this family: a
 * device build failed in four minutes and the watcher printed progress for another sixteen.
 *
 * The marker write must therefore stay OUTSIDE the parentheses, and `$?` must be the subshell's status.
 * Pure so `ssh.test.ts` can hold that shape still.
 */
export function guiScript(script: string, paths: { done: string; log: string }): string {
  return [
    `rm -f ${q(paths.done)} ${q(paths.log)}`,
    `(`,
    `set -e -o pipefail`,
    script,
    `) > ${q(paths.log)} 2>&1`,
    `echo $? > ${q(paths.done)}`,
    '',
  ].join('\n');
}

export class SshTarget implements Target {
  readonly isRemote = true;
  readonly label: string;
  private readonly spec: string;
  private readonly flags: string[];

  constructor(private readonly hostConfig: RemoteHost) {
    const user = hostConfig.user?.trim();
    this.spec = user ? `${user}@${hostConfig.host}` : hostConfig.host;
    this.label = this.spec;
    this.flags = [
      ...(hostConfig.key ? ['-i', hostConfig.key] : []),
      // Never prompt: this runs unattended inside a build. A password prompt with no tty hangs forever.
      '-o', 'BatchMode=yes',
      '-o', 'StrictHostKeyChecking=accept-new',
      '-o', 'ConnectTimeout=10',
    ];
  }

  /**
   * Wrap a command for the remote LOGIN shell.
   *
   * 🔴 Two things here are load-bearing and both were got wrong first:
   *
   * **`-l` (a login shell).** Without the profile a Homebrew or pkg-installed `dotnet` is not on PATH at
   * all, and the failure reads as "this Mac has no .NET" rather than "this shell cannot see it".
   *
   * **Single quotes, not `JSON.stringify`.** ssh concatenates its arguments and hands the result to the
   * remote login shell, which expands `$VAR` itself BEFORE `bash -lc` ever runs. Double-quoted, a command
   * mentioning `$HOME` is expanded against the outer shell's empty environment and silently reads blank.
   */
  private wrap(command: string, cwd?: string): string {
    return `bash -lc ${q(withPipefail(withCwd(command, cwd)))}`;
  }

  sh(command: string, options: TargetRunOptions = {}): RunResult {
    const { cwd, timeoutMs = 30 * 60_000, quiet = false, stream = false } = options;
    const remote = this.wrap(command, cwd);
    if (remote.length > SSH_COMMAND_LIMIT) {
      const out = `shenora: this command is ${remote.length} bytes, past ssh's ${SSH_COMMAND_LIMIT}-byte`
        + ` ceiling — past it the command is truncated and can still report success.\n`;
      if (!quiet) console.error(out.trimEnd());
      return { status: 1, out };
    }
    const r = spawnSync('ssh', [...this.flags, this.spec, remote], {
      encoding: 'utf8',
      timeout: timeoutMs,
      maxBuffer: 64 * 1024 * 1024,
      ...(stream ? { stdio: 'inherit' as const } : {}),
    });
    const why = describeSpawnFailure('ssh', r.error, timeoutMs);
    const out = `${r.stdout ?? ''}${r.stderr ?? ''}${why ? `${why}\n` : ''}`;
    if (!quiet && !stream && out.trim()) console.log(out.trimEnd());
    return { status: r.status ?? 1, out };
  }

  probe(command: string): string {
    const r = this.sh(command, { quiet: true, timeoutMs: 60_000 });
    return r.status === 0 ? r.out.trim() : '';
  }

  // ⚠ Always posix — the target is macOS. Driven from Windows, `path.join` would emit backslashes, and a
  // backslash reaching a remote shell is an escape character rather than a separator.
  join(...parts: string[]): string { return path.posix.join(...parts); }
  basename(p: string): string { return path.posix.basename(p); }
  dirname(p: string): string { return path.posix.dirname(p); }

  // ⚠ These three ask the TARGET's filesystem. `test`/`ls`/`stat` rather than `node:fs`, which would
  // answer about this machine — confidently, and about a path that does not exist here.
  exists(p: string): boolean {
    return this.sh(`test -e ${q(p)}`, { quiet: true, timeoutMs: 60_000 }).status === 0;
  }

  list(directory: string): string[] {
    // `-1` one per line, `-A` so a dotfile is not silently missing. A failure is an empty directory to
    // the caller, which matches the local implementation's catch.
    const out = this.probe(`ls -1A ${q(directory)} 2>/dev/null`);
    return out ? out.split('\n').map((l) => l.trim()).filter(Boolean) : [];
  }

  mtimeMs(p: string): number | null {
    // BSD stat — the target is macOS by definition here. Seconds, so scale to match `fs.statSync`.
    return this.seconds(`stat -f %m ${q(p)} 2>/dev/null`);
  }

  newestMtimeMs(p: string): number | null {
    // ⚠ ONE round trip, deliberately: statting a bundle's files individually over ssh would be thousands
    // of connections. `-exec … +` batches, and `tail -1` of a numeric sort is the newest.
    return this.seconds(
      `find ${q(p)} -exec stat -f %m {} + 2>/dev/null | sort -n | tail -1`);
  }

  private seconds(command: string): number | null {
    const out = this.probe(command);
    const value = Number(out);
    return out && Number.isFinite(value) ? value * 1000 : null;
  }

  push(localPath: string, targetPath: string): boolean {
    return this.scp(localPath, `${this.spec}:${targetPath}`);
  }

  pull(targetPath: string, localPath: string): boolean {
    return this.scp(`${this.spec}:${targetPath}`, localPath);
  }

  private scp(from: string, to: string): boolean {
    // `-r` so a directory works too: a built `.app` IS a directory, and pulling one is the normal case.
    const r = spawnSync('scp', ['-r', ...this.flags, from, to], {
      encoding: 'utf8',
      timeout: 10 * 60_000,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (r.status !== 0) {
      const why = describeSpawnFailure('scp', r.error, 10 * 60_000);
      console.error(`${r.stderr ?? ''}${why}`.trim() || 'shenora: the copy failed.');
    }
    return r.status === 0;
  }

  /**
   * Run a script inside the Mac's LOGIN SESSION, where the keychain is reachable.
   *
   * 🔴 This exists for exactly one reason: **`codesign` cannot use a login-keychain key from an ssh
   * session.** An ssh login is a different audit session, so signing dies with `errSecInternalComponent` —
   * proven in this family by signing a copy of `/bin/echo`, which has nothing to do with any project and
   * failed identically. `osascript` asks Terminal.app to run the script, Terminal is already in the user's
   * Aqua session, and the keychain opens.
   *
   * Its output CANNOT be streamed back — the script is detached in another session — so completion is a
   * marker file this polls for, and the log is read afterwards.
   */
  gui(script: string, options: GuiRunOptions): RunResult {
    const { tag, timeoutMs = GUI_DEFAULT_TIMEOUT_MS } = options;
    const base = `/tmp/shenora-gui-${tag}`;
    const sh = `${base}.sh`;
    const done = `${base}.done`;
    const log = `${base}.log`;

    if (!this.write(sh, guiScript(script, { done, log })))
      return { status: 1, out: 'shenora: could not stage the GUI script.\n' };

    const launched = this.sh(
      `chmod +x ${q(sh)}; osascript -e 'tell application "Terminal" to do script "bash ${sh}"'`,
      { quiet: true, timeoutMs: 60_000 },
    );
    if (launched.status !== 0) {
      return {
        status: 1,
        out: `${launched.out}shenora: could not start a Terminal session on ${this.label}.\n`
          + `  The Mac must be logged in at its screen — a locked or logged-out Mac has no GUI session,\n`
          + `  and signing has nowhere to run.\n`,
      };
    }

    const deadline = Date.now() + timeoutMs;
    for (;;) {
      if (this.exists(done)) break;
      if (Date.now() >= deadline) {
        const partial = this.probe(`cat ${q(log)} 2>/dev/null | tail -40`);
        return {
          status: 1,
          out: `${partial}\nshenora: the ${tag} step did not finish within`
            + ` ${Math.round(timeoutMs / 60_000)} minutes.\n`,
        };
      }
      sleepSync(GUI_POLL_MS);
    }

    const status = Number(this.probe(`cat ${q(done)}`).trim());
    const out = this.probe(`cat ${q(log)} 2>/dev/null`);
    return { status: Number.isFinite(status) ? status : 1, out: out ? `${out}\n` : '' };
  }

  /**
   * Write a file on the target from a string.
   *
   * ⚠ Via {@link push} rather than a heredoc, because a script big enough to be interesting is also big
   * enough to meet {@link SSH_COMMAND_LIMIT} — which truncates and reports success.
   */
  private write(targetPath: string, content: string): boolean {
    const tmp = path.join(os.tmpdir(), `shenora-stage-${path.basename(targetPath)}`);
    try {
      fs.writeFileSync(tmp, content, 'utf8');
    } catch {
      return false;
    }
    try {
      return this.push(tmp, targetPath);
    } finally {
      fs.rmSync(tmp, { force: true });
    }
  }

  close(): void {
    // One-shot connections only; nothing is held open.
    //
    // ⚠ A persistent `bash -l -s` worker is the obvious next optimisation — a fresh connection costs
    // ~1.8 s and a doctor run makes ten — and it is deliberately NOT here. Its donor shipped a frame
    // parser that hardcoded a zero exit status, so every failure sent over the worker reported SUCCESS
    // while the same command over a one-shot connection failed correctly. That is the worst possible
    // bug in a build tool, and it is worth several seconds not to have it. Add it with a test that
    // asserts a FAILING command reports failure over both transports.
  }
}

/** Is a host configured at all? A plain `{host: ''}` is not one. */
export const hasHost = (h: RemoteHost | null | undefined): h is RemoteHost => Boolean(h?.host?.trim());
