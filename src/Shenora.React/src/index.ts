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
  OperationStatuses,
  // The event vocabulary + the default module name. `createOperationsStore` deliberately does NOT
  // subscribe to RESUME_REQUESTED / WAIT_REQUESTED — those target the OWNING module's own service,
  // not the generic store — so the app writing that handler needs both symbols, and until now had
  // neither: it had to hard-code the literals the wire-mirror tests exist to keep it from doing.
  OperationEventTypes,
  OperationModuleName,
  createOperationsStore,
  useShenoraOperations,
  type OperationStatus,
  type OperationLabel,
  // `OperationInfo.progress` is typed as this, so leaving it unexported made the field's own type
  // unnameable from outside the package — the kit's OWN sample had to re-declare the shape inline
  // (`samples/Shenora.Sample.Web/src/App.tsx`) to write a one-line formatter. `index.test.ts` could
  // not catch it: that gate compares `Object.keys(barrel)`, and a type has no runtime binding, so
  // the type half of the surface is pinned by the type-only import in that same file instead.
  type OperationProgress,
  type OperationInfo,
  type OperationsState,
  type OperationsActions,
  type OperationsStoreOptions,
} from './operations.js';
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
export { useShenora, useShenoraEvent, useShenoraQuery, type ShenoraQueryResult } from './hooks.js';
export {
  installDevInterceptor,
  type DevInterceptorOptions,
  type DevIpcEntry,
  type DevEventEntry,
} from './devInterceptor.js';
