// @vitest-environment jsdom
import { renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

/**
 * Guards the fix, not a feature: `vitest.setup.ts` registers @testing-library/react's `cleanup`
 * between tests. With no vitest config at all (the state before P5.5 H7) `globals` was false, so RTL
 * found no global `afterEach` and never self-registered — every `renderHook` a test did not unmount
 * by hand stayed MOUNTED for the rest of its file, effects and subscriptions live.
 *
 * The two tests below are ORDER-DEPENDENT on purpose, which is the only way to observe cleanup from
 * inside the suite it protects: the first mounts a hook and deliberately does NOT unmount it, the
 * second asserts the DOM is empty again. RTL renders into a container appended to document.body, so
 * a body that still has children means cleanup did not run. Deleting `setupFiles` from
 * vitest.config.ts fails the second test — verified.
 */
describe('RTL auto-cleanup is registered', () => {
  it('mounts a hook and leaves it mounted', () => {
    renderHook(() => 'mounted');

    expect(document.body.children.length).toBeGreaterThan(0);
  });

  it('finds the DOM emptied by the previous test\'s cleanup', () => {
    expect(document.body.children.length).toBe(0);
  });
});
