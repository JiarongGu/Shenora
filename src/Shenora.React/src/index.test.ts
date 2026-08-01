import { describe, expect, it } from 'vitest';
import * as barrel from './index.js';
import { isShenoraAvailable } from './index.js';
import type {
  EventMessage,
  IpcError,
  IpcNotification,
  IpcNotificationBatch,
  IpcRequest,
  IpcResponse,
  CaptionButtonKind,
  CaptionButtonRect,
  DropZoneFileDrop,
  InvokeOptions,
  OperationInfo,
  OperationLabel,
  OperationProgress,
  OperationStatus,
  OperationsActions,
  OperationsState,
  OperationsStoreOptions,
  PostFailure,
  PostOptions,
  ShenoraBridgeOptions,
  ShenoraQueryResult,
  ShenoraStore,
  ShenoraStoreIo,
  ShenoraStoreOptions,
  ShenoraStoreSnapshot,
  ShenoraTransport,
  UseDropZoneOptions,
  WindowResizeEdge,
} from './index.js';

/**
 * The barrel IS the package's public surface — `exports` maps "." to it and nothing else is
 * reachable — so deleting a line from index.ts is a breaking change for every consumer. Before
 * P5.5 H7 no test referenced the barrel except for `isShenoraAvailable`, so removing any other
 * export failed NOTHING: the C# side has `ApiSurfaceTests` + five checked-in baselines for exactly
 * this, and the npm half had no equivalent.
 *
 * Kept as an explicit sorted list rather than a snapshot on purpose: a snapshot updates itself with
 * `-u` and a reviewer never sees the removal, whereas editing this array is a deliberate act that
 * shows up in the diff next to the CHANGELOG entry it needs.
 */
const EXPECTED_EXPORTS = [
  'BaseModuleService',
  'DROP_ZONE_MODULE',
  'HANDSHAKE_MODULE',
  'HANDSHAKE_TYPE',
  'IpcCategories',
  'IpcErrorCodes',
  'OperationError',
  'OperationEventTypes',
  'OperationModuleName',
  'OperationStatuses',
  'ShenoraBridge',
  'ShenoraEventBus',
  'WindowCommands',
  'configureBridge',
  'createOperationsStore',
  'createShenoraStore',
  'createWebView2Transport',
  'eventBus',
  'getBridge',
  'installDevInterceptor',
  'isShenoraAvailable',
  'useDropZone',
  'useShenora',
  'useShenoraEvent',
  'useShenoraOperations',
  'useShenoraQuery',
  'useWindowMaximized',
] as const;

/**
 * The TYPE half of the same gate, and it needs to exist separately: `EXPECTED_EXPORTS` above compares
 * `Object.keys(barrel)`, and a type has no runtime binding — so deleting a `type` line from
 * `index.ts` passed both assertions above while breaking every consumer that named it.
 *
 * `OperationProgress` is why this was written. `OperationInfo.progress` is typed as it, so the field's
 * own type was unnameable from outside the package; the tell was that the kit's own sample re-declared
 * the shape inline rather than importing it. Nothing failed.
 *
 * This alias is the pin: it is compiled by `npm run typecheck` (the FULL tsconfig, which includes test
 * files — the build config excludes them and vitest transpiles without checking, so a type-only pin is
 * inert without that step; see `.claude/knowledge/ipc-contracts.md`). Removing an exported type from
 * the barrel makes this file fail to compile, naming the type.
 */
type ExportedTypeSurface = [
  EventMessage, IpcError, IpcNotification, IpcNotificationBatch, IpcRequest, IpcResponse,
  CaptionButtonKind, CaptionButtonRect, DropZoneFileDrop, InvokeOptions,
  OperationInfo, OperationLabel, OperationProgress, OperationStatus,
  OperationsActions, OperationsState, OperationsStoreOptions,
  PostFailure, PostOptions, ShenoraBridgeOptions, ShenoraQueryResult<unknown>,
  ShenoraStore<unknown, unknown>, ShenoraStoreIo, ShenoraStoreOptions<unknown, unknown>,
  ShenoraStoreSnapshot<unknown>, ShenoraTransport, UseDropZoneOptions, WindowResizeEdge,
];

describe('the public barrel', () => {
  it('exports every documented TYPE (compile-time — see ExportedTypeSurface)', () => {
    // The assertion is the ALIAS above compiling at all; this body only keeps the alias referenced
    // so `noUnusedLocals` does not delete the pin for us.
    const surface: ExportedTypeSurface | undefined = undefined;
    expect(surface).toBeUndefined();
  });

  it('exports exactly the documented runtime surface', () => {
    // Sorted so the failure message is a readable set difference, not an ordering complaint.
    expect(Object.keys(barrel).sort()).toEqual([...EXPECTED_EXPORTS]);
  });

  it('exports no accidental undefined bindings', () => {
    // A renamed source symbol can leave the barrel name resolving to undefined while the module
    // still loads; every entry above must be a real value.
    for (const name of EXPECTED_EXPORTS) {
      expect(barrel[name], `${name} is exported but undefined`).toBeDefined();
    }
  });
});

describe('isShenoraAvailable', () => {
  it('is false outside a WebView2 host (node has no window)', () => {
    expect(isShenoraAvailable()).toBe(false);
  });

  it('is true when window.chrome.webview exists', () => {
    const g = globalThis as { window?: unknown };
    g.window = { chrome: { webview: { postMessage() {}, addEventListener() {}, removeEventListener() {} } } };
    try {
      expect(isShenoraAvailable()).toBe(true);
    } finally {
      delete g.window;
    }
  });
});
