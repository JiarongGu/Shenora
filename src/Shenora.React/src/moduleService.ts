import { getBridge, type ShenoraBridge } from './bridge.js';

/**
 * Base class for typed module services, ported from the primary desktop sibling: each backend
 * module gets one service subclass that binds the module name once and exposes app-typed
 * methods over {@link send}. Bind `TRequests` to the module's request map
 * (`{ [type]: payloadType }`) for compile-time payload checking:
 *
 * ```ts
 * interface TodoRequests { GET_ALL: void; ADD: { title: string } }
 * class TodoService extends BaseModuleService<TodoRequests> {
 *   constructor() { super('TODO'); }
 *   getAll() { return this.send<TodoItem[]>('GET_ALL'); }
 *   add(title: string) { return this.send<TodoItem>('ADD', { payload: { title } }); }
 * }
 * ```
 *
 * DEVIATION from the source: its boolean/array/optional convenience wrappers were pure casts
 * around the same call — the response generic already expresses them, so they're gone.
 */
export abstract class BaseModuleService<TRequests extends Record<string, unknown> = Record<string, unknown>> {
  protected constructor(
    /** The backend module this service fronts (e.g. `"TODO"`). */
    protected readonly module: string,
    /**
     * The bridge to speak over. Omit to use the shared default bridge, resolved PER CALL — see
     * {@link bridge}.
     */
    private readonly explicitBridge?: ShenoraBridge,
  ) {}

  /**
   * The bridge this service speaks over — resolved on every access, never captured.
   *
   * This used to be a constructor default (`bridge: ShenoraBridge = getBridge()`), which is evaluated
   * at CONSTRUCTION: a service built before `configureBridge()` captured the old default, and
   * `configureBridge` DISPOSES the bridge it replaces — so every later call from that service
   * rejected with "Bridge disposed" for the rest of the session, with nothing to suggest why (P5.5
   * H2). Module services are commonly module-level singletons, so constructing one before the app's
   * startup configuration ran is the normal case, not an edge case. `useDropZone` already resolved
   * lazily for exactly this reason; this matches it.
   */
  protected get bridge(): ShenoraBridge {
    return this.explicitBridge ?? getBridge();
  }

  /** Send one typed request to this module and await the typed response data. */
  protected send<TResponse = unknown, TType extends keyof TRequests & string = keyof TRequests & string>(
    type: TType,
    options: { payload?: TRequests[TType]; scope?: string; timeoutMs?: number } = {},
  ): Promise<TResponse> {
    return this.bridge.invoke<TResponse>(this.module, type, options);
  }
}
