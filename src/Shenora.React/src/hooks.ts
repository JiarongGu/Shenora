import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import type { EventMessage, ShellInfo } from './types.js';

/**
 * Host access for components: the (default) bridge and whether a host transport exists.
 *
 * ⚠ The result is MEMOIZED, and not as a micro-optimisation. A fresh object every render is a fresh
 * dependency for every hook that lists it, so `useEffect(…, [shenora])` would re-run on EVERY render.
 * The identity changes only when the bridge does, or when `isAvailable` flips as a host attaches.
 */
export function useShenora(): { isAvailable: boolean; bridge: ShenoraBridge } {
  const bridge = getBridge();
  const isAvailable = bridge.isAvailable;
  return useMemo(() => ({ isAvailable, bridge }), [isAvailable, bridge]);
}

/**
 * What the host IS and what it can do — the way one bundle renders correctly on every shell.
 * Branch on {@link ShellInfo.capabilities}, never on {@link ShellInfo.name}.
 *
 * ```tsx
 * const shell = useShellInfo();
 * return <>{shell?.capabilities.includes(ShellCapabilities.windowChrome) && <TitleBar />}</>;
 * ```
 *
 * `undefined` means no host said anything — a plain browser tab, a host predating the handshake, or a
 * handshake that has not finished. **Treat absent as "assume nothing", never as "assume desktop".**
 *
 * ⚠ **Await `bridge.notifyReady()` before rendering the tree that depends on this.** The value is read
 * SYNCHRONOUSLY from the bridge's cache — a capability learned after layout is a visible flash — so
 * this hook does NOT re-render when a handshake lands later. A component mounted mid-handshake sees
 * `undefined` and keeps seeing it: the "assume nothing" tree, not the one you wanted.
 */
export function useShellInfo(bridge?: ShenoraBridge): ShellInfo | undefined {
  return (bridge ?? getBridge()).shell;
}

/**
 * Subscribe to one (module, type) event for the component's lifetime. The handler receives the
 * unwrapped payload plus the full event, and is kept in a ref rather than in the effect key — no
 * re-subscribe churn, no stale-closure trap.
 *
 * Pass `scope` for a scoped app, or a component in profile A also wakes for profile B's events.
 * Omitting it means "every scope", and a global (scope-less) event still reaches a scoped subscriber.
 */
export function useShenoraEvent<TPayload = unknown>(
  module: string,
  type: string,
  handler: (payload: TPayload, event: EventMessage<TPayload>) => void,
  options: { bus?: ShenoraEventBus; scope?: string } = {},
): void {
  const handlerRef = useRef(handler);
  handlerRef.current = handler;
  const bus = options.bus ?? defaultEventBus;
  const scope = options.scope;

  useEffect(
    () => bus.subscribe<TPayload>(
      module,
      type,
      (event) => handlerRef.current(event.payload as TPayload, event),
      { scope },
    ),
    [module, type, bus, scope],
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
 * Fetch-on-mount over the bridge: `invoke` + `{data, error, loading, refetch}`. Minimal — no caching,
 * no dedup, no background refresh; an app with data-layer needs brings its own query library and calls
 * `bridge.invoke` from it. The payload participates in the effect key BY VALUE (JSON), so an inline
 * object literal does not refetch every render.
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
      // The cleanup marked any in-flight fetch stale, so without this `loading` would stay true
      // forever: a spinner that never stops.
      setState((previous) => (previous.loading ? { ...previous, loading: false } : previous));
      return;
    }
    let stale = false;
    setState((previous) => ({ ...previous, loading: true }));
    bridge
      .invoke<TData, TPayload>(module, type, { payload: payloadRef.current, scope })
      .then(
        (data) => { if (!stale) setState({ data, error: undefined, loading: false }); },
        // KEEP the previous data alongside the error, so a failed REFETCH does not blank a screen the
        // UI was showing correctly. The caller has both fields and decides which to render.
        (error: Error) => { if (!stale) setState((previous) => ({ data: previous.data, error, loading: false })); },
      );
    return () => { stale = true; };
  }, [module, type, scope, enabled, bridge, payloadKey, fetchToken]);

  const refetch = useCallback(() => setFetchToken((token) => token + 1), []);

  return { ...state, refetch };
}
