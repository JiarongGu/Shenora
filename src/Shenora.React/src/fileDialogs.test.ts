// @vitest-environment jsdom
import { renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { ShenoraBridge, configureBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { FileDialogs, useFileDialogs } from './fileDialogs.js';
import { FakeTransport } from './testing/fakeTransport.js';
import { ShellCapabilities } from './types.js';

/**
 * `fileDialogs.ts` was the only module in this package with **0 % function coverage** — every other file
 * sat above 89 %. `WireMirrorTests` pins its route strings and its option shapes against the host, so
 * what was unpinned is the RUNTIME half: the payload each method actually builds, and the capability
 * gating `useFileDialogs` performs.
 *
 * The gating is the part worth the test. Three flags read three DIFFERENT `ShellCapabilities` constants,
 * and a copy-paste between them is invisible: the app renders a folder button on a phone that always
 * rejects, or hides a working one on the desktop. Nothing else would notice.
 */
function createDialogs() {
  const transport = new FakeTransport();
  const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });
  return { transport, dialogs: new FileDialogs(bridge) };
}

/** Drive the real handshake so `bridge.shell` is populated the way a live host populates it. */
async function configureShell(capabilities: string[]): Promise<void> {
  const transport = new FakeTransport();
  const bridge = configureBridge({ transport, eventBus: new ShenoraEventBus() });
  const ready = bridge.notifyReady();
  transport.respondToLast({ name: 'probe', capabilities });
  await ready;
}

afterEach(() => {
  // Leave no ambient bridge behind for the next file — `configureBridge` disposes the previous one.
  configureBridge({ transport: null, eventBus: new ShenoraEventBus() });
});

describe('FileDialogs', () => {
  it('sends the four dialog routes on the reserved module', async () => {
    const { transport, dialogs } = createDialogs();

    void dialogs.openFile();
    void dialogs.openFolder();
    void dialogs.saveFile();
    void dialogs.saveText('hello');

    expect(transport.posted.map((r) => `${r.module}.${r.type}`)).toEqual([
      'SHENORA.DIALOGS.OPEN_FILE',
      'SHENORA.DIALOGS.OPEN_FOLDER',
      'SHENORA.DIALOGS.SAVE_FILE',
      'SHENORA.DIALOGS.SAVE_TEXT',
    ]);
  });

  it('carries the options through, and saveText carries the text beside them', async () => {
    const { transport, dialogs } = createDialogs();

    void dialogs.openFile({ title: 'Pick', filters: [{ name: 'Images', extensions: ['png'] }] });
    expect(transport.posted[0]?.payload).toEqual({
      options: { title: 'Pick', filters: [{ name: 'Images', extensions: ['png'] }] },
    });

    // ⚠ `text` is a SIBLING of `options`, not a field inside it — the one route whose payload is not
    // simply `{ options }`, and the shape the host's SAVE_TEXT reads.
    void dialogs.saveText('body', { fileName: 'report.txt' });
    expect(transport.posted[1]?.payload).toEqual({ text: 'body', options: { fileName: 'report.txt' } });
  });

  it('an omitted options argument still posts a payload the host can read', async () => {
    const { transport, dialogs } = createDialogs();

    void dialogs.openFile();

    // `{ options: undefined }` serializes to `{}` — the host's options are optional, so this is the
    // "no options" wire form rather than a missing payload.
    expect(transport.posted[0]?.payload).toEqual({});
  });

  it('unwraps the host answer, including a success with NO path (the mobile saveText case)', async () => {
    const { transport, dialogs } = createDialogs();

    const picked = dialogs.openFile();
    transport.respondToLast({ success: true, filePath: 'C:/tmp/a.png' });
    await expect(picked).resolves.toEqual({ success: true, filePath: 'C:/tmp/a.png' });

    // The third outcome the JSDoc calls out: succeeded, but the bytes went to a revocable grant, so
    // there is no location to hand back. `result.filePath!` after checking `success` is the bug.
    const saved = dialogs.saveText('body');
    transport.respondToLast({ success: true });
    await expect(saved).resolves.toEqual({ success: true });
  });
});

describe('useFileDialogs', () => {
  it('maps each capability to its OWN flag', async () => {
    // Only the folder picker, so a flag reading the wrong constant cannot pass by coincidence — which
    // is exactly what an all-three or none-of-three fixture would allow.
    await configureShell([ShellCapabilities.folderPicker]);

    const { result } = renderHook(() => useFileDialogs());

    expect(result.current.canPickFolder).toBe(true);
    expect(result.current.canPickFile).toBe(false);
    expect(result.current.canPickSavePath).toBe(false);
  });

  it('reports the desktop trio when the shell advertises all three', async () => {
    await configureShell([
      ShellCapabilities.filePicker,
      ShellCapabilities.folderPicker,
      ShellCapabilities.savePicker,
    ]);

    const { result } = renderHook(() => useFileDialogs());

    expect(result.current.canPickFile).toBe(true);
    expect(result.current.canPickFolder).toBe(true);
    expect(result.current.canPickSavePath).toBe(true);
  });

  /**
   * ⚠ **Absent means "assume nothing", never "assume desktop"** — the rule `useShellInfo` documents. A
   * plain browser tab, a host predating the handshake, or a handshake still in flight all land here, and
   * every flag must read false so the app renders no control it cannot honour.
   */
  it('is all-false before any handshake has landed', () => {
    const { result } = renderHook(() => useFileDialogs());

    expect(result.current.canPickFile).toBe(false);
    expect(result.current.canPickFolder).toBe(false);
    expect(result.current.canPickSavePath).toBe(false);
  });

  it('keeps ONE client across renders, so it is usable as an effect dependency', async () => {
    await configureShell([ShellCapabilities.filePicker]);

    const { result, rerender } = renderHook(() => useFileDialogs());
    const first = result.current.dialogs;
    rerender();

    expect(result.current.dialogs).toBe(first);
  });

  it('uses a caller-supplied client rather than minting one', async () => {
    await configureShell([ShellCapabilities.filePicker]);
    const { dialogs } = createDialogs();

    const { result } = renderHook(() => useFileDialogs(dialogs));

    expect(result.current.dialogs).toBe(dialogs);
  });
});
