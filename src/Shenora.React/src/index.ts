// @shenora/react — the client side of the Shenora desktop body: correlated invoke over a
// pluggable transport, the event hub host notifications unbundle into, typed module services,
// React hooks, and the dev interceptor for CDP-driven testing. Headless by design (D13): no UI
// components, no design-system dependency — apps bring their own.

export {
  IpcCategories,
  IpcErrorCodes,
  HANDSHAKE_MODULE,
  HANDSHAKE_TYPE,
  type IpcRequest,
  type IpcResponse,
  type IpcError,
  type IpcNotification,
  type IpcNotificationBatch,
  type EventMessage,
  ShellCapabilities,
  type ShellInfo,
} from './types.js';
export { OperationError } from './errors.js';
export {
  isShenoraAvailable,
  createHostTransport,
  createWebView2Transport,
  createHybridWebViewTransport,
  type ShenoraTransport,
} from './transport.js';
export {
  ShenoraEventBus,
  eventBus,
  // The options type of all THREE public subscribe methods, and it was unexported until the 2026-08-05
  // review — so an app could call them but could not name what it was passing (no typed wrapper, no
  // shared const, no signature that mentions it). Identical to the `IpcProgress` gap recorded
  // below, which is the point: the barrel is a surface, and shipping a method without its parameter
  // type is shipping half of it.
  type SubscribeOptions,
} from './eventBus.js';
export {
  ShenoraBridge,
  getBridge,
  configureBridge,
  type ShenoraBridgeOptions,
  type InvokeOptions,
  type PostOptions,
  type PostFailure,
} from './bridge.js';
export {
  createShenoraStore,
  type ShenoraStore,
  type ShenoraStoreOptions,
  type ShenoraStoreIo,
  type ShenoraStoreSnapshot,
} from './store.js';
export { BaseModuleService } from './moduleService.js';
export {
  IpcRequestStates,
  // The event vocabulary + the default module name. `createRequestsStore` deliberately does NOT
  // subscribe to RESUME_REQUESTED / WAIT_REQUESTED — those target the OWNING module's own service,
  // not the generic store — so the app writing that handler needs both symbols, and until now had
  // neither: it had to hard-code the literals the wire-mirror tests exist to keep it from doing.
  IpcRequestEventTypes,
  IpcRequestsModuleName,
  createRequestsStore,
  useShenoraRequests,
  type IpcRequestState,
  type IpcLabel,
  // `IpcRequestStatus.progress` is typed as this, so leaving it unexported made the field's own type
  // unnameable from outside the package — the kit's OWN sample had to re-declare the shape inline
  // (`samples/Shenora.Sample.Web/src/App.tsx`) to write a one-line formatter. `index.test.ts` could
  // not catch it: that gate compares `Object.keys(barrel)`, and a type has no runtime binding, so
  // the type half of the surface is pinned by the type-only import in that same file instead.
  type IpcProgress,
  type IpcRequestStatus,
  type RequestsState,
  type RequestsActions,
  type RequestsStoreOptions,
} from './requests.js';
export {
  WindowCommands,
  useWindowMaximized,
  type WindowResizeEdge,
  type CaptionButtonKind,
  type CaptionButtonRect,
} from './windowCommands.js';
export {
  useDropZone,
  DROP_ZONE_MODULE,
  type DropZoneFileDrop,
  type UseDropZoneOptions,
} from './useDropZone.js';
export {
  useShenora,
  useShenoraEvent,
  useShenoraQuery,
  useShellInfo,
  type ShenoraQueryResult,
} from './hooks.js';
// Native dialogs, capability-gated. The client half of the host's SHENORA.DIALOGS module.
export {
  FileDialogs,
  useFileDialogs,
  type FileDialogFilter,
  type FileDialogOptions,
  type OpenFileOptions,
  type OpenFolderOptions,
  type SaveFileOptions,
  type FileDialogResult,
  type FileDialogsHandle,
} from './fileDialogs.js';
export {
  installDevInterceptor,
  type DevInterceptorOptions,
  type DevIpcEntry,
  type DevEventEntry,
} from './devInterceptor.js';
// Addressing local content the page cannot reach itself. A pure function, not a hook — building the URL
// needs no React, and a `useMediaSource` can follow if an adopter wants load/error state.
export { mediaUrl, encodeMediaPayload, decodeMediaPayload } from './media.js';
// The HOST-owned player (D58): .NET holds the lifecycle, the page's element is the display and the sound.
// One hook, and the page stops deciding anything about formats.
export {
  useMediaPlayer,
  MEDIA_PLAYER_MODULE,
  MEDIA_PLAYER_REPORT,
  MediaPlayerCommands,
  type MediaPlayerReport,
  type MediaPlayerReportState,
  type UseMediaPlayerOptions,
} from './mediaPlayer.js';
