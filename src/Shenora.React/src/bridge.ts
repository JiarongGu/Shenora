import { ShenoraError } from './errors.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import { randomId } from './internal.js';
import { createHostTransport, type ShenoraTransport } from './transport.js';
import {
  HANDSHAKE_MODULE,
  HANDSHAKE_TYPE,
  IpcCategories,
  IpcErrorCodes,
  type IpcError,
  type IpcNotificationBatch,
  type IpcRequest,
  type IpcResponse,
  type ShellInfo,
} from './types.js';

/** Inputs for {@link ShenoraBridge}. */
export interface ShenoraBridgeOptions {
  /**
   * The channel to the host. Default: whichever Shenora host this page is in — WebView2 postMessage
   * on the desktop shell, `HybridWebView` on the MAUI shell — else null (plain browser). Supply your
   * own for another shell, or a scripted fake for tests and preview harnesses.
   */
  transport?: ShenoraTransport | null;

  /** The event bus host notifications are unbundled into. Default: the shared bus. */
  eventBus?: ShenoraEventBus;

  /** Per-request timeout in ms when the call doesn't set one. Family default: 30 000. */
  defaultTimeoutMs?: number;

  /**
   * Pure-UI development seam: answers requests when NO transport exists (a plain browser tab).
   * Return the response data, or a promise; throw to reject.
   *
   * ⚠ Gate the call site with `import.meta.env.DEV` so production stays hard-failing.
   */
  fallback?: (request: IpcRequest) => unknown;

  /**
   * Where a FAILED {@link ShenoraBridge.post} is reported. Default: `console.error`.
   *
   * ⚠ Route it into the app's logger or toast. A one-way send has no promise to reject, so its
   * failures are otherwise invisible.
   */
  onPostError?: (error: PostFailure) => void;

  /**
   * How many unawaited {@link ShenoraBridge.post} ids to remember for error reporting. Default 256.
   * Capped drop-oldest, so a host that never answers cannot grow the set without bound; evicting an
   * id only loses its error report.
   */
  maxTrackedPosts?: number;
}

/** A one-way {@link ShenoraBridge.post} whose host handler answered with a failure. */
export interface PostFailure {
  module: string;
  type: string;
  /** The request id, so it can be tied to a host log line. */
  id: string;
  error: IpcError;
}

/** Per-call inputs for {@link ShenoraBridge.invoke}. */
export interface InvokeOptions<TPayload = unknown> {
  payload?: TPayload;
  /** Optional app-defined routing scope. */
  scope?: string;
  /** Overrides the bridge's default timeout. */
  timeoutMs?: number;
}

/** Per-call inputs for {@link ShenoraBridge.post}. */
export interface PostOptions<TPayload = unknown> {
  payload?: TPayload;
  /** Optional app-defined routing scope. */
  scope?: string;
}

interface PendingRequest {
  resolve: (data: unknown) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const newId = (): string => randomId();

/** A promise-like: only these need racing against a timeout — a plain value has already settled. */
const isThenable = (value: unknown): value is PromiseLike<unknown> =>
  typeof value === 'object' && value !== null
  && typeof (value as { then?: unknown }).then === 'function';

/**
 * The client side of the Shenora IPC contract: correlated request/response over a pluggable
 * transport, category routing of host messages (`ipc` → resolve the pending call, `notification` →
 * unbundle the batch into the event bus), per-request timeout, the ready handshake, and a browser
 * fallback seam for pure-UI development.
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
  // Ids of one-way sends, kept ONLY so a failed response can be reported instead of vanishing.
  // A Map for its insertion-ordered keys, so the cap can evict the oldest.
  private readonly unawaited = new Map<string, { module: string; type: string }>();
  private readonly maxTrackedPosts: number;
  private readonly onPostError: (failure: PostFailure) => void;
  private readonly unsubscribe?: () => void;
  private disposed = false;
  private shellInfo: ShellInfo | undefined;

  constructor(options: ShenoraBridgeOptions = {}) {
    this.transport = options.transport !== undefined ? options.transport : createHostTransport();
    this.eventBus = options.eventBus ?? defaultEventBus;
    this.defaultTimeoutMs = options.defaultTimeoutMs ?? 30_000;
    this.fallback = options.fallback;
    this.maxTrackedPosts = options.maxTrackedPosts ?? 256;
    this.onPostError = options.onPostError
      ?? ((failure) => console.error(
        `[shenora] ${failure.module}.${failure.type} (post) failed: ${failure.error.code}`,
        failure.error,
      ));
    this.unsubscribe = this.transport?.subscribe((message) => this.onHostMessage(message));
  }

  /**
   * True when this bridge can actually send: a transport to a host exists AND the bridge has not been
   * disposed. The check to make on a reference that may have outlived a {@link configureBridge} swap.
   */
  get isAvailable(): boolean {
    return !this.disposed && this.transport !== null;
  }

