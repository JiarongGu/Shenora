import { afterEach, describe, expect, it } from 'vitest';
import { createWebView2Transport } from './transport.js';

/**
 * `createWebView2Transport` had ZERO references in the suite before P5.5 H7 while being the ONE
 * transport every real desktop consumer runs on — the fakes cover the bridge above it, and the
 * sample exercises it only end-to-end. Two of its behaviours are the regression-prone kind: the
 * null return outside a host (callers branch on it) and the `typeof event.data === 'string'` filter,
 * which exists because the WebView2 channel also carries non-string messages that are not ours.
 */
interface FakeWebView {
  postMessage(message: string): void;
  addEventListener(type: 'message', listener: (event: { data: string }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: string }) => void): void;
}

function installHost() {
  const posted: string[] = [];
  const listeners = new Set<(event: { data: string }) => void>();
  const webview: FakeWebView = {
    postMessage: (message) => posted.push(message),
    addEventListener: (_type, listener) => listeners.add(listener),
    removeEventListener: (_type, listener) => listeners.delete(listener),
  };
  (globalThis as { window?: unknown }).window = { chrome: { webview } };
  // `data` is deliberately typed loosely: the point of these cases is what arrives off a real
  // channel, which is not always a string.
  const emit = (data: unknown) => {
    for (const listener of [...listeners]) listener({ data } as { data: string });
  };
  return { posted, listeners, emit };
}

afterEach(() => {
  delete (globalThis as { window?: unknown }).window;
});

describe('createWebView2Transport', () => {
  it('is null when there is no WebView2 host', () => {
    expect(createWebView2Transport()).toBeNull();
  });

  it('is null when window exists but carries no chrome.webview', () => {
    (globalThis as { window?: unknown }).window = {};
    expect(createWebView2Transport()).toBeNull();
    (globalThis as { window?: unknown }).window = { chrome: {} };
    expect(createWebView2Transport()).toBeNull();
  });

  it('posts through to the host channel verbatim', () => {
    const host = installHost();
    createWebView2Transport()!.post('{"id":"1"}');
    expect(host.posted).toEqual(['{"id":"1"}']);
  });

  it('delivers string messages and ignores everything else on the channel', () => {
    const host = installHost();
    const received: string[] = [];
    createWebView2Transport()!.subscribe((message) => received.push(message));

    host.emit('{"category":"ipc"}');
    // Not ours — the host posts strings (PostWebMessageAsString). Anything else must be dropped
    // rather than handed up as a non-string the bridge would then JSON.parse.
    host.emit({ object: true });
    host.emit(42);
    host.emit(null);
    host.emit(undefined);

    expect(received).toEqual(['{"category":"ipc"}']);
  });

  it('unsubscribe detaches the listener from the host', () => {
    const host = installHost();
    const received: string[] = [];
    const unsubscribe = createWebView2Transport()!.subscribe((m) => received.push(m));
    expect(host.listeners.size).toBe(1);

    unsubscribe();

    expect(host.listeners.size).toBe(0);
    host.emit('after-unsubscribe');
    expect(received).toEqual([]);
  });
});
