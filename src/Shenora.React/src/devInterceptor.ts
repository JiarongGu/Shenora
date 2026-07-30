/**
 * Dev-only IPC + event-hub interceptor, ported from the primary desktop sibling (NEVER ship it
 * in prod — gate the single call site with `import.meta.env.DEV`).
 *
 * Why: during desktop-app testing the agent drives the UI over CDP, but native dialogs and
 * event-driven flows can't be exercised by clicking. This wraps the bridge's `invoke` (the IPC
 * seam) and the event bus's `emit` (the event hub) to (1) record + console.debug every
 * request/response/event into ring buffers, and (2) expose a window global so a CDP eval can
 * invoke ANY IPC directly and await events:
 *
 *   window.__shenora.call('NOTES', 'ADD', { title: 'x' })   // drive an IPC, bypass the UI
 *   window.__shenora.waitEvent('NOTES', 'ADDED')            // resolves on the next emit
 *   window.__shenora.recentIpc(20) / .recentEvents(20)     // inspect traffic
 */
import { getBridge, type InvokeOptions, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import type { EventMessage } from './types.js';

/** One recorded IPC call. */
export interface DevIpcEntry {
  t: number;
  module: string;
  type: string;
  payload?: unknown;
  ms?: number;
  ok?: boolean;
  error?: string;
  result?: unknown;
}

/** One recorded event emit. */
export interface DevEventEntry {
  t: number;
  module: string;
  type: string;
  payload?: unknown;
}

/** Inputs for {@link installDevInterceptor}. */
export interface DevInterceptorOptions {
  /** The window global to expose. Default `"__shenora"`. */
  globalName?: string;
  /** Ring-buffer capacity per stream. Default 300. */
  ringSize?: number;
  /** The bridge to wrap. Default: the shared default bridge. */
  bridge?: ShenoraBridge;
  /** The event bus to wrap. Default: the shared bus. */
  bus?: ShenoraEventBus;
}

/**
 * Install the interceptor (idempotent across HMR / StrictMode double-invoke — keyed on the
 * window global).
 */
export function installDevInterceptor(options: DevInterceptorOptions = {}): void {
  if (typeof window === 'undefined') return;
  const globalName = options.globalName ?? '__shenora';
  const w = window as unknown as Record<string, unknown>;
  if (w[globalName]) return;

  const ringSize = options.ringSize ?? 300;
  const bridge = options.bridge ?? getBridge();
  const bus = options.bus ?? defaultEventBus;

  const ipc: DevIpcEntry[] = [];
  const events: DevEventEntry[] = [];
  const push = <T>(buffer: T[], entry: T) => {
    buffer.push(entry);
    if (buffer.length > ringSize) buffer.shift();
  };

  // --- wrap the IPC send seam ---
  const originalInvoke = bridge.invoke.bind(bridge);
  bridge.invoke = <TData = unknown, TPayload = unknown>(
    module: string,
    type: string,
    invokeOptions: InvokeOptions<TPayload> = {},
  ): Promise<TData> => {
    const start = performance.now();
    const entry: DevIpcEntry = { t: Date.now(), module, type, payload: invokeOptions.payload };
    push(ipc, entry);
    console.debug(`[IPC →] ${module}.${type}`, invokeOptions.payload);
    return originalInvoke<TData, TPayload>(module, type, invokeOptions).then(
      (result) => {
        entry.ms = Math.round(performance.now() - start);
        entry.ok = true;
        entry.result = result;
        console.debug(`[IPC ✓] ${module}.${type} (${entry.ms}ms)`, result);
        return result;
      },
      (error: { message?: string }) => {
        entry.ms = Math.round(performance.now() - start);
        entry.ok = false;
        entry.error = error?.message;
        console.debug(`[IPC ✗] ${module}.${type} (${entry.ms}ms)`, error?.message);
        throw error;
      },
    );
  };

  // --- wrap the event hub ---
  const originalEmit = bus.emit.bind(bus);
  bus.emit = (event: EventMessage) => {
    push(events, { t: Date.now(), module: event.module, type: event.type, payload: event.payload });
    console.debug(`[EVT] ${event.module}.${event.type}`, event.payload);
    originalEmit(event);
  };

  w[globalName] = {
    bridge,
    eventBus: bus,
    ipc,
    events,
    /** Drive ANY IPC directly (bypasses native dialogs / UI). Returns the response promise. */
    call: (module: string, type: string, payload?: unknown, scope?: string) =>
      bridge.invoke(module, type, { payload, scope }),
    /** Resolve on the next matching event (or null on timeout) — for CDP awaitPromise verification. */
    waitEvent: (module: string, type: string, timeoutMs = 8000) =>
      new Promise((resolve) => {
        const off = bus.subscribe(module, type, (event) => {
          off();
          resolve(event);
        });
        setTimeout(() => {
          off();
          resolve(null);
        }, timeoutMs);
      }),
    recentIpc: (n = 20) => ipc.slice(-n),
    recentEvents: (n = 20) => events.slice(-n),
    clear: () => {
      ipc.length = 0;
      events.length = 0;
    },
  };
  console.info(`[shenora] dev interceptor installed → window.${globalName} (call(), waitEvent(), recentIpc(), recentEvents())`);
}
