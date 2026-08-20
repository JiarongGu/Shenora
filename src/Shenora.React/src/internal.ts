/**
 * Shared internals for `@shenora/react`. NOT exported from the barrel — nothing here is public
 * surface, and it must not become so by accident.
 */

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
