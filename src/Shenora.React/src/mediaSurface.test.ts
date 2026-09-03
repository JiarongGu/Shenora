// @vitest-environment jsdom
import { renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { FakeTransport } from './testing/fakeTransport.js';
import { MEDIA_PLAYER_MODULE } from './mediaPlayer.js';
import { MediaSurfaceCommands, useMediaSurface } from './mediaSurface.js';

/**
 * jsdom lays nothing out — every `getBoundingClientRect()` is zero — so the rectangle is stubbed per test.
 * That is the right seam anyway: the hook's job is MEASURE → POST, and what a real browser would have
 * measured is not something a DOM shim could tell us.
 *
 * ⚠ What this cannot prove: that the shell draws where it was told. That is a device claim.
 */
interface Box {
  left: number;
  top: number;
  width: number;
  height: number;
}

function stubRect(element: HTMLElement, box: Box) {
  element.getBoundingClientRect = () => ({
    ...box,
    x: box.left,
    y: box.top,
    right: box.left + box.width,
    bottom: box.top + box.height,
    toJSON: () => ({}),
  }) as DOMRect;
}

/** Every surface message the page sent, newest last. */
function surfaceMessages(transport: FakeTransport) {
  return transport.raw
    .map((line) => JSON.parse(line) as { module: string; type: string; payload?: unknown })
    .filter((m) => m.module === MEDIA_PLAYER_MODULE
      && (m.type === MediaSurfaceCommands.show || m.type === MediaSurfaceCommands.hide));
}

function createFixture(box: Box = { left: 10, top: 20, width: 320, height: 180 }, enabled = true) {
  const transport = new FakeTransport();
  const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });
  const element = document.createElement('div');
  document.body.appendChild(element);
  stubRect(element, box);
  const ref = { current: element as HTMLElement | null };

  const view = renderHook(({ on }: { on: boolean }) => useMediaSurface(ref, { bridge, enabled: on }), {
    initialProps: { on: enabled },
  });
  return { transport, element, view };
}

describe('useMediaSurface', () => {
  beforeEach(() => {
    // ResizeObserver does not exist in jsdom, and the hook constructs one unconditionally.
    vi.stubGlobal('ResizeObserver', class {
      observe() {}
      disconnect() {}
    });
    // Run scheduled measurements immediately, so a test does not have to wait a frame.
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
      callback(0);
      return 1;
    });
    vi.stubGlobal('cancelAnimationFrame', () => {});
  });

  it('sends the element rectangle in CSS pixels, unscaled', () => {
    const { transport } = createFixture({ left: 10, top: 20, width: 320, height: 180 });

    const first = surfaceMessages(transport)[0]!;
    expect(first.type).toBe(MediaSurfaceCommands.show);
    // 🔴 The numbers are the element's own — no devicePixelRatio anywhere on this path.
    expect(first.payload).toEqual({ x: 10, y: 20, width: 320, height: 180, onTop: false });
  });

  it('draws BEHIND the page by default, so the page can paint over the picture', () => {
    const { transport } = createFixture();

    expect((surfaceMessages(transport)[0]!.payload as { onTop: boolean }).onTop).toBe(false);
  });

  /**
   * 🔴 The one that costs real work if it regresses: `scroll` fires far more often than the compositor
   * draws, and an unguarded hook posts one bridge message per event forever.
   */
  it('posts nothing while the rectangle has not moved', () => {
    const { transport, element } = createFixture();
    const before = surfaceMessages(transport).length;

    window.dispatchEvent(new Event('scroll'));
    window.dispatchEvent(new Event('resize'));

    expect(surfaceMessages(transport)).toHaveLength(before);

    // ...and it DOES post once the rectangle actually moves, or the test above would pass on a hook that
    // never measures at all.
    stubRect(element, { left: 10, top: 100, width: 320, height: 180 });
    window.dispatchEvent(new Event('scroll'));

    const latest = surfaceMessages(transport).at(-1)!;
    expect(latest.type).toBe(MediaSurfaceCommands.show);
    expect((latest.payload as { y: number }).y).toBe(100);
  });

  /**
   * 🔴 The picture belongs to the SHELL and outlives the component. Without this, unmounting the stage
   * leaves a native rectangle painted over whatever the page shows next.
   */
  it('hides the picture on unmount', () => {
    const { transport, view } = createFixture();

    view.unmount();

    expect(surfaceMessages(transport).at(-1)?.type).toBe(MediaSurfaceCommands.hide);
  });

  it('hides the picture when it is disabled without unmounting', () => {
    const { transport, view } = createFixture({ left: 0, top: 0, width: 320, height: 180 }, true);

    view.rerender({ on: false });

    expect(surfaceMessages(transport).at(-1)?.type).toBe(MediaSurfaceCommands.hide);
  });

  /**
   * 🔴 A host that refuses the post must not take the PAGE down with it. These calls run from a scroll
   * handler, a ResizeObserver and an effect cleanup — places where a throw is a broken render or a leaked
   * observer, not a caught error. The reachable case is a page rendering the same component in a browser,
   * which is the fallback the capability check exists to make safe.
   */
  it('survives a bridge that throws, on every call site', () => {
    const throwing = {
      post: () => { throw new Error('no host'); },
    } as unknown as ShenoraBridge;
    const element = document.createElement('div');
    document.body.appendChild(element);
    stubRect(element, { left: 0, top: 0, width: 320, height: 180 });
    const ref = { current: element as HTMLElement | null };

    const view = renderHook(() => useMediaSurface(ref, { bridge: throwing }));

    // mount + a reposition + the cleanup's hide — none of them may escape
    expect(() => {
      stubRect(element, { left: 0, top: 40, width: 320, height: 180 });
      window.dispatchEvent(new Event('scroll'));
      view.unmount();
    }).not.toThrow();
  });

  /** A collapsed stage measures zero; the host reads that as "hide" rather than drawing at the origin. */
  it('still reports a collapsed element, so the host can hide rather than guess', () => {
    const { transport } = createFixture({ left: 0, top: 0, width: 0, height: 0 });

    const first = surfaceMessages(transport)[0]!;
    expect(first.payload).toEqual({ x: 0, y: 0, width: 0, height: 0, onTop: false });
  });
});
