import { describe, expect, it } from 'vitest';
import * as barrel from './index.js';
import { isShenoraAvailable } from './index.js';

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
  'ShenoraBridge',
  'ShenoraEventBus',
  'WindowCommands',
  'configureBridge',
  'createWebView2Transport',
  'eventBus',
  'getBridge',
  'installDevInterceptor',
  'isShenoraAvailable',
  'useDropZone',
  'useShenora',
  'useShenoraEvent',
  'useShenoraQuery',
  'useWindowMaximized',
] as const;

describe('the public barrel', () => {
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
