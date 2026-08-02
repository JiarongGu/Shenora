import { afterEach, describe, expect, it } from 'vitest';
import {
  createHostTransport,
  createHybridWebViewTransport,
  createWebView2Transport,
  isShenoraAvailable,
} from './transport.js';

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

/**
 * The MAUI shell. Its two directions are ASYMMETRIC by the platform's design — send through
 * `window.HybridWebView.SendRawMessage`, receive a `HybridWebViewMessageReceived` CustomEvent on
 * `window` — so the fake has to model both halves rather than one channel object.
 */
function installHybridHost() {
  const sent: string[] = [];
  const listeners = new Set<(event: unknown) => void>();
  (globalThis as { window?: unknown }).window = {
    HybridWebView: { SendRawMessage: (message: string) => sent.push(message) },
    addEventListener: (_type: string, listener: (event: unknown) => void) => listeners.add(listener),
    removeEventListener: (_type: string, listener: (event: unknown) => void) => listeners.delete(listener),
  };
  const emit = (detail: unknown) => {
    for (const listener of [...listeners]) listener({ detail });
  };
  return { sent, listeners, emit };
}

describe('createHybridWebViewTransport', () => {
  it('is null when there is no MAUI host', () => {
    expect(createHybridWebViewTransport()).toBeNull();
    (globalThis as { window?: unknown }).window = {};
    expect(createHybridWebViewTransport()).toBeNull();
  });

  it('sends through SendRawMessage verbatim', () => {
    const host = installHybridHost();
    createHybridWebViewTransport()!.post('{"id":"1"}');
    expect(host.sent).toEqual(['{"id":"1"}']);
  });

  it('delivers a string detail.message and ignores every other shape', () => {
    const host = installHybridHost();
    const received: string[] = [];
    createHybridWebViewTransport()!.subscribe((message) => received.push(message));

    host.emit({ message: '{"category":"ipc"}' });
    // Any page script can dispatch an event with this name, so a non-string (or absent) message
    // must be dropped rather than handed up for the bridge to JSON.parse — the same rule the
    // WebView2 path follows for `event.data`.
    host.emit({ message: { object: true } });
    host.emit({ message: 42 });
    host.emit({});
    host.emit(undefined);

    expect(received).toEqual(['{"category":"ipc"}']);
  });

  it('unsubscribe detaches the window listener', () => {
    const host = installHybridHost();
    const received: string[] = [];
    const unsubscribe = createHybridWebViewTransport()!.subscribe((m) => received.push(m));
    expect(host.listeners.size).toBe(1);

    unsubscribe();

    expect(host.listeners.size).toBe(0);
    host.emit({ message: 'after-unsubscribe' });
    expect(received).toEqual([]);
  });
});

describe('host detection across both shells', () => {
  it('createHostTransport picks whichever host is present', () => {
    expect(createHostTransport()).toBeNull();

    installHost();
    expect(createHostTransport()).not.toBeNull();
    createHostTransport()!.post('desktop');

    const hybrid = installHybridHost();
    createHostTransport()!.post('mobile');
    expect(hybrid.sent).toEqual(['mobile']);
  });

  it('isShenoraAvailable answers for the MAUI shell too', () => {
    // The regression this pins: it used to test chrome.webview ALONE, so on the MAUI shell an app
    // concluded it was in a plain browser tab while a perfectly good host sat on the other side.
    expect(isShenoraAvailable()).toBe(false);

    installHybridHost();
    expect(isShenoraAvailable()).toBe(true);

    (globalThis as { window?: unknown }).window = {};
    expect(isShenoraAvailable()).toBe(false);

    installHost();
    expect(isShenoraAvailable()).toBe(true);
  });
});
