/**
 * Building the URL that lets a page render LOCAL content — media, images, documents, exports — that it
 * cannot reach directly.
 *
 * The host answers these through its resource interceptor; this module only builds the address, which is
 * why it is a pure function and not a hook. A hook (`useMediaSource`) can follow if an adopter wants
 * load/error state, but nothing needs one to start.
 */

/**
 * Build a URL the host's resource interceptor will answer: `<route>?<base64url of JSON>`.
 *
 * ⚠ **The result is RELATIVE, and that is the whole point.** Written as a path rather than with a scheme, it
 * resolves against whatever origin the page is already served from — which means the browser hands the host
 * a URL each platform can actually decode:
 *
 * | shell | the same call resolves to |
 * |---|---|
 * | iOS | `app://0.0.0.1/media?…` — the app scheme, the only thing iOS intercepts |
 * | Android | `https://0.0.0.1/media?…` — Android's media pipeline REFUSES a non-standard scheme |
 * | desktop | the app's virtual host |
 *
 * Both fixed forms fail on exactly one platform, in opposite directions, and registering the scheme rescues
 * neither: iOS cannot register a handler for `https`, and Android cannot register a scheme at all. Measured
 * on devices — so if you are tempted to hardcode `app://`, that is why not.
 *
 * The PAYLOAD is opaque to this package: whatever you pass is JSON-encoded and handed to your own host-side
 * route, which decodes it. That keeps the kit out of your addressing scheme — a filename, an id, a container
 * preference, a cache key, several of them. The kit encodes; you decide what it means.
 *
 * base64**url** specifically, so `+`, `/` and `=` cannot survive into a query string and be re-interpreted
 * by anything that parses URLs along the way.
 *
 * ⚠ An encoded payload costs debuggability — you cannot read the URL in a log any more. Have the host log
 * what it DECODED to: the response body cannot say, because an error body would leak paths.
 *
 * @param payload Anything JSON-serialisable. Your host route decides what the shape means.
 * @param route The reserved path the host answers on. **Must not collide with a real asset in your bundle** —
 *   this shadows it. Defaults to `media`.
 * @returns A relative URL, e.g. `/media?eyJzcmMiOiJjbGlwLm1wNCJ9`.
 *
 * @example
 * ```tsx
 * const shell = useShellInfo();
 * const canServe = shell?.capabilities.includes(ShellCapabilities.localFiles);
 * return canServe
 *   ? <video src={mediaUrl({ src: id })} controls playsInline />
 *   : <button onClick={openExternally}>Open</button>;
 * ```
 */
export function mediaUrl(payload: unknown, route = 'media'): string {
  if (typeof route !== 'string' || route.length === 0) {
    throw new Error('mediaUrl: route must be a non-empty string.');
  }
  const path = route.startsWith('/') ? route : `/${route}`;
  return `${path}?${encodeMediaPayload(payload)}`;
}

/**
 * The payload encoding on its own, for a caller that builds its own URL but wants the same wire format —
 * and so a host-side decoder has one documented thing to mirror.
 *
 * `TextEncoder` before `btoa` because `btoa` throws on any character above U+00FF: a payload carrying a
 * non-ASCII title or path would fail at the call site, which is a poor way to discover an encoding choice.
 */
export function encodeMediaPayload(payload: unknown): string {
  const bytes = new TextEncoder().encode(JSON.stringify(payload));
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * Decode what {@link encodeMediaPayload} produced. Here for tests and for a page that round-trips its own
 * URLs; the real decoder is host-side, in whatever language the shell is written in.
 *
 * Padding is restored before decoding — base64url drops `=`, and `atob` requires it.
 */
export function decodeMediaPayload<T = unknown>(encoded: string): T {
  let padded = encoded.replace(/-/g, '+').replace(/_/g, '/');
  padded += '='.repeat((4 - (padded.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (c) => c.charCodeAt(0));
  return JSON.parse(new TextDecoder().decode(bytes)) as T;
}
