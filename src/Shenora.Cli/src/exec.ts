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

/** `--flag value` lookup. Returns undefined when absent or when the flag ends the argument list. */
export function argValue(args: readonly string[], flag: string): string | undefined {
  const i = args.indexOf(flag);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : undefined;
}
