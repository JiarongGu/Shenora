import { getBridge, type ShenoraBridge } from './bridge.js';

/**
 * Base class for typed module services, ported from the primary desktop sibling: each backend
 * module gets one service subclass that binds the module name once and exposes app-typed
 * methods over {@link send}. Bind `TRequests` to the module's request map
 * (`{ [type]: payloadType }`) for compile-time payload checking:
 *
 * ```ts
 * interface NoteRequests { GET_ALL: void; ADD: { title: string } }
 * class NoteService extends BaseModuleService<NoteRequests> {
 *   constructor() { super('NOTES'); }
 *   getAll() { return this.send<Note[]>('GET_ALL'); }
 *   add(title: string) { return this.send<Note>('ADD', { payload: { title } }); }
 * }
 * ```
 *
 * DEVIATION from the source: its boolean/array/optional convenience wrappers were pure casts
 * around the same call — the response generic already expresses them, so they're gone.
 *
 * `TRequests extends object`, NOT `extends Record<string, unknown>` (P5.5 H6). The stricter bound was
 * unsatisfiable by a plain `interface` — interfaces get no implicit index signature — so the example
 * above and the README's snippet both failed with TS2344, on the first line an adopter copies. And
 * satisfying it the way the kit's own `windowCommands.ts` did (`interface X extends Record<string,
 * unknown>`) widened `keyof TRequests & string` back to `string`, so a mistyped request type compiled
 * and every payload collapsed to `unknown` — the flagship typed-service feature checking nothing at all.
 */
export abstract class BaseModuleService<TRequests extends object = Record<string, unknown>> {
  protected constructor(
    /** The backend module this service fronts (e.g. `"NOTES"`). */
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
