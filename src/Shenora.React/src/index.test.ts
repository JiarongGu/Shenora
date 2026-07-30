import { describe, expect, it } from 'vitest';
import { isShenoraAvailable } from './index.js';

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
