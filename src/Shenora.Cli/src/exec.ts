// Running commands, and the trap that made this worth extracting rather than re-deriving per app.
import { spawnSync } from 'node:child_process';

export interface RunResult {
  status: number;
  out: string;
}

/** Shell-quote one argument for the `sh -c` strings below. */
export const q = (s: string): string => `'${String(s).replace(/'/g, `'\\''`)}'`;

/**
 * 🔴 `set -o pipefail` is prepended to anything containing a pipe, and it is NOT decoration. Without it
 * a pipeline reports the LAST command's status — `| tail` is always 0 — so a REJECTED install sails
 * through, the launch runs against an app that was never installed, and the tool finishes by cheerfully
 * printing "running on the device". Measured on this kit's first real device run, then reintroduced on a
 * second step the same day, which is why it is applied here once instead of remembered per call site.
 *
 * Split out from {@link sh} so it is TESTABLE OFF macOS: `sh` shells out to `/bin/sh`, which does not
 * exist on the Windows box where the gate runs, and a guarantee only asserted on the machine that rarely
 * runs the suite is a guarantee nobody is watching.
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
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}`;
  if (!quiet && out.trim()) console.log(out.trimEnd());
  return { status: r.status ?? 1, out };
}

/**
 * Run a program DIRECTLY — no shell, no quoting, no pipes.
 *
 * 🔴 **This exists because the Android half has to run on WINDOWS**, and {@link sh} shells out to
 * `/bin/sh`, which is not there. Every iOS command is macOS-only by nature (`xcrun`, `codesign`), so a
 * POSIX shell was a free assumption; `adb` and `dotnet` are not, and most .NET Android work happens on
 * Windows.
 *
 * ⚠ It also removes the trap that `sh` needs `set -o pipefail` to survive: there is no pipeline, so
 * nothing can convert a failure into `tail`'s exit code. Where output has to be trimmed, {@link capture}
 * brings it into the tool and the filtering happens here — which is what `adb logcat` requires anyway
 * (its own `-t N` applies to the RAW buffer BEFORE any filterspec, so asking the tool to tail prints
 * nothing once platform chatter has gone by).
 */
export function run(
  file: string,
  args: readonly string[],
  { cwd, env, quiet = false, timeoutMs = 30 * 60_000 }:
    { cwd?: string; env?: NodeJS.ProcessEnv; quiet?: boolean; timeoutMs?: number } = {},
): RunResult {
  // 🔴 NO `shell: true`, EVER — and this was written the other way first, which is worth recording.
  // With a shell, spawnSync does not escape an args array, it CONCATENATES it (Node warns DEP0190), so a
  // device serial or a project path containing a space becomes two arguments and one containing `&`
  // becomes a second command. `adb`/`dotnet` are real executables on Windows, so PATH resolution finds
  // them without one; a `.cmd` shim would need explicit handling rather than a blanket shell.
  // ⚠ THE SAME CEILING {@link sh} HAS, and it was missing here. `adb` is the one tool in this CLI that
  // genuinely hangs rather than failing — a device in `offline`/`unauthorized`, or an `adb server` losing
  // its socket, blocks forever with no output. Without a timeout the CLI has no floor on how long it can
  // sit there, and the user cannot tell it apart from a slow build.
  const r = spawnSync(file, [...args], {
    cwd,
    env: env ? { ...process.env, ...env } : process.env,
    encoding: 'utf8',
    timeout: timeoutMs,
    maxBuffer: 64 * 1024 * 1024,
  });
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}`;
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
 * 🔴 That last case is not defensive tidying. Every flag here is optional-valued — `--device` alone means
 * "the only one attached", `--simulator` alone means "whatever is booted" — so `ios log --device -n 700`
 * read the device name as `-n` and refused with *"no connected device matches \"-n\""*. The user is then
 * told, precisely and confidently, something that is not their fault. Hit 2026-08-09 against a real phone.
 * A value that genuinely begins with `-` has to go after `--`, which is where machine-specific arguments
 * already live.
 */
export function argValue(args: readonly string[], flag: string): string | undefined {
  const i = args.indexOf(flag);
  if (i < 0 || i + 1 >= args.length) return undefined;
  const next = args[i + 1]!;
  return next.startsWith('-') ? undefined : next;
}
