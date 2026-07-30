import { OperationError } from './errors';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus';
import { createWebView2Transport, type ShenoraTransport } from './transport';
import {
  HANDSHAKE_MODULE,
  HANDSHAKE_TYPE,
  IpcCategories,
  IpcErrorCodes,
  type IpcNotificationBatch,
  type IpcRequest,
  type IpcResponse,
} from './types';

/** Inputs for {@link ShenoraBridge}. */
export interface ShenoraBridgeOptions {
  /**
   * The channel to the host. Default: the WebView2 postMessage transport when running inside a
   * host, else null (browser). Supply your own for other shells — a WebSocket, a mobile shell's
   * native channel (D16) — or a scripted fake for tests/preview harnesses.
   */
  transport?: ShenoraTransport | null;

  /** The event bus host notifications are unbundled into. Default: the shared bus. */
  eventBus?: ShenoraEventBus;

  /** Per-request timeout in ms when the call doesn't set one. Family default: 30 000. */
  defaultTimeoutMs?: number;

  /**
   * Pure-UI development seam: answers requests when NO transport exists (plain browser tab —
   * component/layout work without the desktop host). Return the response data (or a promise;
   * throw to reject). Generalized from the source app's hardcoded dev mocks: the mocks are app
   * schema, so the app supplies them — gate with `import.meta.env.DEV` at the call site so
   * production stays hard-failing.
   */
  fallback?: (request: IpcRequest) => unknown;
}

/** Per-call inputs for {@link ShenoraBridge.invoke}. */
export interface InvokeOptions<TPayload = unknown> {
  payload?: TPayload;
  /** Optional app-defined routing scope. */
  scope?: string;
  /** Overrides the bridge's default timeout. */
  timeoutMs?: number;
}

interface PendingRequest {
  resolve: (data: unknown) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const newId = (): string =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    // Ancient-environment fallback — correlation ids only need uniqueness, not entropy.
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;

/**
 * The client side of the Shenora IPC contract, ported from the primary desktop sibling:
 * correlated request/response over a pluggable transport, category routing of host messages
 * (`ipc` → resolve the pending call, `notification` → unbundle the batch into the event bus),
 * per-request timeout, the ready handshake, and a browser fallback seam for pure-UI development.
 *
 * Most apps use the lazy default instance via {@link getBridge}/{@link configureBridge}; create
 * instances directly for tests or multi-transport setups.
 */
export class ShenoraBridge {
  private readonly transport: ShenoraTransport | null;
  private readonly eventBus: ShenoraEventBus;
  private readonly defaultTimeoutMs: number;
  private readonly fallback?: (request: IpcRequest) => unknown;
  private readonly pending = new Map<string, PendingRequest>();
  private readonly unsubscribe?: () => void;
  private disposed = false;

  constructor(options: ShenoraBridgeOptions = {}) {
    this.transport = options.transport !== undefined ? options.transport : createWebView2Transport();
    this.eventBus = options.eventBus ?? defaultEventBus;
    this.defaultTimeoutMs = options.defaultTimeoutMs ?? 30_000;
    this.fallback = options.fallback;
    this.unsubscribe = this.transport?.subscribe((message) => this.onHostMessage(message));
  }

  /** True when a transport to a host exists (false in a plain browser). */
  get isAvailable(): boolean {
    return this.transport !== null;
  }

