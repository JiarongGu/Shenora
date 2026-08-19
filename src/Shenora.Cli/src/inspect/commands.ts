// The operator's half: start the service, see who checked in, run something over there.
import { argValue, fail } from '../exec.js';
import type { DeployConfig } from '../config.js';
import { resolveTarget } from '../remote/host.js';
import { createInspectService, lanAddresses, INSPECT_DEFAULT_PORT, type InspectResult } from './service.js';
import { inspectPage } from './page.js';

/** Flags that consume the token after them, so {@link positionals} does not read a value as a word. */
const VALUED_FLAGS = new Set(['--port', '--device', '--host', '--app']);

/**
 * The words that are not flags — an expression to evaluate, or a command to run.
 *
 * ⚠ A valued flag swallows the token after it. Without that, `inspect eval --port 7699 location.href`
 * would evaluate `7699 location.href` — a syntax error whose message says nothing about the real
 * mistake, which was that the port number was read as part of the expression.
 */
export function positionals(args: readonly string[]): string[] {
  const out: string[] = [];
  for (let i = 0; i < args.length; i++) {
    const a = args[i]!;
    if (VALUED_FLAGS.has(a)) { i++; continue; }
    if (a.startsWith('--')) continue;
    out.push(a);
  }
  return out;
}

const portOf = (args: readonly string[]): number =>
  Number(argValue(args, '--port') ?? process.env.SHENORA_INSPECT_PORT ?? INSPECT_DEFAULT_PORT) || INSPECT_DEFAULT_PORT;

const base = (args: readonly string[]): string => `http://127.0.0.1:${portOf(args)}`;

/**
 * Ask the running service something.
 *
 * ⚠ A connection refusal and a 404 collapse to the same answer deliberately: both mean "no service is
 * answering here", and the remediation is identical. Reporting them apart would ask the user to care
 * about a distinction that changes nothing they do.
 */
async function ask(args: readonly string[], path: string, body?: unknown): Promise<unknown | null> {
  try {
    const res = await fetch(`${base(args)}${path}`, {
      method: body ? 'POST' : 'GET',
      ...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
    });
    if (!res.ok) return null;
    return (await res.json()) as unknown;
  } catch {
    return null;
  }
}

/**
 * Say so when the SHELL rewrote the expression before this process ever saw it.
 *
 * 🔴 **Git Bash converts anything that looks like a POSIX path into a Windows one**, so a regex literal
 * is silently rewritten on its way to argv: `k=>/chrome/i.test(k)` arrives as
 * `k=>C:/Program Files/Git/chrome/i.test(k)`. The device then answers *"missing ) after argument list"* —
 * an error about YOUR expression, describing text you did not type, with nothing pointing at the cause.
 * Measured while proving the inspector inside a WebView2 shell.
 *
 * ⚠ Nothing here can PREVENT it — the damage is done before `node` starts — so it is reported instead.
 * The tell is a Git installation path inside an expression, which nobody types on purpose.
 */
export function warnIfShellRewrote(expression: string): void {
  const rewritten = /[A-Za-z]:[\\/](Program Files[\\/])?Git[\\/]/.exec(expression);
  if (!rewritten) return;
  console.error('\nshenora: your SHELL rewrote this expression before the CLI saw it —');
  console.error(`    ${expression}`);
  console.error('  Git Bash converts anything path-shaped, which mangles a regex literal. Re-run with:');
  console.error('    MSYS_NO_PATHCONV=1 shenora inspect eval "…"');
  console.error('  Evaluating it anyway, so you can see what the device makes of it.\n');
}

function noService(args: readonly string[]): false {
  return fail(
    `no inspect service is answering on port ${portOf(args)}.`,
    '  Start one in another terminal:  shenora inspect serve',
  );
}

