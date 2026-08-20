// Running commands: `sh` for POSIX pipelines, `run` for a direct spawn that works on Windows.
import { spawnSync } from 'node:child_process';

export interface RunResult {
  status: number;
  out: string;
}

/** Shell-quote one argument for the `sh -c` strings below. */
export const q = (s: string): string => `'${String(s).replace(/'/g, `'\\''`)}'`;

/**
 * Turn `spawnSync`'s `error` into a line a user can act on, or '' when the process really ran.
 *
 * 🔴 **Without this the two ways a command can fail to RUN are invisible.** `spawnSync` leaves
 * `stdout`/`stderr` empty and `status` null when it never started the process, so the caller's own guess
 * is all the user sees — a missing `dotnet` gets reported as whatever a non-zero exit was assumed to mean.
 *
 * ⚠ **The TIMEOUT case is the sharper one**: unnamed, the CLI stops after 30 minutes and says nothing
 * about why.
 */
export function describeSpawnFailure(
  file: string,
  error: (Error & { code?: string }) | undefined,
  timeoutMs: number,
): string {
  if (!error) return '';
  if (error.code === 'ENOENT') {
    return `shenora: '${file}' was not found on PATH — install it, or add it to PATH for this shell.`;
  }
  // ⚠ `ETIMEDOUT` on the ERROR is the whole test, and the obvious second guess is wrong: on a timeout Node
  // sets `signal: 'SIGTERM'` on the RESULT and leaves the error's own keys as errno/code/syscall/path/
  // spawnargs, so an `error.signal` check reads undefined every time.
  if (error.code === 'ETIMEDOUT') {
    return `shenora: '${file}' did not finish within ${Math.round(timeoutMs / 1000)}s and was stopped.`;
  }
  return `shenora: '${file}' could not be run — ${error.message}`;
}

/**
 * 🔴 `set -o pipefail` is prepended to anything containing a pipe. Without it a pipeline reports the LAST
 * command's status — `| tail` is always 0 — so a REJECTED install sails through, the launch runs against
 * an app that was never installed, and the tool finishes by printing "running on the device".
 */
export function withPipefail(command: string): string {
  return command.includes('|') ? `set -o pipefail\n${command}` : command;
}

/** Run a shell command and return its status and combined output. */
export function sh(
  command: string,
  { cwd, timeoutMs = 30 * 60_000, quiet = false }: { cwd?: string; timeoutMs?: number; quiet?: boolean } = {},
): RunResult {
  const r = spawnSync('/bin/sh', ['-c', withPipefail(command)], {
    cwd,
    encoding: 'utf8',
    timeout: timeoutMs,
    maxBuffer: 64 * 1024 * 1024,
  });
  // Appended, never substituted: whatever the shell managed to say before dying is still evidence.
  const why = describeSpawnFailure('/bin/sh', r.error, timeoutMs);
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}${why ? `${why}\n` : ''}`;
  if (!quiet && out.trim()) console.log(out.trimEnd());
  return { status: r.status ?? 1, out };
}

/**
 * Run a program DIRECTLY — no shell, no quoting, no pipes.
 *
 * 🔴 **This exists because the Android half has to run on WINDOWS**, where {@link sh}'s `/bin/sh` is not
 * there. With no pipeline nothing can convert a failure into `tail`'s exit code either, so
 * {@link withPipefail} has nothing to do here.
 */
