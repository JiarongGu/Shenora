// The diag service: a queue the DEVICE drains, and an operator half that never leaves this box.
//
// 🔴 Why a separate service rather than a route in the app: it runs arbitrary JS in whatever page polls
// it, which must never be reachable in a product binary — and a diagnostic hosted inside the thing being
// diagnosed dies with it, exactly when you need it. See `docs/design/cli-remote.md`.
import http from 'node:http';
import os from 'node:os';
import type { Target } from '../remote/target.js';

export const DIAG_DEFAULT_PORT = 7699;

/** Caps. A result is text, not a payload — anything larger is a mistake, not a big report. */
const MAX_ACTIONS = 200;
const MAX_RESULTS = 400;
const MAX_DEVICES = 32;
const MAX_BODY_BYTES = 1_000_000;
const MAX_LABEL = 120;

export interface DiagAction {
  seq: number;
  kind: 'eval' | 'reload' | 'report';
  payload: string;
  /** Which device should run it, or undefined for all of them. */
  device?: string;
}

export interface DiagResult {
  seq: number;
  device: string;
  kind: string;
  ok: boolean;
  value: string;
  at: string;
}

export interface DiagDevice {
  name: string;
  /** The LAN address, taken from the SOCKET — the one fact a device cannot report about itself. */
  address: string;
  polls: number;
  lastSeen: string;
  report?: string;
}

/**
 * 🔴 **The trust boundary, and it is a socket fact.** `remoteAddress` cannot be set by a client;
 * `X-Forwarded-For` can, so it is never consulted. Everything that DECIDES WHAT RUNS — queueing an
 * action, reading results, running a command on the Mac — is gated on this.
 */
export function isLoopback(address: string | undefined): boolean {
  if (!address) return false;
  return address === '127.0.0.1' || address === '::1' || address === '::ffff:127.0.0.1';
}

/** Strip the IPv4-mapped-IPv6 prefix so an address reads the way the user's router shows it. */
export function plainAddress(address: string | undefined): string {
  const ip = address ?? '';
  return ip.startsWith('::ffff:') ? ip.slice(7) : ip;
}

/** Every IPv4 address a device on the LAN could reach this machine on. */
export function lanAddresses(): string[] {
  const out: string[] = [];
  for (const entries of Object.values(os.networkInterfaces())) {
    for (const e of entries ?? []) {
      if (e.family === 'IPv4' && !e.internal) out.push(e.address);
    }
  }
  return out;
}

const clamp = (v: unknown, max = MAX_LABEL): string => String(v ?? '').slice(0, max);

/**
 * The queue and the roster.
 *
 * ⚠ **Append-only with a monotonic `seq`; a poll never CONSUMES.** A destructive queue makes a dropped
 * response cost an action rather than a retry, and makes two devices steal each other's work.
 */
export class DiagState {
  private actionSeq = 0;
  private resultSeq = 0;
  readonly actions: DiagAction[] = [];
  readonly results: DiagResult[] = [];
  readonly devices = new Map<string, DiagDevice>();

  queue(kind: DiagAction['kind'], payload: string, device?: string): DiagAction {
    const action: DiagAction = { seq: ++this.actionSeq, kind, payload, ...(device ? { device } : {}) };
    this.actions.push(action);
    while (this.actions.length > MAX_ACTIONS) this.actions.shift();
    return action;
  }

  record(device: string, kind: string, ok: boolean, value: string): DiagResult {
    const result: DiagResult = {
      seq: ++this.resultSeq,
      device: clamp(device),
      kind: clamp(kind, 40),
      ok,
      value: String(value ?? ''),
      at: new Date().toISOString(),
    };
    this.results.push(result);
    while (this.results.length > MAX_RESULTS) this.results.shift();
    return result;
  }

  /** Everything after `since`, plus the head so a caller can advance its cursor past an empty poll. */
  actionsSince(since: number, device?: string): { actions: DiagAction[]; latest: number } {
    const mine = this.actions.filter((a) => a.seq > since && (!a.device || a.device === device));
    return { actions: mine, latest: this.actionSeq };
  }

  resultsSince(since: number): { results: DiagResult[]; latest: number } {
    return { results: this.results.filter((r) => r.seq > since), latest: this.resultSeq };
  }

  /** A poll IS a heartbeat, so "is it still there?" costs no extra request. */
  touch(name: string, address: string, report?: string): DiagDevice {
    const key = clamp(name) || 'unnamed';
    const existing = this.devices.get(key);
    const device: DiagDevice = {
      name: key,
      address,
      polls: (existing?.polls ?? 0) + 1,
      lastSeen: new Date().toISOString(),
      ...(report !== undefined ? { report } : existing?.report !== undefined ? { report: existing.report } : {}),
    };
    this.devices.set(key, device);
    while (this.devices.size > MAX_DEVICES) {
      const oldest = this.devices.keys().next().value;
      if (oldest === undefined) break;
      this.devices.delete(oldest);
    }
    return device;
  }
}

async function readBody(req: http.IncomingMessage): Promise<unknown> {
  return new Promise((resolve) => {
    let size = 0;
    const chunks: Buffer[] = [];
    req.on('data', (c: Buffer) => {
      size += c.length;
      if (size > MAX_BODY_BYTES) {
        req.destroy();
        resolve(null);
        return;
      }
      chunks.push(c);
    });
    // Never throws: a malformed body is an answer of null, not a crashed service.
    req.on('end', () => {
      try {
        resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')) as unknown);
      } catch {
        resolve(null);
      }
    });
    req.on('error', () => resolve(null));
  });
}

