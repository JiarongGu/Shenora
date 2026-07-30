/**
 * The Shenora IPC wire contract — the TS mirror of the `Shenora.Ipc` C# envelopes (names are
 * pinned on both sides; the host serializes camelCase). Transport-neutral: the same envelopes
 * travel over WebView2 postMessage, a WebSocket, or a mobile shell's channel.
 */

/** Values of the `category` discriminator on host→client messages. */
export const IpcCategories = {
  /** A response to a client request. */
  ipc: 'ipc',
  /** A host-pushed notification batch. */
  notification: 'notification',
} as const;

/** Reserved wire route: the ready handshake the host bridge intercepts (mirror of the host consts). */
export const HANDSHAKE_MODULE = 'SHENORA';
/** Reserved wire route: the ready handshake type. */
export const HANDSHAKE_TYPE = 'READY';

/**
 * Error codes with framework-reserved meaning (`errors.{code}` is the family i18n-key
 * convention). `timeout` and `noTransport` are CLIENT-side failures — they never come from the
 * host but reject through the same structured shape so error handling stays uniform.
 */
export const IpcErrorCodes = {
  unknownError: 'UNKNOWN_ERROR',
  noHandler: 'NO_HANDLER',
  missingPayloadValue: 'MISSING_PAYLOAD_VALUE',
  invalidPayloadValue: 'INVALID_PAYLOAD_VALUE',
  /** Client-only: the request timed out waiting for a response. */
  timeout: 'TIMEOUT',
  /** Client-only: no transport is available and no fallback was configured. */
  noTransport: 'NO_TRANSPORT',
} as const;

/** The request envelope a client sends to the host. */
export interface IpcRequest<TPayload = unknown> {
  /** Correlation id, echoed back on the response. */
  id: string;
  /** Routing: the module the request targets (e.g. `"APP"`). */
  module: string;
  /** Routing: the action within the module (e.g. `"GET_ALL"`). */
  type: string;
  /** Optional app-defined routing scope. */
  scope?: string;
  payload?: TPayload;
  /** ISO-8601 send time. */
  timestamp: string;
}

/** The structured error carried by a failed response. `code` is the i18n key (`errors.{code}`). */
export interface IpcError {
  code: string;
  /** Untranslated fallback message for logs/dev; not for end users. */
  message?: string;
  /** Values interpolated into the translated message. */
  parameters?: Record<string, string>;
}

/** The response envelope the host returns for an {@link IpcRequest}. */
export interface IpcResponse<TData = unknown> {
  category: typeof IpcCategories.ipc;
  /** The request id this responds to. */
  id: string;
  success: boolean;
  data?: TData;
  error?: IpcError;
}

/** One host→client event inside an {@link IpcNotificationBatch}. Fire-and-forget. */
export interface IpcNotification<TPayload = unknown> {
  module: string;
  type: string;
  payload?: TPayload;
  scope?: string;
}

/**
 * The host→client push envelope: notifications batched every ~50 ms host-side. Always a batch —
 * a single notification ships as a batch of one; `category` alone discriminates.
 */
export interface IpcNotificationBatch {
  category: typeof IpcCategories.notification;
  id: string;
  payload: IpcNotification[];
  timestamp: string;
}

/**
 * A client-side event on the event bus — an unbundled {@link IpcNotification} (or a locally
 * emitted event; the host-side `EventMessage` additionally carries id/timestamp, which don't
 * cross the wire).
 */
export interface EventMessage<TPayload = unknown> {
  module: string;
  type: string;
  payload?: TPayload;
  scope?: string;
}
