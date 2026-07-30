/** The WebView2 host surface injected by a Shenora (or family) desktop host. */
interface ChromeWebView {
  postMessage(message: string): void;
  addEventListener(type: 'message', listener: (event: { data: string }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: string }) => void): void;
}

/**
 * The shape we need off `window`, read through a local cast rather than a global augmentation.
 *
 * `declare global { interface Window { chrome?: … } }` is what this used to do, and shipping it in a
 * `.d.ts` is hostile to consumers (P5.5 H6): any app that also has `@types/chrome` in its program gets
 * TS2717 ("subsequent property declarations must have the same type") from a declaration file it does not
 * own and cannot edit. A library must not claim global names — the augmentation bought nothing here
 * beyond two casts.
 */
interface WebViewWindow {
  chrome?: { webview?: ChromeWebView };
}

const webViewWindow = (): WebViewWindow | undefined =>
  typeof window === 'undefined' ? undefined : (window as unknown as WebViewWindow);

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
  return !!webViewWindow()?.chrome?.webview;
}

/** The WebView2 postMessage transport, or null outside a WebView2 host. */
export function createWebView2Transport(): ShenoraTransport | null {
  const webview = webViewWindow()?.chrome?.webview;
  if (!webview) return null;
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