export function run(
  file: string,
  args: readonly string[],
  { cwd, env, quiet = false, timeoutMs = 30 * 60_000 }:
    { cwd?: string; env?: NodeJS.ProcessEnv; quiet?: boolean; timeoutMs?: number } = {},
): RunResult {
  // 🔴 NO `shell: true`, EVER. With a shell, spawnSync does not escape an args array, it CONCATENATES it
  // (Node warns DEP0190) — so a device serial or project path containing a space becomes two arguments,
  // and one containing `&` becomes a second command. `adb`/`dotnet` are real executables on Windows, so
  // PATH resolution finds them without one; a `.cmd` shim would need explicit handling.
  // ⚠ A TIMEOUT, because `adb` is the one tool here that hangs rather than failing — a device in
  // `offline`/`unauthorized`, or an `adb server` losing its socket, blocks forever with no output, and the
  // user cannot tell that apart from a slow build.
  const r = spawnSync(file, [...args], {
    cwd,
    env: env ? { ...process.env, ...env } : process.env,
    encoding: 'utf8',
    timeout: timeoutMs,
    maxBuffer: 64 * 1024 * 1024,
  });
  const why = describeSpawnFailure(file, r.error, timeoutMs);
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}${why ? `${why}\n` : ''}`;
  if (!quiet && out.trim()) console.log(out.trimEnd());
  return { status: r.status ?? 1, out };
}

/** {@link run}, but the output is returned rather than printed — the caller filters it. */
export function captureRun(file: string, args: readonly string[], env?: NodeJS.ProcessEnv): RunResult {
  return run(file, args, { env, quiet: true });
}

/** Run and return trimmed stdout, or '' — for probing, where a failure is an ANSWER rather than an error. */
export function probe(command: string): string {
  const r = spawnSync('/bin/sh', ['-c', command], { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
  return r.status === 0 ? (r.stdout ?? '').trim() : '';
}

/** Report a failure and set a non-zero exit code. Returns false so callers can `return fail(...)`. */
export function fail(message: string, hint?: string): false {
  console.error(`\nshenora: ${message}`);
  if (hint) console.error(hint);
  process.exitCode = 1;
  return false;
}

/**
 * `--flag value` lookup. Returns undefined when the flag is absent, ends the argument list, or is
 * followed by ANOTHER FLAG.
 *
 * 🔴 Every flag here is optional-valued — `--device` alone means "the only one attached", `--simulator`
 * alone means "whatever is booted" — so without that last case `ios log --device -n 700` reads the device
 * name as `-n` and refuses with *"no connected device matches \"-n\""*: precise, confident, and not the
 * user's fault. A value that genuinely begins with `-` goes after `--`.
 */
export function argValue(args: readonly string[], flag: string): string | undefined {
  const i = args.indexOf(flag);
  if (i < 0 || i + 1 >= args.length) return undefined;
  const next = args[i + 1]!;
  return next.startsWith('-') ? undefined : next;
}

/**
 * Anything after a bare `--` is passed straight to `dotnet build`.
 *
 * 🔴 A COMMAND-LINE FLAG AND NOT A CONFIG FIELD. The case that forced it is an Xcode whose version the
 * installed .NET-for-iOS workload refuses, cleared with `-p:ValidateXcodeVersion=false
 * -p:MtouchLink=SdkOnly`. **Which Xcode a machine has is a fact about THAT MACHINE**, so writing the
 * override into a committed file silences the mismatch for everyone who clones the repo — including
 * whoever hits it when it is the real problem.
 */
export function splitArgs(args: readonly string[]): { own: string[]; passthrough: string[] } {
  const i = args.indexOf('--');
  if (i < 0) return { own: [...args], passthrough: [] };
  // ⚠ `own` MUST stop at the separator. `argValue` scans for a flag and takes the next token, so with a
  // single flat array `deploy --simulator -- -p:Foo=1` reads the simulator's NAME as `-p:Foo=1` and tries
  // to boot a device by that name.
  //
  // 🔴 AN ARRAY, not a joined string. Joining throws the user's own argument boundaries away: a value
  // containing a space (`-p:Foo=a b`, or any path with one) arrives as ONE argv entry, is flattened into
  // the command line, and the shell re-splits it into two.
  return { own: args.slice(0, i), passthrough: [...args.slice(i + 1)] };
}

/**
 * The passthrough as a fragment for a `sh` command line — each argument quoted SEPARATELY so its
 * boundaries survive the shell. Only the iOS half needs this.
 */
export function shellPassthrough(passthrough: readonly string[]): string {
  return passthrough.length ? ` ${passthrough.map(q).join(' ')}` : '';
}
