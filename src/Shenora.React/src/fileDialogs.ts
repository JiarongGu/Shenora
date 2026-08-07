/**
 * The page's half of the host's `SHENORA.DIALOGS` module — native file/folder/save dialogs, and the
 * capability gating that lets ONE bundle ship to every shell.
 *
 * Mirrors `Shenora.Ipc.FileDialogFacade`. The wire names here are pinned against the host's own
 * constants by `WireMirrorTests`, not by care.
 */
import { useMemo } from 'react';
import type { ShenoraBridge } from './bridge.js';
import { useShellInfo } from './hooks.js';
import { BaseModuleService } from './moduleService.js';
import { ShellCapabilities } from './types.js';

/** One dialog filter row (e.g. `{ name: 'Images', extensions: ['png', 'jpg'] }`). */
export interface FileDialogFilter {
  name: string;
  /** Extensions WITHOUT the dot or wildcard — `'png'`, not `'*.png'`. */
  extensions: string[];
}

/**
 * What EVERY dialog call takes. The per-dialog shapes below add what only that dialog can honour —
 * which is the point: a save-only field on a folder pick will not compile.
 */
export interface FileDialogOptions {
  /** Dialog title. Omit for a neutral per-dialog default. */
  title?: string;
  /** Start location when nothing is remembered. */
  defaultPath?: string;
  /**
   * Key under which the host remembers the last-used directory and restores it next time.
   * Remembered PER KEY, so "import" and "export" stay separate. Omit for no memory.
   */
  rememberPathKey?: string;
}

/** Inputs for {@link FileDialogs.openFile}. */
export interface OpenFileOptions extends FileDialogOptions {
  /** Filter rows; omit/empty = "All Files". */
  filters?: FileDialogFilter[];
  /** Initial file name shown in the dialog. */
  fileName?: string;
  /** Require the picked file to exist. Default true. Desktop hint — a mobile picker ignores it. */
  checkFileExists?: boolean;
  /** Require the path to exist. Default true. Desktop hint. */
  checkPathExists?: boolean;
  /** Validate the host's file-name rules. Default true. Desktop hint. */
  validateNames?: boolean;
}

/** Inputs for {@link FileDialogs.openFolder}. */
export interface OpenFolderOptions extends FileDialogOptions {
  /** Also allow picking a FILE. The host swaps in a file dialog with relaxed validation. */
  allowFileSelection?: boolean;
  /** Filter rows for the FILE half — ⚠ ignored unless {@link allowFileSelection} is set. */
  filters?: FileDialogFilter[];
}

/** Inputs for {@link FileDialogs.saveFile} and {@link FileDialogs.saveText}. */
export interface SaveFileOptions extends FileDialogOptions {
  /** Filter rows; omit/empty = "All Files". */
  filters?: FileDialogFilter[];
  /** The name being suggested. */
  fileName?: string;
  /** Extension appended when the user omits one (no dot). */
  defaultExtension?: string;
  /** Prompt before overwriting. Default true. Desktop hint. */
  overwritePrompt?: boolean;
  /** Require the path to exist. Default true. Desktop hint. */
  checkPathExists?: boolean;
}

/**
 * A dialog outcome. ⚠ **THREE outcomes, not two** — and the third is the one that surprises people:
 * cancelled (`success: false`); succeeded with an addressable location (`filePath` set); and
 * **succeeded with NO location**, which is what {@link FileDialogs.saveText} returns on a phone,
 * because the bytes went to a revocable grant rather than somewhere the app may reopen.
 *
 * So `success && filePath === undefined` is legitimate, and `result.filePath!` after checking
 * `success` is a null-reference waiting for a mobile shell.
 */
export interface FileDialogResult {
  success: boolean;
  filePath?: string;
}

// A plain interface — NOT `extends Record<string, unknown>`, which widens `keyof TRequests & string`
// back to `string`, so a mistyped route compiles and every payload collapses to `unknown`.
interface FileDialogRequests {
  OPEN_FILE: { options?: OpenFileOptions };
  OPEN_FOLDER: { options?: OpenFolderOptions };
  SAVE_FILE: { options?: SaveFileOptions };
  SAVE_TEXT: { text: string; options?: SaveFileOptions };
}