/** `shenora inspect serve` — hold the service open and print the URL a phone should open. */
export function cmdInspectServe(cfg: DeployConfig | null, args: readonly string[]): Promise<void> {
  const port = portOf(args);
  const appOrigin = argValue(args, '--app') ?? '';
  const { server } = createInspectService({
    page: () => inspectPage({ appOrigin }),
    // Resolved lazily and QUIETLY: the inspector is useful with no Mac configured at all, so this
    // must not print a refusal at startup for a route nobody may call.
    host: () => (cfg?.remote?.host || process.env.SHENORA_IOS_HOST ? resolveTarget(cfg, args) : null),
  });

  return new Promise<void>((resolve) => {
    server.on('error', (e: NodeJS.ErrnoException) => {
      if (e.code === 'EADDRINUSE') {
        // ⚠ Named rather than left to crash: what is already listening may be ANOTHER repo's inspect
        // service. It answers every route validly, so the session looks fine while driving someone
        // else's devices.
        fail(`port ${port} is already in use — something else is listening.`,
          `  Another inspect service, perhaps. Pick another:  shenora inspect serve --port ${port + 1}`);
      } else {
        fail(`the inspect service could not start — ${e.message}`);
      }
      resolve();
    });

    // 🔴 0.0.0.0, deliberately: the whole point is that a phone on the LAN can reach it. The privileged
    // half is gated per-request on the peer's address, never on the bind address.
    server.listen(port, '0.0.0.0', () => {
      const addresses = lanAddresses();
      console.log(`\nshenora inspect — open this on the device you are diagnosing:\n`);
      if (addresses.length === 0) {
        console.log('  (this machine has no LAN address — a device cannot reach it)');
      }
      for (const a of addresses) console.log(`    http://${a}:${port}/`);
      console.log(`\n  here:  http://127.0.0.1:${port}/`);
      console.log('\n  The device and this machine must be on the same network.');
      console.log('  Ctrl-C to stop.\n');
    });
  });
}

/** `shenora inspect devices` — who has checked in. */
export async function cmdInspectDevices(args: readonly string[]): Promise<boolean> {
  const data = (await ask(args, '/api/inspect/devices')) as { devices?: Array<Record<string, unknown>> } | null;
  if (!data) return noService(args);
  const devices = data.devices ?? [];
  if (devices.length === 0) {
    console.log('\nNo device has checked in yet.');
    console.log('  Open the URL printed by `shenora inspect serve` on the device.');
    return true;
  }
  console.log('');
  for (const d of devices) {
    console.log(`  ${String(d.name).padEnd(18)} ${String(d.address).padEnd(16)} ${d.polls} polls`
      + `  last seen ${String(d.lastSeen).slice(11, 19)}`);
  }
  return true;
}

/** `shenora inspect report [--device X]` — what a device said about itself. */
export async function cmdInspectReport(args: readonly string[]): Promise<boolean> {
  const want = argValue(args, '--device');
  const data = (await ask(args, '/api/inspect/devices')) as
    { devices?: Array<{ name: string; report?: string }> } | null;
  if (!data) return noService(args);
  const devices = (data.devices ?? []).filter((d) => !want || d.name === want);
  if (devices.length === 0) return fail(want ? `no device named "${want}" has checked in.` : 'no devices.');
  for (const d of devices) {
    console.log(`\n── ${d.name}`);
    console.log(d.report ? JSON.stringify(JSON.parse(d.report), null, 2) : '  (checked in, but sent no report)');
  }
  return true;
}

/**
 * `shenora inspect eval <expression>` — run it on the device and print what came back.
 *
 * ⚠ The results cursor is read BEFORE the action is queued. A device polling on a 1.2 s loop can answer
 * before a cursor read that came after would have started watching, and the result is then invisible.
 */
export async function cmdInspectEval(args: readonly string[], expression: string): Promise<boolean> {
  if (!expression.trim()) return fail('nothing to evaluate.', '  shenora inspect eval "location.href"');
  warnIfShellRewrote(expression);
  const device = argValue(args, '--device');

  const before = (await ask(args, '/api/inspect/results')) as { latest?: number } | null;
  if (!before) return noService(args);
  const cursor = before.latest ?? 0;

  const queued = (await ask(args, '/api/inspect/actions',
    { kind: 'eval', payload: expression, ...(device ? { device } : {}) })) as { ok?: boolean } | null;
  if (!queued?.ok) return fail('the inspect service refused the action.');

  const deadline = Date.now() + 15_000;
  for (;;) {
    const data = (await ask(args, `/api/inspect/results?since=${cursor}`)) as { results?: InspectResult[] } | null;
    const hit = data?.results?.find((r) => r.kind === 'eval' && (!device || r.device === device));
    if (hit) {
      console.log(`\n${hit.device}  ${hit.ok ? '' : '(threw) '}${hit.value}`);
      // 🔴 The EXIT CODE has to carry it too. Printing "(threw)" and exiting 0 means
      // `inspect eval … && next-step` runs the next step after a failed probe — the same false success
      // this CLI polices everywhere else, arriving through a diagnostic rather than a build.
      if (!hit.ok) process.exitCode = 1;
      return hit.ok;
    }
    if (Date.now() > deadline) {
      return fail('no device answered within 15s.',
        '  `shenora inspect devices` shows who is polling. A device whose page was closed cannot answer.');
    }
    await new Promise((r) => setTimeout(r, 400));
  }
}

