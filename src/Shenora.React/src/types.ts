/**
 * The Shenora IPC wire contract — the TS mirror of the `Shenora.Core.Ipc` C# envelopes (names are
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
   * **No MODULE claimed the request** — nothing host-side answers that name, i.e. the module was never
   * registered. Parameters: `module`, `type`.
   *
   * ⚠ Distinct from {@link noRoute}, and opposite fixes: wire the module up, versus correct a route name.
   */
  noHandler: 'NO_HANDLER',
  /**
   * **The module answered but has no route of that type** — so it IS registered, and this is a
   * route-name problem. Parameters: `module`, `type`.
   */
  noRoute: 'NO_ROUTE',
  /** A scope-routed module was called without a `scope`. Parameters: `module`. */
  scopeRequired: 'SCOPE_REQUIRED',
  missingPayloadValue: 'MISSING_PAYLOAD_VALUE',
  invalidPayloadValue: 'INVALID_PAYLOAD_VALUE',
  /**
   * The operation was cancelled — a NORMAL outcome, not a fault. Treat it as "show nothing": it is the
   * one failure a UI should stay silent about.
   */
  operationCancelled: 'OPERATION_CANCELLED',
  /**
   * The shell has NO EXPRESSION of what was asked for — not a fault, and not something a retry fixes.
   * Parameters: `capability` (a {@link ShellCapabilities} value). Hide the control rather than showing
   * an error: the capability is absent by design on that platform, not broken.
   *
   * ⚠ A page should not normally NEED this. The ready handshake advertises `ShellInfo.capabilities` so
   * one bundle can decide BEFORE it asks — `useFileDialogs().canPickFolder` is the intended path.
   */
  capabilityNotSupported: 'CAPABILITY_NOT_SUPPORTED',
  /** Client-only: the request timed out waiting for a response. */
  timeout: 'TIMEOUT',
  /** Client-only: no transport is available and no fallback was configured. */
  noTransport: 'NO_TRANSPORT',
} as const;

/**
 * The codes that exist ONLY on the client — they never arrive from the host, but reject through the same
 * structured shape. Named here so the cross-language mirror check excludes them by intent.
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
 * `ShellInfo`), and what lets ONE page ship to every shell. Read it with `useShellInfo`, whose docs
 * carry the rule for an absent one.
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
   * The shell has a system BACK gesture this page can take responsibility for — Android only. Absent
   * on iOS and the desktop, which have none.
   *
   * ⚠ Branch on it. Asking to intercept where there is no gesture is accepted and no press ever
   * arrives, which looks exactly like a handler that is broken.
   */
  backNavigation: 'backNavigation',
  /**
   * The host can put a FILE LIST on the clipboard, so the user can paste into Explorer, Finder or a
   * file manager. No web API expresses this, so it is the one part of the clipboard worth branching on.
   *
   * ⚠ It says nothing about the rest: text and bytes work everywhere, and the gesture-driven half is
   * `navigator.clipboard`'s job.
   */
  clipboardFiles: 'clipboardFiles',
  /**
   * The host can serve LOCAL FILES to this page — media, images, documents, exports — through its
   * resource interceptor. Pair it with `mediaUrl`.
   *
   * ⚠ A page cannot reach a local file itself on any shell, so branch on this and fall back rather than
   * rendering a player that can never load. It says the host CAN serve, not what: routes, payload shape
   * and allowed roots are the app's, and it says nothing about the URL SCHEME.
   */
  localFiles: 'localFiles',
  /**
   * The shell can HOLD the window at an orientation — see `WindowOrientation`. Android only today; the
   * desktop has nothing to hold, and iOS refuses rather than half-doing it.
   *
   * ⚠ Branch on it, because the page's own fallback is real but weaker: `screen.orientation.lock()` is
   * honoured only while the document is FULLSCREEN, and not at all in WKWebView. Absent here means take
   * fullscreen first or leave rotation alone.
   */
  windowOrientation: 'windowOrientation',
  /**
   * The shell can draw the PICTURE itself, under a transparent region the page leaves — see
   * `useMediaSurface`. The mobile shells; the desktop has none and does not need one.
   *
   * ⚠ Branch on it, but the fallback is a real player rather than a degraded one: absent, a `<video>`
   * element is the picture, which is the right answer wherever the webview can decode the file. Present,
   * the shell's own player opens what that element refuses.
   *
   * 🔴 It asserts TWO things: a surface exists AND a player is attached to draw into it. A host that
   * advertises it on the strength of the surface alone gives you a hole with no decoder behind it — the
   * controls render, nothing ever appears, and it is indistinguishable from a refused file.
   *
   * ⚠ It still says nothing about a GIVEN file — what the platform decodes is a per-stream question the
   * host answers.
   */
  mediaSurface: 'mediaSurface',
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
 * A client-side event on the event bus — an unbundled {@link IpcNotification}, or a locally emitted
 * event. The host-side `EventMessage` additionally carries id/timestamp, which don't cross the wire.
 */
export interface EventMessage<TPayload = unknown> {
  module: string;
  type: string;
  payload?: TPayload;
  scope?: string;
}
