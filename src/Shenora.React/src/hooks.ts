import { useCallback, useEffect, useRef, useState } from 'react';
import { getBridge, type ShenoraBridge } from './bridge';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus';
import type { EventMessage } from './types';

/** Host access for components: the (default) bridge and whether a host transport exists. */
export function useShenora(): { isAvailable: boolean; bridge: ShenoraBridge } {
  const bridge = getBridge();
  return { isAvailable: bridge.isAvailable, bridge };
}

/**
 * Subscribe to one `module.type` event for the component's lifetime, ported from the primary
 * desktop sibling. The handler receives the unwrapped payload (plus the full event). DEVIATION
 * from the source: instead of a deps array re-subscribing on change, the latest handler is kept
 * in a ref — no re-subscribe churn, no stale-closure trap.
 */
export function useShenoraEvent<TPayload = unknown>(
  module: string,
  type: string,
  handler: (payload: TPayload, event: EventMessage<TPayload>) => void,
  options: { bus?: ShenoraEventBus } = {},
): void {
  const handlerRef = useRef(handler);
  handlerRef.current = handler;
  const bus = options.bus ?? defaultEventBus;

  useEffect(
    () => bus.subscribe<TPayload>(module, type, (event) => handlerRef.current(event.payload as TPayload, event)),
    [module, type, bus],
  );
}

/** Result of {@link useShenoraQuery}. */
export interface ShenoraQueryResult<TData> {
  data: TData | undefined;
  error: Error | undefined;
  /** True while a fetch is in flight. */
  loading: boolean;
  /** Re-run the query. */
  refetch: () => void;
}

/**
 * Fetch-on-mount over the bridge: `invoke` + `{data, error, loading, refetch}`. Deliberately
 * minimal — no caching, no dedup, no background refresh (headless, D13): apps with data-layer
 * needs bring their own query library and call `bridge.invoke` from it. The payload participates
 * in the effect key BY VALUE (JSON), so inline object literals don't refetch every render.
 */
export function useShenoraQuery<TData = unknown, TPayload = unknown>(
  module: string,
  type: string,
  options: {
    payload?: TPayload;
    scope?: string;
    /** False = don't fetch (yet). Default true. */
    enabled?: boolean;
    bridge?: ShenoraBridge;
  } = {},
): ShenoraQueryResult<TData> {
  const { payload, scope, enabled = true } = options;
  const bridge = options.bridge ?? getBridge();
  const [state, setState] = useState<{ data: TData | undefined; error: Error | undefined; loading: boolean }>({
    data: undefined,
    error: undefined,
    loading: enabled,
  });
  const [fetchToken, setFetchToken] = useState(0);
  const payloadKey = payload === undefined ? '' : JSON.stringify(payload);
  const payloadRef = useRef(payload);
  payloadRef.current = payload;

  useEffect(() => {
    if (!enabled) {
      // A fetch in flight when enabled flipped false was marked stale by the cleanup — without
      // this, `loading` would stay true forever (a spinner that never stops).
      setState((previous) => (previous.loading ? { ...previous, loading: false } : previous));
      return;
    }
    let stale = false;
    setState((previous) => ({ ...previous, loading: true }));
    bridge
      .invoke<TData, TPayload>(module, type, { payload: payloadRef.current, scope })
      .then(
        (data) => { if (!stale) setState({ data, error: undefined, loading: false }); },
        (error: Error) => { if (!stale) setState({ data: undefined, error, loading: false }); },
      );
    return () => { stale = true; };
  }, [module, type, scope, enabled, bridge, payloadKey, fetchToken]);

  const refetch = useCallback(() => setFetchToken((token) => token + 1), []);

  return { ...state, refetch };
}
