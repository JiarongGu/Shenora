import { getBridge, type ShenoraBridge } from './bridge.js';

/**
 * Base class for typed module services: each backend module gets one service subclass that binds the
 * module name once and exposes app-typed methods over {@link send}. Bind `TRequests` to the module's
 * request map (`{ [type]: payloadType }`) for compile-time payload checking:
 *
 * ```ts
 * interface NoteRequests { GET_ALL: void; ADD: { title: string } }
 * class NoteService extends BaseModuleService<NoteRequests> {
 *   constructor() { super('NOTES'); }
 *   getAll(): Promise<Note[]> { return this.send('GET_ALL'); }
 *   add(title: string): Promise<Note> { return this.send('ADD', { payload: { title } }); }
 * }
 * ```
 *
 * 🔴 **DECLARE THE RETURN TYPE; NEVER WRITE `send<Note>(…)`.** TypeScript has no partial type-argument
 * inference, so naming the response argument makes `TType` fall back to its DEFAULT — the union of every
 * key — and `payload` collapses to the union of every route's payload. The check silently stops
 * checking: `send<Note>('ADD', { payload: { notAField: 1 } })` compiles clean, while the same call
 * without the type argument is a TS2353. The response is inferred from the declared return type instead.
 *
 * ⚠ `TRequests extends object`, never `extends Record<string, unknown>`: the stricter bound is
 * unsatisfiable by a plain `interface` (TS2344 on the example above), and satisfying it by widening the
 * request map widens `keyof TRequests & string` back to `string` — turning the payload check off again.
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
   * 🔴 Never default it at CONSTRUCTION. Module services are commonly module-level singletons, so one
   * is routinely built before `configureBridge()` runs — and `configureBridge` DISPOSES the bridge it
   * replaces, so a captured default makes every later call reject with "Bridge disposed" for the rest
   * of the session, with nothing to suggest why.
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
