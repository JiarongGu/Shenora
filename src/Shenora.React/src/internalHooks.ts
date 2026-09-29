/**
 * React-only internals for `@shenora/react`'s hooks, apart from `internal.ts`, which the React-free bridge
 * imports. NOT exported from the barrel.
 */
import { useEffect, useState, type RefObject } from 'react';

/**
 * A ref's CONTENT, as state. A ref is a stable object, so an effect keyed on it runs once, and a target that
 * is not there on that run (rendered conditionally, or attached after the first commit) is never seen: the
 * hook is silently dead for the component's whole life. A ref mutation triggers no render, so this effect has
 * NO dependency array; setting an unchanged value is a React no-op, so it cannot loop.
 */
export function useRefElement<T extends Element>(ref: RefObject<T | null>): T | null {
  const [element, setElement] = useState<T | null>(null);
  useEffect(() => {
    setElement(ref.current ?? null);
  });
  return element;
}
