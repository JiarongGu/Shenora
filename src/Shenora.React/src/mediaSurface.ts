import { useEffect, type RefObject } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { MEDIA_PLAYER_MODULE } from './mediaPlayer.js';

/**
 * The routes that move the SHELL's picture. **A wire contract**: these strings are duplicated in C# as
 * `MediaPlayerModule.SurfaceShowType` / `SurfaceHideType`, and the two halves agree by string or not at all.
 */
export const MediaSurfaceCommands = {
  /** `{ x, y, width, height, onTop? }` in CSS pixels. */
  show: 'SURFACE_SHOW',
  /** No payload. */
  hide: 'SURFACE_HIDE',
} as const;

/** Inputs for {@link useMediaSurface}. */
export interface UseMediaSurfaceOptions {
  /**
   * Draw the picture ABOVE the page instead of behind it. Default `false`, which is what lets you paint
   * captions and controls over it.
   */
  onTop?: boolean;
  /**
   * Stop measuring and hide the picture. Use it to turn the surface off without unmounting — flipping this
   * to `false` sends one `hide`.
   */
  enabled?: boolean;
  /** Override the module. Must match the host's `MediaPlayerOptions.Access.Module`. */
  module?: string;
  /** Test seam. */
  bridge?: ShenoraBridge;
}

/**
 * Tell the shell where to draw the picture: this element's rectangle becomes a hole, and the host's own
 * player fills it from underneath (D58's second surface).
 *
 * ```tsx
 * const stage = useRef<HTMLDivElement>(null);
 * useMediaSurface(stage);
 * return <div ref={stage} style={{ background: 'transparent' }} />;
 * ```
 *
 * **Use this instead of {@link useMediaPlayer} when the webview cannot decode the file** — the shell's
 * player opens what a `<video>` element refuses. Everywhere else the element is the better answer, and
 * both are the same `IMediaPlayer` underneath.
 *
 * 🔴 **The element must be genuinely TRANSPARENT, and so must everything behind it.** The picture is drawn
 * BELOW the webview, so any opaque ancestor — most often a `body` background — hides it completely. That
 * failure looks exactly like a player that never started, so check the backgrounds before the player.
 *
 * ⚠ **Gate it on the `mediaSurface` capability**, by rendering this component only on a shell that has one:
 * a host without a surface answers every post with `MEDIA_SURFACE_UNAVAILABLE`. Read the capability from a
 * source that RE-RENDERS when the handshake lands — a synchronous cache read taken during the first render
 * is `false` for the whole session on a page that mounts before the host answers.
 *
 * ⚠ **The rectangle is in CSS pixels and crosses to the shell unconverted** — do not scale it by
 * `devicePixelRatio`.
 *
 * ⚠ **It follows scroll and resize, coalesced to one post per animation frame**, and posts nothing when
 * the rectangle has not moved. A collapsed or unmounted element measures zero, which the host reads as
 * "hide" rather than drawing a dot at the origin.
 */
export function useMediaSurface(
  ref: RefObject<HTMLElement | null>,
  options: UseMediaSurfaceOptions = {},
): void {
  const { onTop = false, enabled = true, module = MEDIA_PLAYER_MODULE, bridge } = options;

  useEffect(() => {
    const element = ref.current;

    /* 🔴 NOTHING HERE MAY THROW AT THE PAGE, and the call sites are why.
     *
     * These run from a scroll handler, a ResizeObserver and an effect CLEANUP — three places where an
     * exception is not a caught error but a broken render or a leaked observer. And the throw is reachable:
     * a page rendering the same component in a browser has no host at all, which is exactly the fallback
     * case the capability check is supposed to make safe.
     */
    const post = (type: string, payload?: object) => {
      try {
        (bridge ?? getBridge()).post(module, type, payload ? { payload } : {});
      } catch { /* a picture is never worth taking the page down for */ }
    };

    const hide = () => post(MediaSurfaceCommands.hide);

    if (!element || !enabled) {
      // Not a no-op: turning the surface off has to reach the shell, or the picture stays where it was.
      hide();
      return;
    }

    let frame = 0;
    let pending = false;
    let last = '';

    const measure = () => {
      pending = false;
      const box = element.getBoundingClientRect();
      // Rounded before the comparison, not after: sub-pixel jitter during a scroll otherwise makes every
      // frame look like a move and posts one message per frame forever.
      const payload = {
        x: Math.round(box.left),
        y: Math.round(box.top),
        width: Math.round(box.width),
        height: Math.round(box.height),
        onTop,
      };

      // ⚠ `onTop` is part of the dedupe key, or a surface that changes ONLY its z-order is never told.
      const key = JSON.stringify(payload);
      if (key === last) return;
      last = key;
      post(MediaSurfaceCommands.show, payload);
    };

    // Coalesced to a frame: scroll fires far more often than the compositor draws, and every post is a
    // trip across the bridge.
    //
    // ⚠ The guard is a FLAG, not the frame id. Clearing `frame` inside the callback only works while the
    // callback is asynchronous — and the id is assigned AFTER it returns — so a synchronous frame leaves
    // the id set for good and the surface never moves again.
    const schedule = () => {
      if (pending) return;
      pending = true;
      frame = requestAnimationFrame(measure);
    };

    measure();

    // `capture: true` is what makes a NESTED scroller count — a scroll inside a div does not bubble, and
    // without it the picture stays behind whenever the stage is inside its own scroll area.
    window.addEventListener('scroll', schedule, { capture: true, passive: true });
    window.addEventListener('resize', schedule, { passive: true });

    // Size and layout changes that no scroll or resize event reports: a sibling collapsing, a font
    // loading, the element itself being animated.
    const observer = new ResizeObserver(schedule);
    observer.observe(element);

    return () => {
      if (frame !== 0) cancelAnimationFrame(frame);
      window.removeEventListener('scroll', schedule, { capture: true });
      window.removeEventListener('resize', schedule);
      observer.disconnect();
      // 🔴 The picture is the SHELL's and outlives this component. Without this, unmounting the stage
      // leaves a native rectangle painted over whatever the page navigates to next.
      hide();
    };
  }, [ref, onTop, enabled, module, bridge]);
}
