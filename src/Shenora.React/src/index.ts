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
export { ShenoraError } from './errors.js';
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
  // The event and route vocabulary + the default module name, so an app handling or cancelling a
  // request without `useShenoraRequests` never types the wire literals by hand.
  IpcRequestEventTypes,
  IpcRequestRoutes,
  IpcRequestsModuleName,
  createRequestsStore,
  useShenoraRequests,
  type IpcRequestState,
  type IpcLabel,
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
// The native clipboard, for the two things navigator.clipboard cannot do: FILES, and access with no
// user gesture. The client half of the host's SHENORA.CLIPBOARD module.
export {
  ClipboardAccess,
  useClipboard,
  PNG_IMAGE,
  HTML,
  type ClipboardContent,
  type ClipboardHandle,
} from './clipboard.js';
export {
  installDevInterceptor,
  type DevInterceptorOptions,
  type DevIpcEntry,
  type DevEventEntry,
} from './devInterceptor.js';
// Addressing local content the page cannot reach itself. Pure functions, not hooks.
export { mediaUrl, encodeMediaPayload, decodeMediaPayload } from './media.js';
export {
  parseManifest,
  pickMediaSource,
  nextSegment,
  segmentMimeType,
  codecsFromInitSegment,
  remoteSegmentUrl,
  SEGMENT_REMOTE_PREFIX,
} from './segmentStream.js';
export { bindSegmentStream, SegmentBinderError } from './segmentBinder.js';
export type { SegmentBinderOptions, SegmentBinding } from './segmentBinder.js';
export type {
  SegmentEntry,
  SegmentManifest,
  MediaSourceKind,
  MediaSourceGlobals,
  FetchState,
  FetchPolicy,
} from './segmentStream.js';
// The HOST-owned player (D58): .NET holds the lifecycle, the page's element is the display and the sound.
export {
  useMediaPlayer,
  MEDIA_PLAYER_MODULE,
  MEDIA_PLAYER_REPORT,
  MEDIA_PLAYER_STATUS,
  MediaPlayerCommands,
  MediaConversionEvents,
  MediaConversionErrorCodes,
  type MediaPlayerReport,
  type MediaPlayerReportState,
  type UseMediaPlayerOptions,
} from './mediaPlayer.js';