  /**
   * Send a request and await its typed response data. A failed response rejects with the
   * structured {@link ShenoraError} (code + parameters); no response within the timeout
   * rejects with code `TIMEOUT`; no transport and no fallback rejects with `NO_TRANSPORT`.
   */
  invoke<TData = unknown, TPayload = unknown>(
    module: string,
    type: string,
    options: InvokeOptions<TPayload> = {},
  ): Promise<TData> {
    if (this.disposed) {
      // Fail fast: the transport subscription is gone, so a response could never correlate and the
      // call would otherwise burn the full timeout.
      return Promise.reject(new ShenoraError({
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

    const timeoutMs = options.timeoutMs ?? this.defaultTimeoutMs;

    if (!this.transport) {
      if (this.fallback) {
        let result: unknown;
        try {
          result = this.fallback(request as IpcRequest);
        } catch (error) {
          return Promise.reject(error);
        }
        // An async fallback is raced against the timeout too, or one that never settles hangs the
        // caller forever. Only a thenable needs racing; a plain value has already settled.
        if (!isThenable(result)) return Promise.resolve(result as TData);
        // ⚠ The loser's timer is cleared in `finally` — otherwise every call holds a live timer, and
        // its closure, for the full timeout.
        let timer: ReturnType<typeof setTimeout> | undefined;
        return Promise.race([
          Promise.resolve(result) as Promise<TData>,
          new Promise<TData>((_, reject) => {
            timer = setTimeout(() => reject(new ShenoraError({
              code: IpcErrorCodes.timeout,
              message: `${module}.${type} timed out after ${timeoutMs} ms (in the configured fallback).`,
              parameters: { module, type },
            })), timeoutMs);
          }),
        ]).finally(() => clearTimeout(timer));
      }
      return Promise.reject(new ShenoraError({
        code: IpcErrorCodes.noTransport,
        message: `No transport for ${module}.${type} — not inside a Shenora host, and no fallback is configured.`,
      }));
    }

    return new Promise<TData>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(request.id);
        reject(new ShenoraError({
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
   * Send WITHOUT awaiting a reply, and return the request id.
   *
   * This is the default shape for a desktop shell and {@link invoke} is the special case (D23):
   * reserve `invoke` for calls that are quick AND safe on the host's UI thread, and post everything
   * else, streaming results back as notifications.
   *
   * A failed response is reported through `onPostError` (default `console.error`) rather than
   * dropped. The id is remembered for that — see {@link ShenoraBridgeOptions.maxTrackedPosts}. No
   * timer is set, so there is no deadline.
   *
   * ⚠ No transport (a plain browser tab) is a silent no-op, and **so is a DISPOSED bridge** — where
   * `invoke` would reject with `NO_TRANSPORT`, this just returns the id, so a stale reference kept
   * across a {@link configureBridge} swap looks like it is still sending. `isAvailable` is the check.
   */
  post<TPayload = unknown>(module: string, type: string, options: PostOptions<TPayload> = {}): string {
    const request: IpcRequest<TPayload> = {
      id: newId(),
      module,
      type,
      scope: options.scope,
      payload: options.payload,
      timestamp: new Date().toISOString(),
    };

    if (this.disposed || !this.transport) return request.id;

    // Remembered ONLY to report a failure, drop-oldest at the cap.
    if (this.unawaited.size >= this.maxTrackedPosts) {
      const oldest = this.unawaited.keys().next();
      if (!oldest.done) this.unawaited.delete(oldest.value);
    }
    this.unawaited.set(request.id, { module, type });

    try {
      this.transport.post(JSON.stringify(request));
    } catch (error) {
      this.unawaited.delete(request.id);
      this.onPostError({
        module,
        type,
        id: request.id,
        error: {
          code: IpcErrorCodes.noTransport,
          message: error instanceof Error ? error.message : String(error),
        },
      });
    }
    return request.id;
  }

  /**
   * The ready handshake: tells the host the page's listeners are attached, which starts
   * notification delivery (events buffered host-side arrive in the first batch). Call once the
   * app shell has subscribed — a reloaded page calls it again on its fresh startup, which is
   * also the host's cue to reset per-page state. No-transport is a silent no-op so browser dev
   * doesn't error.
   *
   * ⚠ Any per-page state YOUR host resets on this handshake races whatever the page sent before it,
   * and in React that is structural rather than bad luck: CHILD effects run before PARENT effects.
   *
   * ⚠ The returned promise REJECTS on a failed handshake (disposed bridge, timeout). Handle it —
   * `void bridge.notifyReady()` turns that into an unhandled rejection, which in a WebView2 page is
   * a silent console error.
   */
  async notifyReady<TPayload = unknown>(payload?: TPayload): Promise<ShellInfo | undefined> {
    if (!this.transport) return undefined;
    // The host answers with what it IS and what it can do, so a page can render one tree on every
    // shell instead of sniffing the platform. A host that says nothing leaves it UNDEFINED (never
    // null — JSON null means absent on this wire) — and absent means "assume nothing", never
    // "assume desktop".
    const shell = await this.invoke<ShellInfo | undefined>(HANDSHAKE_MODULE, HANDSHAKE_TYPE, { payload });
    this.shellInfo = shell && typeof shell === 'object' && typeof shell.name === 'string'
      ? { name: shell.name, capabilities: Array.isArray(shell.capabilities) ? shell.capabilities : [] }
      : undefined;
    return this.shellInfo;
  }

  /**
   * What the host said it was during {@link notifyReady} — undefined before the handshake, or when
   * the host advertised nothing. Cached so components can read it synchronously while rendering; a
   * capability learned after layout is a visible flash.
   */
  get shell(): ShellInfo | undefined {
    return this.shellInfo;
  }

  /** Reject everything in flight and detach from the transport. */
  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.unsubscribe?.();
    for (const [id, entry] of this.pending) {
      clearTimeout(entry.timer);
      entry.reject(new ShenoraError({ code: IpcErrorCodes.noTransport, message: 'Bridge disposed.' }));
      this.pending.delete(id);
    }
    // Pure bookkeeping with nothing to settle — dropped so a disposed bridge holds no references.
    this.unawaited.clear();
  }

  private onHostMessage(message: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(message) as unknown;
    } catch (error) {
      console.error('[shenora] ignored unparseable host message:', error);
      return;
    }

    // ⚠ A literal `null` is VALID JSON and survives the parse, and property access on it throws —
    // out of a transport listener, i.e. an uncaught page error with no caller to catch it. Anything
    // that is not an object simply is not ours.
    if (parsed === null || typeof parsed !== 'object') return;
    const envelope = parsed as { category?: string };

    if (envelope.category === IpcCategories.ipc) {
      const response = parsed as IpcResponse;
      if (typeof response.id !== 'string') return;
      const entry = this.pending.get(response.id);
      if (!entry) {
        // No pending call. Either this answers a one-way `post`, whose FAILURE must be surfaced
        // because there is no promise to reject, or it is a late/foreign response, which stays
        // ignored.
        const posted = this.unawaited.get(response.id);
        if (posted) {
          this.unawaited.delete(response.id);
          if (!response.success) {
            this.onPostError({
              module: posted.module,
              type: posted.type,
              id: response.id,
              error: response.error ?? { code: IpcErrorCodes.unknownError },
            });
          }
        }
        return;
      }
      this.pending.delete(response.id);
      clearTimeout(entry.timer);
      if (response.success) {
        entry.resolve(response.data);
      } else {
        entry.reject(new ShenoraError(response.error ?? { code: IpcErrorCodes.unknownError }));
      }
      return;
    }

    if (envelope.category === IpcCategories.notification) {
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
