// @vitest-environment jsdom
import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { configureBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { FakeTransport } from './testing/fakeTransport.js';
import { BACK_MODULE, BACK_PRESSED, useBackNavigation } from './backNavigation.js';
import { ShellCapabilities } from './types.js';

/**
 * 🔴 Every test here is about the SAME failure: a press that goes unanswered. The host holds it for a
 * timeout and then gives it to the platform, which on Android quits the app — so a hook that
 * unsubscribes at the wrong moment does not break back, it makes the app exit under the user.
 */
async function fixture(capabilities: string[] = [ShellCapabilities.backNavigation]) {
  const transport = new FakeTransport();
  transport.autoAck = true;
  const bus = new ShenoraEventBus();
  const bridge = configureBridge({ transport, eventBus: bus });

  // `useShellInfo` reads what the HANDSHAKE landed, so the capability has to arrive that way.
  transport.autoAck = false;
  const ready = bridge.notifyReady();
  await act(async () => {
    await Promise.resolve();
    transport.respondToLast({ name: 'android', capabilities });
    await ready;
  });
  transport.autoAck = true;
  return { transport, bus, bridge };
}

/** Publish a press the way the host's notification pump would. */
async function press(bus: ShenoraEventBus, token: string) {
  await act(async () => {
    await bus.emit({ module: BACK_MODULE, type: BACK_PRESSED, payload: { token } });
    await Promise.resolve();
  });
}

afterEach(() => {
  configureBridge({ transport: null, eventBus: new ShenoraEventBus() });
});

describe('useBackNavigation', () => {
  it('takes responsibility on mount and RELEASES it on unmount', async () => {
    const { transport, bus } = await fixture();

    const view = renderHook(() => useBackNavigation(() => true, { eventBus: bus }));
    await waitFor(() => expect(transport.routes()).toContain('INTERCEPT'));
    expect(transport.posted.at(-1)?.payload).toEqual({ enabled: true });

    view.unmount();
    // ⚠ Without the release, every later press is held for the host's whole timeout before the
    // platform gets it — the app appears to hang on back and then quits.
    await waitFor(() => expect(transport.posted.at(-1)?.payload).toEqual({ enabled: false }));
    expect(bus).toBeDefined();
  });

  it('answers a press with what the handler returned', async () => {
    const { transport, bus } = await fixture();
    renderHook(() => useBackNavigation(() => true, { eventBus: bus }));
    await waitFor(() => expect(transport.routes()).toContain('INTERCEPT'));

    await press(bus, 'b1');

    await waitFor(() => {
      const resolve = transport.posted.find((r) => r.type === 'RESOLVE');
      expect(resolve?.payload).toEqual({ token: 'b1', handled: true });
    });
  });

  it('answers NOT HANDLED when the handler throws, so the user can still leave', async () => {
    // 🔴 The safe direction. Swallowing the press on an app bug would be a back button that does
    // nothing, which no amount of page-side debugging reveals.
    const { transport, bus } = await fixture();
    renderHook(() =>
      useBackNavigation(() => {
        throw new Error('bug in the page');
      }, { eventBus: bus }),
    );
    await waitFor(() => expect(transport.routes()).toContain('INTERCEPT'));

    await press(bus, 'b7');

    await waitFor(() => {
      const resolve = transport.posted.find((r) => r.type === 'RESOLVE');
      expect(resolve?.payload).toEqual({ token: 'b7', handled: false });
    });
  });

  it('answers the token it was GIVEN, not the last one it saw', async () => {
    const { transport, bus } = await fixture();
    renderHook(() => useBackNavigation(() => false, { eventBus: bus }));
    await waitFor(() => expect(transport.routes()).toContain('INTERCEPT'));

    await press(bus, 'b1');
    await press(bus, 'b2');

    await waitFor(() => {
      const answered = transport.posted.filter((r) => r.type === 'RESOLVE').map((r) => r.payload);
      expect(answered).toEqual([
        { token: 'b1', handled: false },
        { token: 'b2', handled: false },
      ]);
    });
  });

  it('uses the LATEST handler without resubscribing, so a press in the gap is never lost', async () => {
    // 🔴 The reason the handler lives in a ref. An inline arrow is a new function every render, so a
    // hook that depended on it would unsubscribe and resubscribe on each one — and a press landing in
    // that window is answered by nobody, i.e. the app quits mid-interaction.
    const { transport, bus } = await fixture();
    let answer = false;
    const view = renderHook(() => useBackNavigation(() => answer, { eventBus: bus }));
    await waitFor(() => expect(transport.routes()).toContain('INTERCEPT'));

    answer = true;
    view.rerender();

    // Exactly ONE intercept, even after the re-render — the subscription was never torn down.
    expect(transport.posted.filter((r) => r.type === 'INTERCEPT')).toHaveLength(1);

    await press(bus, 'b3');
    await waitFor(() => {
      const resolve = transport.posted.find((r) => r.type === 'RESOLVE');
      expect(resolve?.payload).toEqual({ token: 'b3', handled: true });
    });
  });

  it('does NOTHING on a shell with no back gesture', async () => {
    // Asking to intercept where nothing is ever raised looks exactly like a broken handler, so the
    // hook declines to ask at all and reports `supported: false` for the page to branch on.
    const { transport, bus } = await fixture([ShellCapabilities.filePicker]);

    const view = renderHook(() => useBackNavigation(() => true, { eventBus: bus }));

    expect(view.result.current.supported).toBe(false);
    expect(transport.routes()).not.toContain('INTERCEPT');
  });
});
