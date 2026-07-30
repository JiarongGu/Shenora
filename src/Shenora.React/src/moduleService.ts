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
    /** The bridge to speak over. Default: the shared default bridge. */
    protected readonly bridge: ShenoraBridge = getBridge(),
  ) {}

  /** Send one typed request to this module and await the typed response data. */
  protected send<TResponse = unknown, TType extends keyof TRequests & string = keyof TRequests & string>(
    type: TType,
    options: { payload?: TRequests[TType]; scope?: string; timeoutMs?: number } = {},
  ): Promise<TResponse> {
    return this.bridge.invoke<TResponse>(this.module, type, options);
  }
}
