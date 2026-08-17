import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getBridge, type ShenoraBridge } from './bridge.js';
import { eventBus as defaultEventBus, type ShenoraEventBus } from './eventBus.js';
import type { EventMessage, ShellInfo } from './types.js';

/**
 * Host access for components: the (default) bridge and whether a host transport exists.
 *
 * ⚠ The result is MEMOIZED, and that is not a micro-optimisation. A fresh object every render is a
 * fresh dependency for every `useEffect`/`useMemo`/`useCallback` that lists it, so the natural
 * `const shenora = useShenora(); useEffect(…, [shenora])` re-runs on EVERY render — a subscribe/
 * unsubscribe cycle per frame in the worst case. The identity changes only when the bridge itself does,
 * or when `isAvailable` flips as a host attaches, which are the two moments a consumer means to react to.
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
 * SYNCHRONOUSLY from the bridge's cache, deliberately — a capability learned after layout is a visible
 * flash — which means this hook does not re-render when a handshake lands later. A component mounted
 * mid-handshake sees `undefined` and keeps seeing it, which is the documented "assume nothing" tree
 * rather than a wrong one, but it is not the tree you wanted.
 *
 * ⚠ This hook was referenced by two doc examples in this package for several releases before it
 * existed — `bridge.shell` was the only way to read it. If you wrote that workaround, this replaces it.
 */
export function useShellInfo(bridge?: ShenoraBridge): ShellInfo | undefined {
  return (bridge ?? getBridge()).shell;
}

/**
 * Subscribe to one (module, type) event for the component's lifetime, ported from the primary
 * desktop sibling. The handler receives the unwrapped payload (plus the full event). DEVIATION
 * from the source: instead of a deps array re-subscribing on change, the latest handler is kept
 * in a ref — no re-subscribe churn, no stale-closure trap.
 *
 * Pass `scope` for a scoped app: the wire carries a scope and the host keys on it, but this hook had
 * no way to express one, so a component in profile A also woke for profile B's events with no filter
 * available (P5.5 H6). Omitting it still means "every scope", and a global (scope-less) event still
 * reaches a scoped subscriber — the host's rule, mirrored.
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
        // KEEP the previous data alongside the error (P5.5 H2). This used to set `data: undefined`, so
        // a failed REFETCH — a transient host hiccup, one timed-out call — blanked data the UI was
        // already showing correctly, turning a recoverable error into an empty screen. The caller has
        // both fields and can decide: render stale data with an error banner, or hide it. Blanking it
        // for them removes that choice. (A first fetch has no previous data, so it is unaffected.)
        (error: Error) => { if (!stale) setState((previous) => ({ data: previous.data, error, loading: false })); },
      );
    return () => { stale = true; };
  }, [module, type, scope, enabled, bridge, payloadKey, fetchToken]);

  const refetch = useCallback(() => setFetchToken((token) => token + 1), []);

  return { ...state, refetch };
}
