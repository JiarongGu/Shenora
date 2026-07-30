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
} from './types';
export { OperationError } from './errors';
export { isShenoraAvailable, createWebView2Transport, type ShenoraTransport } from './transport';
export { ShenoraEventBus, eventBus } from './eventBus';
export {
  ShenoraBridge,
  getBridge,
  configureBridge,
  type ShenoraBridgeOptions,
  type InvokeOptions,
} from './bridge';
export { BaseModuleService } from './moduleService';
export { useShenora, useShenoraEvent, useShenoraQuery, type ShenoraQueryResult } from './hooks';
export {
  installDevInterceptor,
  type DevInterceptorOptions,
  type DevIpcEntry,
  type DevEventEntry,
} from './devInterceptor';
