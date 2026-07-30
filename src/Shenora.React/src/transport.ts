/** The WebView2 host surface injected by a Shenora (or family) desktop host. */
interface ChromeWebView {
  postMessage(message: string): void;
  addEventListener(type: 'message', listener: (event: { data: string }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: string }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: ChromeWebView };
  }
}

/**
 * A message channel the bridge speaks over — the transport-pluggable seam (design D16): WebView2
 * postMessage on desktop today; a WebSocket or a mobile shell's native channel speaks the same
 * envelopes tomorrow. Messages are JSON strings in both directions.
 */
export interface ShenoraTransport {
  /** Send one serialized envelope to the host. */
  post(message: string): void;
  /** Register a host→client listener; returns the unsubscribe function. */
  subscribe(listener: (message: string) => void): () => void;
}

/**
 * True when running inside a WebView2 desktop host (the bridge transport exists).
 * In a plain browser this is false — callers should fall back to browser-only behavior.
 */
export function isShenoraAvailable(): boolean {
  return typeof window !== 'undefined' && !!window.chrome?.webview;
}

/** The WebView2 postMessage transport, or null outside a WebView2 host. */
export function createWebView2Transport(): ShenoraTransport | null {
  if (!isShenoraAvailable()) return null;
  const webview = window.chrome!.webview!;
  return {
    post: (message) => webview.postMessage(message),
    subscribe: (listener) => {
      // The host posts strings (PostWebMessageAsString); anything else on the channel isn't ours.
      const handler = (event: { data: string }) => {
        if (typeof event.data === 'string') listener(event.data);
      };
      webview.addEventListener('message', handler);
      return () => webview.removeEventListener('message', handler);
    },
  };
}
