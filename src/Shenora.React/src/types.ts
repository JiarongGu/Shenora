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
  /**
   * **No MODULE claimed the request** — nothing on the host answers that name. Parameters:
   * `module`, `type`.
   *
   * ⚠ Distinct from {@link noRoute}, and the split is what makes it actionable: this means the module
   * was never registered host-side, while `noRoute` means it WAS and does not know that type. Opposite
   * fixes — wire the module up, versus correct a route name — and until 2026-08-08 both arrived as
   * `NO_HANDLER` with identical parameters, so a dead page could not be diagnosed from the wire.
   */
  noHandler: 'NO_HANDLER',
  /**
   * **The module answered but has no route of that type.** Parameters: `module`, `type`.
   *
   * Seeing this is proof the module IS registered and mapped, which is exactly what {@link noHandler}
   * cannot tell you — so this is a route-name problem, not a composition problem.
   */
  noRoute: 'NO_ROUTE',
  /**
   * A scope-routed module was called without a `scope`. Parameters: `module`.
   *
   * This was MISSING here while the host emitted it (P5.5 H6), so a scoped app could not match it by
   * constant and had to hard-code the string — against documentation claiming the two sides mirror
   * name-for-name. The mirror is now enforced by a test rather than by care.
   */
  scopeRequired: 'SCOPE_REQUIRED',
  missingPayloadValue: 'MISSING_PAYLOAD_VALUE',
  invalidPayloadValue: 'INVALID_PAYLOAD_VALUE',
  /**
   * The operation was cancelled — a NORMAL outcome, not a fault. Treat it as "show nothing": it is the
   * one failure a UI should stay silent about. Previously indistinguishable from `UNKNOWN_ERROR`.
   */
  operationCancelled: 'OPERATION_CANCELLED',
  /**
   * The shell has NO EXPRESSION of what was asked for — not a fault, and not something a retry fixes.
   * Parameters: `capability` (a {@link ShellCapabilities} value).
   *
   * Treat it like `operationCancelled`: do not show a fault. The right response is to hide the control,
   * because the capability is absent by design on that platform (a folder picker on a phone, for
   * instance) rather than broken.
   *
   * ⚠ A page should not normally NEED this. The ready handshake advertises `ShellInfo.capabilities`
   * precisely so one bundle can decide BEFORE it asks — `useFileDialogs().canPickFolder` is the
   * intended path. This is the honest answer when a page asks anyway.
   */
  capabilityNotSupported: 'CAPABILITY_NOT_SUPPORTED',
  /** Client-only: the request timed out waiting for a response. */
  timeout: 'TIMEOUT',
  /** Client-only: no transport is available and no fallback was configured. */
  noTransport: 'NO_TRANSPORT',
} as const;

/**
 * The codes that exist ONLY on the client — they never arrive from the host, but reject through the same
 * structured shape so app error handling stays uniform. Named here so the cross-language mirror check can
 * exclude them by intent rather than by a hard-coded list on the other side.
 */
export const ClientOnlyIpcErrorCodes: readonly string[] = [
  IpcErrorCodes.timeout,
  IpcErrorCodes.noTransport,
];

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

/**
 * What the host is and what it can do — the handshake's response data (mirror of the host's
 * `ShellInfo`).
 *
 * This is what lets ONE page ship to every shell. Render on the data rather than sniffing the
 * platform:
 *
 * ```tsx
 * const shell = useShellInfo();
 * return <>{shell?.capabilities.includes(ShellCapabilities.windowChrome) && <TitleBar />}</>;
 * ```
 *
 * A desktop shell that draws its own chrome advertises `windowChrome` and `dropZones`; a mobile one
 * has neither, and the same bundle renders correctly on both. Undefined means no host said
 * anything — a plain browser tab, or a host predating this — so treat absent as "assume nothing",
 * never as "assume desktop".
 */
export interface ShellInfo {
  /** Short host identifier, for diagnostics (`"winforms"`, `"maui"`). Never branch on this — branch on the capabilities. */
  name: string;
  /** The capabilities this host offers; see {@link ShellCapabilities}. */
  capabilities: string[];
}

/**
 * The well-known capability names, mirroring the host's `ShellCapability` constants. Apps may
 * advertise their own strings too — this is the shared vocabulary, not the whole set.
 */
export const ShellCapabilities = {
  windowChrome: 'windowChrome',
  dropZones: 'dropZones',
  filePicker: 'filePicker',
  folderPicker: 'folderPicker',
  savePicker: 'savePicker',
  secondaryWindows: 'secondaryWindows',
  tray: 'tray',
  /**
   * The host can serve LOCAL FILES to this page — media, images, documents, exports — through its resource
   * interceptor. Pair it with {@link mediaUrl}.
   *
   * A page cannot reach a local file itself on any shell (`file://` is blocked from a virtual-host origin,
   * and would be the wrong answer anyway), so branch on this and fall back rather than rendering a player
   * that can never load.
   *
   * ⚠ It says the host CAN serve, not what: routes, payload shape and allowed roots are the app's. And it
   * deliberately tells you nothing about the URL SCHEME — {@link mediaUrl} is relative precisely so each
   * shell supplies its own, and knowing it would put you back to branching on platform.
   */
  localFiles: 'localFiles',
} as const;

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
