import { OperationError } from './errors.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import { randomId } from './internal.js';
import { createWebView2Transport, type ShenoraTransport } from './transport.js';
import {
  HANDSHAKE_MODULE,
  HANDSHAKE_TYPE,
  IpcCategories,
  IpcErrorCodes,
  type IpcError,
  type IpcNotificationBatch,
  type IpcRequest,
  type IpcResponse,
} from './types.js';

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

  /**
   * Where a FAILED {@link ShenoraBridge.post} is reported. Default: `console.error`.
   *
   * A one-way send has no promise to reject, so without this its failures would be invisible — and an
   * unmatched response is dropped silently by the inbound handler, which is exactly how a feature
   * "just stops working" with nothing to grep for. Route it into the app's logger/toast instead.
   */
  onPostError?: (error: PostFailure) => void;

  /**
   * How many unawaited {@link ShenoraBridge.post} ids to remember for error reporting. Default 256.
   * Capped (drop-oldest) so a host that never answers cannot grow the set without bound — the same
   * shape as the host's own bounded notification queue. Evicting an id only loses its error report.
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
  // Ids of one-way sends, kept ONLY so a failed response can be reported instead of vanishing.
  // Insertion-ordered and capped: a Map is used for its ordered keys, not for the values.
  private readonly unawaited = new Map<string, { module: string; type: string }>();
  private readonly maxTrackedPosts: number;
  private readonly onPostError: (failure: PostFailure) => void;
  private readonly unsubscribe?: () => void;
  private disposed = false;

  constructor(options: ShenoraBridgeOptions = {}) {
    this.transport = options.transport !== undefined ? options.transport : createWebView2Transport();
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
   * disposed. It used to ignore `disposed`, so a stale reference to a bridge that `configureBridge`
   * replaced still reported itself available while every `invoke` on it rejected with `NO_TRANSPORT` —
   * the exact case the disposed check in `invoke` exists for (P5.5 H2).
   */
  get isAvailable(): boolean {
    return !this.disposed && this.transport !== null;
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

    const timeoutMs = options.timeoutMs ?? this.defaultTimeoutMs;

    if (!this.transport) {
      if (this.fallback) {
        let result: unknown;
        try {
          result = this.fallback(request as IpcRequest);
        } catch (error) {
          return Promise.reject(error);
        }
        // A fallback may be async (a scripted preview harness commonly is), and this path used to
        // bypass the timeout entirely — so a fallback that never settled hung the caller forever, with
        // none of the diagnostics the real path gives (P5.5 H2). Only a thenable needs racing; a plain
        // value is already settled.
        if (!isThenable(result)) return Promise.resolve(result as TData);
        return Promise.race([
          Promise.resolve(result) as Promise<TData>,
          new Promise<TData>((_, reject) => {
            setTimeout(() => reject(new OperationError({
              code: IpcErrorCodes.timeout,
              message: `${module}.${type} timed out after ${timeoutMs} ms (in the configured fallback).`,
              parameters: { module, type },
            })), timeoutMs);
          }),
        ]);
      }
      return Promise.reject(new OperationError({
        code: IpcErrorCodes.noTransport,
        message: `No transport for ${module}.${type} — not inside a Shenora host, and no fallback is configured.`,
      }));
    }

    return new Promise<TData>((resolve, reject) => {
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
   * Send WITHOUT awaiting a reply, and return the request id.
   *
   * This is the default shape for a desktop shell, and {@link invoke} is the special case — see
   * `docs/2026-07-31-shenora-oneway-ipc-design.md`. Two reasons: a correlated call carries a deadline
   * (30 s by default) and real work does not; and request/response is UI-THREAD-COUPLED here by
   * design, because the dispatch pipeline preserves the caller's synchronization context so facades
   * can touch the window. Reserve `invoke` for calls that are quick AND safe on the UI thread — the
   * window commands are the model — and post everything else, streaming results back as notifications.
   *
   * ⚠ Posting is only HALF of freeing the UI thread. The host still dispatches on the UI thread
   * whether or not the client awaits, so a handler that does heavy work synchronously stalls the
   * window either way. The other half is the host's: return from the route immediately and stream.
   *
   * Failures are not silent. There is no promise to reject, so a failed response is reported through
   * `onPostError` (default `console.error`) rather than being dropped the way an unmatched response
   * otherwise is. Nothing is queued and no timer is set, so there is nothing to leak and no deadline.
   *
   * No transport (a plain browser tab) is a silent no-op, matching the fire-and-forget contract —
   * unlike `invoke`, there is no caller waiting to be told.
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

    // Remember it ONLY to report a failure. Drop-oldest at the cap so a host that never answers
    // cannot grow this without bound; an evicted id simply loses its error report.
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
   * Ordering used to matter here and no longer does for drop zones: the host cleared the previous
   * page's overlays on this handshake, so a `REGISTER` sent before `READY` was wiped even though it
   * was acked. `DropZoneManager` now clears on DOCUMENT CHANGE instead, which cannot race the client.
   * The general point still stands for any per-page state YOUR host resets here — a reset keyed on
   * the handshake races anything the page sends before it, and in React that is structural rather
   * than a mistake, because CHILD effects run before PARENT effects.
   *
   * The returned promise REJECTS on a failed handshake (disposed bridge, timeout). Handle it —
   * `void bridge.notifyReady()` turns that into an unhandled rejection, which in a WebView2 page is
   * a silent console error.
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
    // Unawaited ids are pure bookkeeping with nothing to settle — drop them so a disposed bridge
    // holds no references (this is the instance `configureBridge` replaces).
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

    // A literal `null` is VALID JSON, so it survives the parse and then `parsed.category` threw a
    // TypeError — out of a transport listener, i.e. an uncaught page error with no caller to catch it
    // (P5.5 H2). Primitives (`"str"`, `123`, `true`) never threw because property access on them just
    // yields undefined; null and only null did. Anything that isn't an object simply isn't ours.
    if (parsed === null || typeof parsed !== 'object') return;
    const envelope = parsed as { category?: string };

    if (envelope.category === IpcCategories.ipc) {
      const response = parsed as IpcResponse;
      if (typeof response.id !== 'string') return;
      const entry = this.pending.get(response.id);
      if (!entry) {
        // No pending call. Either this answers a one-way `post` — in which case a FAILURE must be
        // surfaced, because there is no promise to reject and dropping it here is exactly how a
        // feature "just stops working" with nothing to grep for — or it is a late/foreign response,
        // which stays ignored.
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
        entry.reject(new OperationError(response.error ?? { code: IpcErrorCodes.unknownError }));
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
