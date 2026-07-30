import type { IpcError } from './types.js';

/**
 * The rejection type for every failed bridge call — the client mirror of the host's
 * `OperationException` (the `Error` suffix is the TS idiom; the C# type keeps the platform's
 * `Exception` suffix). Carries the structured code + parameters so callers translate
 * `errors.{code}` instead of matching message strings. Client-side failures (timeout, missing
 * transport) reject through this same shape with the client-reserved codes.
 */
export class OperationError extends Error {
  /** Error code / i18n key (e.g. `"IMPORT_FAILED"`, `"TIMEOUT"`). */
  readonly code: string;

  /** Values the client interpolates into the translated message. */
  readonly parameters?: Record<string, string>;

  constructor(error: IpcError) {
    super(error.message ?? error.code);
    this.name = 'OperationError';
    this.code = error.code;
    this.parameters = error.parameters;
  }
}
