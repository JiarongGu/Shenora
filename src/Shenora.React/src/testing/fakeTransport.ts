import { IpcCategories, type IpcRequest } from '../types.js';
import type { ShenoraTransport } from '../transport.js';

/**
 * The ONE in-memory {@link ShenoraTransport} double for the client suite (P5.5 H7).
 *
 * It replaced four separate `FakeTransport` classes (bridge / hooks / useDropZone / windowCommands)
 * plus two inline `{ post, subscribe }` literals. Every one of them hand-wrote the host's reply
 * envelope — `{ category: 'ipc', id, success }` — as a LITERAL, so all four could have drifted from
 * the wire contract together and stayed green. This builds replies from the exported
 * {@link IpcCategories} constant instead, which is the same value the host's own tests assert
 * against, so the two halves can no longer disagree quietly.
 *
 * The shape is the union of what the four needed, and nothing here is speculative: `posted` parses
 * (three of them wanted requests), `lastRequest()` reads the raw string (one wanted that), and
 * `autoAck` off by default matches three of four.
 */
export class FakeTransport implements ShenoraTransport {
  /** Every message posted, verbatim — the raw wire strings. */
  readonly raw: string[] = [];

  /** Acknowledge each request on a microtask instead of waiting for an explicit reply. */
  autoAck = false;

  private listener?: (message: string) => void;
  private readonly unacked: string[] = [];

  /** @inheritdoc */
  post(message: string): void {
    this.raw.push(message);
    const request = JSON.parse(message) as IpcRequest;
    if (this.autoAck) queueMicrotask(() => this.respond(request.id, undefined));
    else this.unacked.push(request.id);
  }

  /** @inheritdoc */
  subscribe(listener: (message: string) => void): () => void {
    this.listener = listener;
    return () => {
      this.listener = undefined;
    };
  }

  /** True while a listener is attached — proves `dispose()` unsubscribed. */
  get subscribed(): boolean {
    return this.listener !== undefined;
  }

  /** Everything posted, parsed as requests. */
  get posted(): IpcRequest[] {
    return this.raw.map((message) => JSON.parse(message) as IpcRequest);
  }

  /** The most recent request, typed to its payload. */
  lastRequest<T = unknown>(): IpcRequest<T> {
    return JSON.parse(this.raw.at(-1)!) as IpcRequest<T>;
  }

  /** The `type` of every request posted, in order. */
  routes(): string[] {
    return this.posted.map((request) => request.type);
  }

  /** Answer one request successfully. */
  respond(id: string, data: unknown): void {
    this.listener?.(JSON.stringify({ category: IpcCategories.ipc, id, success: true, data }));
  }

  /** Answer the most recent request successfully. */
  respondToLast(data: unknown): void {
    this.respond(this.lastRequest().id, data);
  }

  /** Answer one request with a structured error. */
  fail(id: string, code: string): void {
    this.listener?.(
      JSON.stringify({ category: IpcCategories.ipc, id, success: false, error: { code } }));
  }

  /** Acknowledge everything still outstanding (used with `autoAck = false`). */
  ackAll(): void {
    for (const id of this.unacked.splice(0)) this.respond(id, undefined);
  }

  /**
   * Deliver an arbitrary host→client message. Objects are serialized; a string is passed THROUGH
   * unchanged, which is what lets the inbound-robustness tests feed `'not json'`, `'null'`, `'123'`
   * and friends straight at the listener.
   */
  emitFromHost(message: unknown): void {
    this.listener?.(typeof message === 'string' ? message : JSON.stringify(message));
  }
}