export interface DiagServiceOptions {
  state?: DiagState;
  /** The page to serve. Injected so the server module has no opinion about HTML. */
  page: () => string;
  /**
   * The Mac, for `POST /api/diag/host`. A function because resolving one can print, and it should only
   * happen when something asks.
   */
  host?: () => Target | null;
}

/**
 * Build the service. Separate from listening so a test can drive real requests without a fixed port.
 */
export function createDiagService(options: DiagServiceOptions): { server: http.Server; state: DiagState } {
  const state = options.state ?? new DiagState();

  const server = http.createServer((req, res) => {
    void handle(req, res).catch(() => {
      // A handler must never take the service down — the whole point is being up when nothing else is.
      if (!res.headersSent) send(res, 500, { error: 'diag failed' });
      else res.end();
    });
  });

  const send = (res: http.ServerResponse, status: number, body: unknown): void => {
    const text = typeof body === 'string' ? body : JSON.stringify(body);
    res.writeHead(status, {
      'Content-Type': typeof body === 'string' ? 'text/html; charset=utf-8' : 'application/json',
      'Cache-Control': 'no-store',
      // So the channel can also be driven from the app's own origin, which is a different port.
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Headers': 'Content-Type',
    });
    res.end(text);
  };

  async function handle(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    const url = new URL(req.url ?? '/', 'http://diag');
    const route = url.pathname;
    const local = isLoopback(req.socket.remoteAddress);
    const peer = plainAddress(req.socket.remoteAddress);
    const since = Number(url.searchParams.get('since') ?? '0') || 0;

    if (req.method === 'OPTIONS') {
      res.writeHead(204, { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Headers': 'Content-Type' });
      res.end();
      return;
    }

    if (route === '/' || route === '/index.html' || route === '/diag.html') {
      send(res, 200, options.page());
      return;
    }

    // 🔴 The operator half. A privileged request from off-box gets 404, not 403: a route that decides
    // what runs should not confirm it exists to someone who cannot use it.
    const operatorOnly = (): boolean => {
      if (local) return true;
      send(res, 404, { error: 'not found' });
      return false;
    };

    switch (`${req.method} ${route}`) {
      // ── The device's half. Open to the LAN: the device being diagnosed is routinely the one that
      // cannot authenticate, and neither route decides anything.
      case 'POST /api/diag/hello': {
        const body = (await readBody(req)) as { device?: string; report?: unknown } | null;
        const report = body?.report === undefined ? undefined : JSON.stringify(body.report);
        const device = state.touch(clamp(body?.device) || peer, peer, report);
        send(res, 200, { ok: true, device: device.name, latest: state.actionsSince(0).latest });
        return;
      }
      case 'GET /api/diag/actions': {
        const device = clamp(url.searchParams.get('device'));
        if (device) state.touch(device, peer);
        send(res, 200, state.actionsSince(since, device));
        return;
      }
      case 'POST /api/diag/results': {
        const body = (await readBody(req)) as
          { device?: string; kind?: string; ok?: boolean; value?: unknown } | null;
        if (!body) {
          send(res, 400, { error: 'a JSON body is required' });
          return;
        }
        const value = typeof body.value === 'string' ? body.value : JSON.stringify(body.value ?? null);
        state.record(clamp(body.device) || peer, clamp(body.kind, 40) || 'result', body.ok !== false, value);
        send(res, 200, { ok: true });
        return;
      }

      // ── The operator's half. Loopback only.
      case 'POST /api/diag/actions': {
        if (!operatorOnly()) return;
        const body = (await readBody(req)) as
          { kind?: DiagAction['kind']; payload?: string; device?: string } | null;
        if (!body?.payload) {
          send(res, 400, { error: 'payload is required' });
          return;
        }
        const action = state.queue(body.kind ?? 'eval', String(body.payload), clamp(body.device) || undefined);
        send(res, 200, { ok: true, seq: action.seq });
        return;
      }
      case 'GET /api/diag/results': {
        if (!operatorOnly()) return;
        send(res, 200, state.resultsSince(since));
        return;
      }
      case 'GET /api/diag/devices': {
        if (!operatorOnly()) return;
        send(res, 200, { devices: [...state.devices.values()] });
        return;
      }
      case 'POST /api/diag/host': {
        // 🔴 THIS RUNS A COMMAND ON THE MAC. Reachable from the LAN it would be a remote shell for
        // anyone on the same wifi. The loopback gate is the only thing between those two readings, and
        // `service.test.ts` fails if it is ever removed.
        if (!operatorOnly()) return;
        const body = (await readBody(req)) as { command?: string } | null;
        if (!body?.command) {
          send(res, 400, { error: 'command is required' });
          return;
        }
        const target = options.host?.() ?? null;
        if (!target) {
          send(res, 409, { error: 'no Mac is configured — see `shenora ios doctor --host`' });
          return;
        }
        const r = target.sh(String(body.command), { quiet: true, timeoutMs: 5 * 60_000 });
        send(res, 200, { ok: r.status === 0, status: r.status, out: r.out, host: target.label });
        return;
      }
      default:
        send(res, 404, { error: 'not found' });
    }
  }

  return { server, state };
}
