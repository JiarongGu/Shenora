// @shenora/react — bootstrap placeholder.
// The real bridge (correlated invoke/send/subscribe, typed module services, hooks, browser
// fallback) arrives with the Phase 3 extraction (docs/ROADMAP.md in the repo). Until then the
// package exposes only the host-detection primitive so the version/packaging pipeline is real.

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
 * True when running inside a WebView2 desktop host (the bridge transport exists).
 * In a plain browser this is false — callers should fall back to browser-only behavior.
 */
export function isShenoraAvailable(): boolean {
  return typeof window !== 'undefined' && !!window.chrome?.webview;
}
