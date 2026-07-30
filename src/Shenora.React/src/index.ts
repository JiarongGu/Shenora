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
} from './types.js';
export { OperationError } from './errors.js';
export { isShenoraAvailable, createWebView2Transport, type ShenoraTransport } from './transport.js';
export { ShenoraEventBus, eventBus } from './eventBus.js';
export {
  ShenoraBridge,
  getBridge,
  configureBridge,
  type ShenoraBridgeOptions,
  type InvokeOptions,
} from './bridge.js';
export { BaseModuleService } from './moduleService.js';
export { WindowCommands, useWindowMaximized, type WindowResizeEdge } from './windowCommands.js';
export {
  useDropZone,
  DROP_ZONE_MODULE,
  type DropZoneFileDrop,
  type UseDropZoneOptions,
} from './useDropZone.js';
export { useShenora, useShenoraEvent, useShenoraQuery, type ShenoraQueryResult } from './hooks.js';
export {
  installDevInterceptor,
  type DevInterceptorOptions,
  type DevIpcEntry,
  type DevEventEntry,
} from './devInterceptor.js';
