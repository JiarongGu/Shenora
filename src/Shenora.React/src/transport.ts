/** The WebView2 host surface injected by a Shenora (or family) desktop host. */
interface ChromeWebView {
  postMessage(message: string): void;
  addEventListener(type: 'message', listener: (event: { data: string }) => void): void;
  removeEventListener(type: 'message', listener: (event: { data: string }) => void): void;
}

/**
 * The shape we need off `window`, read through a local cast rather than a global augmentation.
 *
 * ⚠ **A library must not claim global names.** A `declare global { interface Window { chrome?: … } }`
 * shipped in a `.d.ts` gives any app that also has `@types/chrome` a TS2717 out of a declaration file it
 * cannot edit.
 */
interface WebViewWindow {
  chrome?: { webview?: ChromeWebView };
  HybridWebView?: HybridWebViewApi;
}

/**
 * The MAUI host surface, injected by `_framework/hybridwebview.js` (.NET 10; a copy under
 * `scripts/` on .NET 9). Host→client arrives as a `HybridWebViewMessageReceived` CustomEvent on
 * `window` rather than through this object — the two directions are genuinely asymmetric here.
 *
 * ⚠ The page must LOAD that script. Without it this object simply does not exist, and the failure
 * is quiet in the worst way: the page renders, the send throws a TypeError nobody sees, and the host
 * waits for a handshake that never arrives.
 */
interface HybridWebViewApi {
  SendRawMessage(message: string): void;
}

/** The detail MAUI puts on its CustomEvent. */
interface HybridWebViewMessageEvent extends Event {
  detail?: { message?: unknown };
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
 * True when running inside ANY Shenora host — a WebView2 desktop shell or a MAUI `HybridWebView` — i.e.
 * when a transport to the host exists. In a plain browser this is false and callers should fall back to
 * browser-only behavior. The question is "is there a host", never "is it WebView2".
 */
export function isShenoraAvailable(): boolean {
  const host = webViewWindow();
  return !!host?.chrome?.webview || !!host?.HybridWebView;
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

/**
 * The MAUI `HybridWebView` transport, or null outside a MAUI host.
 *
 * Asymmetric by the platform's design: sending goes through `window.HybridWebView.SendRawMessage`,
 * while receiving is a `HybridWebViewMessageReceived` CustomEvent dispatched on `window`. Both
 * directions carry the same JSON envelopes the desktop shell speaks; the host side is
 * `Shenora.Maui.MauiIpcBridge`.
 */
export function createHybridWebViewTransport(): ShenoraTransport | null {
  const host = webViewWindow();
  const hybrid = host?.HybridWebView;
  if (!hybrid) return null;
  return {
    post: (message) => hybrid.SendRawMessage(message),
    subscribe: (listener) => {
      const handler = (event: Event) => {
        // Narrow before reading: any code on the page can dispatch an event with this name, and a
        // non-string detail must be ignored rather than handed to JSON.parse. Same rule the inbound
        // WebView2 path follows.
        const message = (event as HybridWebViewMessageEvent).detail?.message;
        if (typeof message === 'string') listener(message);
      };
      window.addEventListener('HybridWebViewMessageReceived', handler);
      return () => window.removeEventListener('HybridWebViewMessageReceived', handler);
    },
  };
}

/**
 * The transport for whichever Shenora host this page is running in, or null in a plain browser.
 * This is what the bridge uses by default, so an app that simply calls `invoke`/`post` works on the
 * desktop shell and the MAUI shell without knowing which one it is. A page is only ever in one of
 * them; WebView2 wins if both objects are somehow present.
 */
export function createHostTransport(): ShenoraTransport | null {
  return createWebView2Transport() ?? createHybridWebViewTransport();
}