/**
 * Typed client for the host's `SHENORA.DIALOGS` module (`FileDialogFacade`).
 *
 * ⚠ **Two of these are DESKTOP capabilities.** `openFolder` and `saveFile` have no expression on a
 * phone and reject with `IpcErrorCodes.capabilityNotSupported` there. Do not catch that — ask first,
 * via {@link useFileDialogs}, and do not render the control at all. `openFile` and `saveText` work
 * everywhere.
 */
export class FileDialogs extends BaseModuleService<FileDialogRequests> {
  constructor(bridge?: ShenoraBridge) {
    super('SHENORA.DIALOGS', bridge);
  }

  /** Pick an existing file. Available on every shell. */
  openFile(options?: OpenFileOptions): Promise<FileDialogResult> {
    return this.send<FileDialogResult>('OPEN_FILE', { payload: { options } });
  }

  /**
   * Pick a folder. ⚠ DESKTOP only — gate on {@link FileDialogsHandle.canPickFolder}.
   *
   * On mobile "open folder" means the camera roll, the app's own space, or a scoped grant: the same
   * word with a different guarantee, which is why there is no portable version of it.
   */
  openFolder(options?: OpenFolderOptions): Promise<FileDialogResult> {
    return this.send<FileDialogResult>('OPEN_FOLDER', { payload: { options } });
  }

  /**
   * Pick a save destination and get the PATH back. ⚠ DESKTOP only — gate on
   * {@link FileDialogsHandle.canPickSavePath}. "Give me somewhere to write later" has no mobile
   * expression; use {@link saveText} in portable code, which also works on the desktop.
   */
  saveFile(options?: SaveFileOptions): Promise<FileDialogResult> {
    return this.send<FileDialogResult>('SAVE_FILE', { payload: { options } });
  }

  /**
   * Pick a destination AND write `text` to it, in one call — the PORTABLE save, working everywhere
   * because the HOST does the writing.
   *
   * ⚠ For text a page legitimately holds: an export, a report, a config. The content crosses the IPC
   * envelope as JSON, so anything large or binary should be produced host-side and saved through the
   * host's own `IFileDialogs.SaveAsync`, where it never enters a message.
   */
  saveText(text: string, options?: SaveFileOptions): Promise<FileDialogResult> {
    return this.send<FileDialogResult>('SAVE_TEXT', { payload: { text, options } });
  }
}

/** What {@link useFileDialogs} returns: the client, plus what this shell will actually honour. */
export interface FileDialogsHandle {
  /** The typed client. Stable across renders. */
  dialogs: FileDialogs;
  /** This shell can pick an existing file. */
  canPickFile: boolean;
  /** This shell can pick a FOLDER — desktop only (see {@link FileDialogs.openFolder}). */
  canPickFolder: boolean;
  /** This shell can return a save PATH — desktop only (see {@link FileDialogs.saveFile}). */
  canPickSavePath: boolean;
}

/**
 * The dialogs client together with what the CURRENT shell can honour — read from the ready
 * handshake, not sniffed from the platform (D36).
 *
 * ```tsx
 * const { dialogs, canPickFolder } = useFileDialogs();
 * return (
 *   <>
 *     <button onClick={() => dialogs.openFile()}>Choose a file</button>
 *     {canPickFolder && <button onClick={() => dialogs.openFolder()}>Choose a folder</button>}
 *   </>
 * );
 * ```
 *
 * ⚠ **Use these to decide what to RENDER, not what to catch.** A refused call rejects with
 * `IpcErrorCodes.capabilityNotSupported`, which is the honest answer to a question that should not
 * have been asked — the button should not have been there.
 *
 * ⚠ Every flag is `false` until the handshake has landed, so await `bridge.notifyReady()` before
 * rendering this tree — see {@link useShellInfo} for why the read is synchronous.
 */
export function useFileDialogs(dialogs?: FileDialogs): FileDialogsHandle {
  const shell = useShellInfo();
  // The service is stateless and holds only its module name, but a fresh instance per render would
  // still make it useless as an effect dependency — the same reason useWindowMaximized caches one.
  const client = useMemo(() => dialogs ?? new FileDialogs(), [dialogs]);
  const capabilities = shell?.capabilities;

  return useMemo(
    () => ({
      dialogs: client,
      canPickFile: capabilities?.includes(ShellCapabilities.filePicker) ?? false,
      canPickFolder: capabilities?.includes(ShellCapabilities.folderPicker) ?? false,
      canPickSavePath: capabilities?.includes(ShellCapabilities.savePicker) ?? false,
    }),
    [client, capabilities],
  );
}
