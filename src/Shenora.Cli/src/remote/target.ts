// Where a command runs. See `docs/design/cli-remote.md` for why this seam exists at all.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { describeSpawnFailure, withPipefail, type RunResult } from '../exec.js';

export interface TargetRunOptions {
  /** Working directory ON THE TARGET. */
  cwd?: string;
  timeoutMs?: number;
  /** Return the output instead of printing it. */
  quiet?: boolean;
  /**
   * Let the command write straight to this terminal instead of being captured.
   *
   * 🔴 For anything that STREAMS. `ios log --device` attaches a console to a relaunching app and its
   * whole value is watching startup happen; captured, it prints nothing until the app exits, which for a
   * console attach is never. A streamed run cannot report its own output — {@link RunResult.out} is
   * empty by design — so nothing may parse it.
   */
  stream?: boolean;
}

export interface GuiRunOptions {
  /** Names the marker files, so two GUI runs cannot collide. Lowercase, no spaces. */
  tag: string;
  timeoutMs?: number;
}

/**
 * A machine that can run commands — this one, or a Mac on the LAN.
 *
 * 🔴 **The filesystem members are here for the same reason `sh` is.** `ios.ts` used to ask `/bin/sh`
 * about Xcode and `node:fs` about the build output in adjacent lines, which is only correct while both
 * are the same machine. Splitting them is most of what makes a remote mode possible, and a call to
 * `fs.existsSync` on a path that lives on the target is now the bug this interface exists to prevent.
 */
export interface Target {
  /** For messages: "this machine", or the host as configured. Never a path or a key. */
  readonly label: string;
  readonly isRemote: boolean;
  /** Run a shell command on the target. */
  sh(command: string, options?: TargetRunOptions): RunResult;
  /** Run and return trimmed stdout, or '' — for probing, where a failure is an ANSWER. */
  probe(command: string): string;
  /**
   * Join path segments the way the TARGET spells them.
   *
   * 🔴 `path.join` is wrong for a remote target and `path.posix.join` is wrong for a local one, so
   * neither can be hardcoded. On Windows `path.join` emits backslashes, which corrupt a path the moment
   * it is interpolated into a command running on the Mac; but a LOCAL target's paths are this machine's,
   * and forcing posix on them yields `C:\dir/file` — which works when passed to `fs`, and does not match
   * anything a caller compares it against.
   */
  join(...parts: string[]): string;
  /** The last segment of a target path. */
  basename(p: string): string;
  /** Everything but the last segment of a target path. */
  dirname(p: string): string;
  exists(path: string): boolean;
  /** Directory entry names, or [] when the directory is absent or unreadable. */
  list(directory: string): string[];
  /** Modification time in epoch ms, or null when it cannot be read. */
  mtimeMs(path: string): number | null;
  /** Copy a local file TO the target. */
  push(localPath: string, targetPath: string): boolean;
  /** Copy a file FROM the target to here. */
  pull(targetPath: string, localPath: string): boolean;
  /**
   * Run a script where the LOGIN KEYCHAIN is reachable — the only way to codesign.
   * @see docs/design/cli-remote.md
   */
  gui(script: string, options: GuiRunOptions): RunResult;
  /** Release any held connection. Safe to call twice. */
  close(): void;
}

/** Quote one argument for a `sh -c` string. */
const q = (s: string): string => `'${String(s).replace(/'/g, `'\\''`)}'`;

/**
 * Prefix a command with a `cd`, for a target whose shell has no `cwd` option.
 *
 * ⚠ `&&`, not `;` — with a semicolon a failed `cd` runs the command in the HOME directory instead, so a
 * mistyped path builds whatever happens to be there and reports honestly on the wrong tree.
 */
export function withCwd(command: string, cwd?: string): string {
  return cwd ? `cd ${q(cwd)} && ${command}` : command;
}

/** This machine. Every method is what `ios.ts` did inline before the seam existed. */
export class LocalTarget implements Target {
  readonly label = 'this machine';
  readonly isRemote = false;

  sh(command: string, options: TargetRunOptions = {}): RunResult {
    const { cwd, timeoutMs = 30 * 60_000, quiet = false, stream = false } = options;
    const r = spawnSync('/bin/sh', ['-c', withPipefail(command)], {
      cwd,
      encoding: 'utf8',
      timeout: timeoutMs,
      maxBuffer: 64 * 1024 * 1024,
      ...(stream ? { stdio: 'inherit' as const } : {}),
    });
    const why = describeSpawnFailure('/bin/sh', r.error, timeoutMs);
    const out = `${r.stdout ?? ''}${r.stderr ?? ''}${why ? `${why}\n` : ''}`;
    if (!quiet && !stream && out.trim()) console.log(out.trimEnd());
    return { status: r.status ?? 1, out };
  }

  probe(command: string): string {
    const r = spawnSync('/bin/sh', ['-c', command], { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
    return r.status === 0 ? (r.stdout ?? '').trim() : '';
  }

  // This machine's own spelling. On the Mac this IS posix; the difference only shows up under the tests,
  // which run on Windows.
  join(...parts: string[]): string { return path.join(...parts); }
  basename(p: string): string { return path.basename(p); }
  dirname(p: string): string { return path.dirname(p); }

  exists(path: string): boolean {
    return fs.existsSync(path);
  }

  list(directory: string): string[] {
    try {
      return fs.readdirSync(directory);
    } catch {
      return [];
    }
  }

  mtimeMs(path: string): number | null {
    try {
      return fs.statSync(path).mtimeMs;
    } catch {
      return null;
    }
  }

  // A local "copy across" is a copy. Both directions are the same operation; they stay separate so the
  // call sites read the same against either target.
  push(localPath: string, targetPath: string): boolean {
    return this.copy(localPath, targetPath);
  }

  pull(targetPath: string, localPath: string): boolean {
    return this.copy(targetPath, localPath);
  }

  private copy(from: string, to: string): boolean {
    try {
      if (from === to) return true;
      // ⚠ NOT `fs.cpSync` — it fail-fasts the whole Node process (0xC0000409, no message) on this
      // family's Windows box. A single file needs none of it anyway.
      fs.copyFileSync(from, to);
      return true;
    } catch {
      return false;
    }
  }

  /** Already in a login session, so there is nothing to hand off to. */
  gui(script: string, options: GuiRunOptions): RunResult {
    return this.sh(script, { timeoutMs: options.timeoutMs });
  }

  close(): void {
    // Nothing held.
  }
}

/** The user's home on THIS machine. Only for local paths — a target's home is `target.probe('echo $HOME')`. */
export const localHome = (): string => os.homedir();