  /**
   * Send a request and await its typed response data. A failed response rejects with the
   * structured {@link OperationError} (code + parameters); no response within the timeout
   * rejects with code `TIMEOUT`; no transport and no fallback rejects with `NO_TRANSPORT`.
   */
  invoke<TData = unknown, TPayload = unknown>(
    module: string,
    type: string,
    options: InvokeOptions<TPayload> = {},
  ): Promise<TData> {
    if (this.disposed) {
      // Fail fast: the transport subscription is gone, so a response could never correlate —
      // without this the call would burn the full timeout (stale references after
      // configureBridge replaced the default are the typical way here).
      return Promise.reject(new OperationError({
        code: IpcErrorCodes.noTransport,
        message: `Bridge disposed — ${module}.${type} cannot be sent.`,
      }));
    }

    const request: IpcRequest<TPayload> = {
      id: newId(),
      module,
      type,
      scope: options.scope,
      payload: options.payload,
      timestamp: new Date().toISOString(),
    };

    if (!this.transport) {
      if (this.fallback) {
        try {
          return Promise.resolve(this.fallback(request as IpcRequest) as TData);
        } catch (error) {
          return Promise.reject(error);
        }
      }
      return Promise.reject(new OperationError({
        code: IpcErrorCodes.noTransport,
        message: `No transport for ${module}.${type} — not inside a Shenora host, and no fallback is configured.`,
      }));
    }

    return new Promise<TData>((resolve, reject) => {
      const timeoutMs = options.timeoutMs ?? this.defaultTimeoutMs;
      const timer = setTimeout(() => {
        this.pending.delete(request.id);
        reject(new OperationError({
          code: IpcErrorCodes.timeout,
          message: `${module}.${type} timed out after ${timeoutMs} ms.`,
          parameters: { module, type },
        }));
      }, timeoutMs);

      this.pending.set(request.id, {
        resolve: (data) => resolve(data as TData),
        reject,
        timer,
      });

      try {
        this.transport!.post(JSON.stringify(request));
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(request.id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  /**
   * The ready handshake: tells the host the page's listeners are attached, which starts
   * notification delivery (events buffered host-side arrive in the first batch). Call once the
   * app shell has subscribed — a reloaded page calls it again on its fresh startup, which is
   * also the host's cue to reset per-page state. No-transport is a silent no-op so browser dev
   * doesn't error.
   */
  async notifyReady<TPayload = unknown>(payload?: TPayload): Promise<void> {
    if (!this.transport) return;
    await this.invoke(HANDSHAKE_MODULE, HANDSHAKE_TYPE, { payload });
  }

  /** Reject everything in flight and detach from the transport. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribe?.();
    for (const [id, entry] of this.pending) {
      clearTimeout(entry.timer);
      entry.reject(new OperationError({ code: IpcErrorCodes.noTransport, message: 'Bridge disposed.' }));
      this.pending.delete(id);
    }
  }

  private onHostMessage(message: string): void {
    let parsed: { category?: string };
    try {
      parsed = JSON.parse(message) as { category?: string };
    } catch (error) {
      console.error('[shenora] ignored unparseable host message:', error);
      return;
    }

    if (parsed.category === IpcCategories.ipc) {
      const response = parsed as IpcResponse;
      if (typeof response.id !== 'string') return;
      const entry = this.pending.get(response.id);
      if (!entry) return; // timed out (or not ours) — the reject already happened
      this.pending.delete(response.id);
      clearTimeout(entry.timer);
      if (response.success) {
        entry.resolve(response.data);
      } else {
        entry.reject(new OperationError(response.error ?? { code: IpcErrorCodes.unknownError }));
      }
      return;
    }

    if (parsed.category === IpcCategories.notification) {
      // Always a batch (a single notification is a batch of one) — unbundle in order.
      const batch = parsed as IpcNotificationBatch;
      if (!Array.isArray(batch.payload)) return;
      for (const item of batch.payload) {
        if (item && typeof item.module === 'string' && typeof item.type === 'string') {
          this.eventBus.emit({ module: item.module, type: item.type, payload: item.payload, scope: item.scope });
        }
      }
    }
    // Unknown categories: not ours — ignore (forward compatibility).
  }
}

let defaultBridge: ShenoraBridge | undefined;

/** The lazy default bridge (created with default options on first use). */
export function getBridge(): ShenoraBridge {
  return (defaultBridge ??= new ShenoraBridge());
}

/**
 * Create the default bridge with options — call once at startup, before anything uses
 * {@link getBridge}. Calling again (tests, HMR) disposes the previous default first.
 */
export function configureBridge(options: ShenoraBridgeOptions): ShenoraBridge {
  defaultBridge?.dispose();
  defaultBridge = new ShenoraBridge(options);
  return defaultBridge;
}
