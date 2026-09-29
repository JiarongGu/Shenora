/**
 * Shared internals for `@shenora/react`. NOT exported from the barrel — nothing here is public
 * surface, and it must not become so by accident.
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

/** A debounced void callback with a `cancel` for effect teardown. */
export interface Debounced {
  (): void;
  cancel(): void;
}

/**
 * Trailing-edge debounce: the callback runs `ms` after the LAST call. `cancel` must be called from a
 * React effect's cleanup, or a pending timer fires against an unmounted component.
 */
export function debounce(fn: () => void, ms: number): Debounced {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const wrapped = (() => {
    clearTimeout(timer);
    timer = setTimeout(fn, ms);
  }) as Debounced;
  wrapped.cancel = () => clearTimeout(timer);
  return wrapped;
}

/**
 * A unique id, optionally prefixed. Correlation ids and zone ids need uniqueness, not entropy, so the
 * non-`crypto` fallback for a non-secure context is fine.
 */
export function randomId(prefix?: string): string {
  const id =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return prefix === undefined ? id : `${prefix}${id}`;
}
