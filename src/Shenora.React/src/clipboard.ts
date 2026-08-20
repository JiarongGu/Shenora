/**
 * The page's half of the host's `SHENORA.CLIPBOARD` module — the parts of the native clipboard the web
 * platform withholds. Mirrors `Shenora.Modules.Clipboard.ClipboardModule`, pinned by `WireMirrorTests`.
 *
 * 🔴 **Reach for `navigator.clipboard` first.** A gesture-driven "copy this text" or "copy this image"
 * already works and needs no host round trip. Two things it cannot do are why this module exists —
 *
 * 1. **Files.** No web API puts a file list on the clipboard, so the user cannot paste into Explorer,
 *    Finder or a file manager. There is no polyfill.
 * 2. **Access with no user gesture or focus.** `navigator.clipboard.read()` needs transient activation,
 *    document focus and a permission. A host has none of those constraints.
 *
 * ⚠ **The choice is per-COPY, not per-format**: a clipboard set is atomic, one item, last writer wins.
 * An item that includes files must be written entirely through {@link ClipboardAccess.write} — writing
 * its text half with `navigator.clipboard` and the files here leaves only the files, silently.
 */
import { useMemo } from 'react';
import type { ShenoraBridge } from './bridge.js';
import { useShellInfo } from './hooks.js';
import { BaseModuleService } from './moduleService.js';
import { ShellCapabilities } from './types.js';

/** Media type for PNG bytes — the interchange image format every platform and browser reads. */
export const PNG_IMAGE = 'image/png';

/** Media type for UTF-8 HTML, for a paste that keeps its formatting. */
export const HTML = 'text/html';

/**
 * One clipboard item and every representation it offers. `text` and `files` are named because every
 * platform has a first-class API for them; everything else lives in `formats`, keyed by media type —
 * {@link PNG_IMAGE}, {@link HTML}, or your own `application/…` type, which the host carries verbatim.
 */
export interface ClipboardContent {
  /** The plain-text representation. */
  text?: string;
  /**
   * Absolute paths, for the copy a file manager can paste.
   * ⚠ DESKTOP only — gate on {@link ClipboardHandle.canCopyFiles}.
   */
  files?: string[];
  /** Every other representation, as raw bytes keyed by media type. */
  formats?: Record<string, Uint8Array>;
}

/** What actually crosses the wire: the same shape with the byte payloads base64-encoded. */
interface ClipboardWire {
  text?: string;
  files?: string[];
  formats?: Record<string, string>;
}

interface ClipboardRequests {
  READ: undefined;
  WRITE: { content: ClipboardWire };
  CLEAR: undefined;
}

/**
 * Base64 without a `Buffer`.
 *
 * ⚠ Never `btoa(String.fromCharCode(...bytes))`: the spread overflows the argument list and throws
 * `RangeError` on anything past roughly a small screenshot. A chunked loop has no such ceiling.
 */
function toBase64(bytes: Uint8Array): string {
  let binary = '';
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

function fromBase64(value: string): Uint8Array {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i += 1) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Typed client for the host's `SHENORA.CLIPBOARD` module (`ClipboardModule`).
 *
 * ⚠ **`files` is a DESKTOP capability** and rejects with `IpcErrorCodes.capabilityNotSupported` on a
 * phone. Do not catch that — ask first, via {@link useClipboard}, and do not render the control.
 */
export class ClipboardAccess extends BaseModuleService<ClipboardRequests> {
  constructor(bridge?: ShenoraBridge) {
    super('SHENORA.CLIPBOARD', bridge);
  }

  /**
   * Everything the clipboard is offering — **no user gesture, no permission prompt, no focus
   * requirement**, unlike `navigator.clipboard.read()`.
   */
  async read(): Promise<ClipboardContent> {
    const wire: ClipboardWire = await this.send('READ');
    const formats: Record<string, Uint8Array> = {};
    for (const [mediaType, value] of Object.entries(wire.formats ?? {})) {
      formats[mediaType] = fromBase64(value);
    }
    return { text: wire.text, files: wire.files, formats };
  }

  /**
   * Replace the clipboard with one item, every representation at once.
   *
   * ⚠ Bytes cross the IPC envelope as base64, so a large picture is a large message. A page copying
   * something big should hand the host a path and let it read the file instead.
   */
  write(content: ClipboardContent): Promise<void> {
    const formats: Record<string, string> = {};
    for (const [mediaType, bytes] of Object.entries(content.formats ?? {})) {
      formats[mediaType] = toBase64(bytes);
    }
    return this.send('WRITE', {
      payload: { content: { text: content.text, files: content.files, formats } },
    });
  }

  /** Leave the clipboard holding nothing. */
  clear(): Promise<void> {
    return this.send('CLEAR');
  }
}

/** What {@link useClipboard} returns: the client, plus what this shell will actually honour. */
export interface ClipboardHandle {
  /** The typed client. Stable across renders. */
  clipboard: ClipboardAccess;
  /** This shell can put a FILE LIST on the clipboard — desktop only. Use it to decide what to RENDER. */
  canCopyFiles: boolean;
}

/**
 * The clipboard client together with what the CURRENT shell can honour — read from the ready
 * handshake, not sniffed from the platform (D36).
 *
 * ```tsx
 * const { clipboard, canCopyFiles } = useClipboard();
 * return (
 *   <>
 *     <button onClick={() => navigator.clipboard.writeText(name)}>Copy name</button>
 *     {canCopyFiles && (
 *       <button onClick={() => clipboard.write({ text: name, files: [path] })}>Copy file</button>
 *     )}
 *   </>
 * );
 * ```
 *
 * ⚠ Note the first button: plain text on a click is the browser's job and stays there. This hook is for
 * the file copy beside it.
 *
 * ⚠ `canCopyFiles` is `false` until the handshake has landed, so await `bridge.notifyReady()` before
 * rendering this tree — see {@link useShellInfo} for why the read is synchronous.
 */
export function useClipboard(clipboard?: ClipboardAccess): ClipboardHandle {
  const shell = useShellInfo();
  // Cached so it is usable as an effect dependency, not because the service is expensive.
  const client = useMemo(() => clipboard ?? new ClipboardAccess(), [clipboard]);
  const capabilities = shell?.capabilities;

  return useMemo(
    () => ({
      clipboard: client,
      canCopyFiles: capabilities?.includes(ShellCapabilities.clipboardFiles) ?? false,
    }),
    [client, capabilities],
  );
}
