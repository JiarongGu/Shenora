// @vitest-environment jsdom
import { act, render, screen } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { createShenoraStore } from './store.js';
import { FakeTransport } from './testing/fakeTransport.js';
import { IpcCategories, type IpcNotificationBatch } from './types.js';

interface DeployState {
  status: string;
  lines: string[];
}

function fixture(options: { snapshot?: boolean } = {}) {
  const transport = new FakeTransport();
  const bus = new ShenoraEventBus();
  const bridge = new ShenoraBridge({ transport, eventBus: bus });
  const onError = vi.fn();

  const useDeploy = createShenoraStore<DeployState, { start: () => string }>('DEPLOY', {
    initial: { status: 'idle', lines: [] },
    snapshot: options.snapshot
      ? {
        type: 'GET_STATE',
        apply: (state, data) => ({ ...state, ...(data as Partial<DeployState>) }),
      }
      : undefined,
    on: {
      PROGRESS: (state, payload: { line: string }) => ({ ...state, lines: [...state.lines, payload.line] }),
      ENDED: (state, payload: { ok: boolean }) => ({ ...state, status: payload.ok ? 'done' : 'failed' }),
      BOOM: () => { throw new Error('reducer blew up'); },
    },
    actions: ({ post }) => ({ start: () => post('START', { payload: { env: 'prod' } }) }),
    bridge,
    bus,
    onError,
  });

  /** Push one host notification, as the bridge would after unbundling a batch. */
  const emit = (type: string, payload: unknown): void => {
    const batch: IpcNotificationBatch = {
      category: IpcCategories.notification,
      id: 'n1',
      payload: [{ module: 'DEPLOY', type, payload }],
      timestamp: new Date(0).toISOString(),
    };
    act(() => transport.emitFromHost(batch));
  };

  return { transport, bus, bridge, useDeploy, onError, emit };
}

describe('createShenoraStore', () => {
  it('folds host events into state that every component reads', () => {
    const { useDeploy, emit } = fixture();
    function Panel(): ReactNode {
      const lines = useDeploy((s) => s.lines);
      return createElement('div', { 'data-testid': 'panel' }, lines.join('|'));
    }
    function Strip(): ReactNode {
      const status = useDeploy((s) => s.status);
      return createElement('div', { 'data-testid': 'strip' }, status);
    }
    render(createElement('div', null, createElement(Panel), createElement(Strip)));

    emit('PROGRESS', { line: 'one' });
    emit('PROGRESS', { line: 'two' });
    emit('ENDED', { ok: true });

    // The two components in the reference apps: a full panel and a compact progress strip, both
    // reflecting the same live state without either re-implementing the wiring.
    expect(screen.getByTestId('panel').textContent).toBe('one|two');
    expect(screen.getByTestId('strip').textContent).toBe('done');
  });

  it('🔴 re-renders a Set-valued selector — a shallow compare calls EVERY Set equal', () => {
    // `Object.keys(new Set([1, 2]))` is `[]`, so two Sets of different contents both present zero own
    // keys, the lengths match, and `[].every(...)` is vacuously true. The selector is then pinned to its
    // first value for the component's life, with no warning: React is simply told nothing changed.
    // Same shape for Map, Date and any class instance whose state lives in private/internal slots.
    const { useDeploy, emit } = fixture();
    function Tags(): ReactNode {
      const unique = useDeploy((s) => new Set(s.lines));
      return createElement('div', { 'data-testid': 'tags' }, [...unique].join('|'));
    }
    render(createElement(Tags));

    emit('PROGRESS', { line: 'one' });
    expect(screen.getByTestId('tags').textContent).toBe('one');

    emit('PROGRESS', { line: 'two' });
    expect(screen.getByTestId('tags').textContent).toBe('one|two');
  });

  it('🔴 re-renders a Date-valued selector, which the same vacuous compare pins', () => {
    // Dates carry their value in an internal slot, so `Object.keys` is `[]` for every Date ever made.
    const { useDeploy, emit } = fixture();
    function Stamp(): ReactNode {
      const at = useDeploy((s) => new Date(s.lines.length * 1000));
      return createElement('div', { 'data-testid': 'stamp' }, at.toISOString());
    }
    render(createElement(Stamp));

    emit('PROGRESS', { line: 'one' });
    expect(screen.getByTestId('stamp').textContent).toBe(new Date(1000).toISOString());
  });

  it('opens ONE subscription per event type no matter how many components read it', () => {
    const { useDeploy, bus, emit } = fixture();
    const subscribe = vi.spyOn(bus, 'subscribe');
    function Reader(): ReactNode {
      useDeploy((s) => s.status);
      return null;
    }
    const view = render(createElement('div', null,
      createElement(Reader), createElement(Reader), createElement(Reader)));

    // Three readers, three declared event types — three subscriptions total, not nine. This is the
    // property the whole primitive exists for: status UI in an app is inherently many-watchers.
    expect(subscribe).toHaveBeenCalledTimes(3);

    view.unmount();

    // …and the last component leaving tears them down. Asserted BEHAVIOURALLY — an earlier version
    // of this test counted the spy's return values, which is 3 whether or not anything unsubscribed:
    // a vacuous assertion. A detached store must not react to a host event at all.
    const before = useDeploy.getState();
    emit('ENDED', { ok: true });
    expect(useDeploy.getState()).toBe(before);
  });

  it('gives a LATE-mounting component the state it missed', async () => {
    // THE case this primitive exists for, and the one the first design draft would have shipped
    // without: a progress strip mounts when its tab is opened, long after the work started. Events
    // it was not present for cannot be replayed, so the store snapshots on first subscription.
    const { useDeploy, transport } = fixture({ snapshot: true });
    function Late(): ReactNode {
      const status = useDeploy((s) => s.status);
      return createElement('div', { 'data-testid': 'late' }, status);
    }

    render(createElement(Late));
    // The snapshot request went out on the FIRST subscriber…
    expect(transport.lastRequest().type).toBe('GET_STATE');
    // …and its answer becomes the state this component renders, despite it having missed everything.
    await act(async () => {
      transport.respondToLast({ status: 'running', lines: ['already', 'happened'] });
    });

    expect(screen.getByTestId('late').textContent).toBe('running');
    expect(useDeploy.getState().lines).toEqual(['already', 'happened']);
  });

  it('re-snapshots when a component mounts after every subscriber left', async () => {
    // Between subscriber epochs the store is OFF the bus, so anything the host emitted in the gap
    // is gone — and an entry the host evicted in that window can never be corrected by a later
    // event. The once-only flag dedupes same-tick double mounts (StrictMode); it must not make the
    // first snapshot permanent across epochs.
    const { useDeploy, transport } = fixture({ snapshot: true });
    function Reader(): ReactNode {
      const status = useDeploy((s) => s.status);
      return createElement('div', { 'data-testid': 'reader' }, status);
    }

    const first = render(createElement(Reader));
    await act(async () => {
      transport.respondToLast({ status: 'running', lines: [] });
    });
    first.unmount(); // the last subscriber leaves; the bus subscription is gone

    render(createElement(Reader));
    // Asked AGAIN — counted, because lastRequest() would still show the first ask if no second went out.
    expect(transport.routes().filter((type) => type === 'GET_STATE')).toHaveLength(2);
    await act(async () => {
      transport.respondToLast({ status: 'done', lines: [] });
    });
    expect(screen.getByTestId('reader').textContent).toBe('done');
  });

  it('drops a previous epoch’s late snapshot answer instead of clobbering the fresh one', async () => {
    // R1 goes out, the subscriber leaves before it answers, a new mount fires R2. R2's answer lands
    // first; R1's arrives late. Applying R1 would be a lost update wearing a success path — the
    // stale body silently overwriting state a demonstrably fresher fetch already applied.
    const { useDeploy, transport } = fixture({ snapshot: true });
    function Reader(): ReactNode {
      const status = useDeploy((s) => s.status);
      return createElement('div', { 'data-testid': 'reader' }, status);
    }

    const first = render(createElement(Reader));
    const r1 = transport.lastRequest();
    first.unmount(); // R1 still unanswered

    render(createElement(Reader));
    const r2 = transport.lastRequest();
    await act(async () => {
      transport.respond(r2.id, { status: 'done', lines: [] });
    });
    expect(screen.getByTestId('reader').textContent).toBe('done');

    await act(async () => {
      transport.respond(r1.id, { status: 'running', lines: [] }); // the dead epoch answers late…
    });
    expect(screen.getByTestId('reader').textContent).toBe('done'); // …and is ignored
  });

  it('snapshots ONCE even when several components mount together', async () => {
    const { useDeploy, transport } = fixture({ snapshot: true });
    function Reader(): ReactNode {
      useDeploy();
      return null;
    }
    render(createElement('div', null, createElement(Reader), createElement(Reader)));

    // React StrictMode double-invokes effects and two components mount in one tick; neither may
    // duplicate the request. The guard is set BEFORE the await for exactly this.
    expect(transport.posted.filter((r) => r.type === 'GET_STATE')).toHaveLength(1);
    await act(async () => transport.respondToLast({ status: 'running', lines: [] }));
    expect(useDeploy.getState().status).toBe('running');
  });

  it('reports a throwing reducer without corrupting shared state', () => {
    const { useDeploy, onError, emit } = fixture();
    function Reader(): ReactNode {
      useDeploy();
      return null;
    }
    render(createElement(Reader));
    emit('PROGRESS', { line: 'kept' });

    emit('BOOM', {});

    // App code runs inside a host-event callback; a throw there must be reported, not allowed to
    // break the other subscribers or leave the store half-updated (the guarded-callback rule).
    expect(onError).toHaveBeenCalledTimes(1);
    expect(useDeploy.getState().lines).toEqual(['kept']);
  });

  it('exposes actions that post fire-and-forget to the store module', () => {
    const { useDeploy, transport } = fixture();

    const id = useDeploy.actions.start();

    const request = transport.lastRequest<{ env: string }>();
    expect(request.module).toBe('DEPLOY');
    expect(request.type).toBe('START');
    expect(request.payload).toEqual({ env: 'prod' });
    // The id is returned so a caller CAN correlate; nothing is awaited and no timer was set.
    expect(request.id).toBe(id);
  });

  it('does not re-render a reader whose selected slice did not change', () => {
    const { useDeploy, emit } = fixture();
    let renders = 0;
    function Reader(): ReactNode {
      renders++;
      useDeploy((s) => s.status);
      return null;
    }
    render(createElement(Reader));
    const before = renders;

    emit('PROGRESS', { line: 'x' }); // changes `lines`, not the selected `status`

    expect(renders).toBe(before);
  });

  it('caches a selector that derives a NEW object, instead of looping', () => {
    // THE case the memoization exists for, and the one a primitive-returning selector cannot prove:
    // `useSyncExternalStore` calls getSnapshot during render and compares with Object.is, so a
    // selector building a fresh object every call never compares equal — React bails out with
    // "The result of getSnapshot should be cached (...)" and re-renders without end.
    // An earlier version of this test selected a string, which passes with or without the cache.
    const { useDeploy, emit } = fixture();
    let renders = 0;
    function Reader(): ReactNode {
      renders++;
      const view = useDeploy((s) => ({ count: s.lines.length }));
      return createElement('div', { 'data-testid': 'derived' }, String(view.count));
    }
    render(createElement(Reader));

    emit('PROGRESS', { line: 'one' });

    expect(screen.getByTestId('derived').textContent).toBe('1');
    // One mount + one update. Unbounded here is the failure mode, so the bound is the assertion.
    expect(renders).toBeLessThanOrEqual(4);
  });

  it('a selector whose CLOSURE changed returns the new value, not the previous one', () => {
    // 🔴 THE OTHER HALF, and the one the state-keyed cache got wrong. The store is untouched between
    // renders, so a cache keyed on STATE identity hit and handed back the PREVIOUS selector's answer —
    // a virtualised list row reused for a different `id` rendered the previous row's data until some
    // unrelated event happened to replace the state.
    const { useDeploy, emit } = fixture();

    function Reader({ index }: { index: number }): ReactNode {
      // The closure changes with the prop; the STATE does not change at all between the two renders.
      const line = useDeploy((s) => s.lines[index]);
      return createElement('div', { 'data-testid': 'line' }, String(line));
    }

    const view = render(createElement(Reader, { index: 0 }));
    // Fill the store while mounted — the store only subscribes once a component is reading it.
    emit('PROGRESS', { line: 'alpha' });
    emit('PROGRESS', { line: 'beta' });
    expect(screen.getByTestId('line').textContent).toBe('alpha');

    // ⚠ No emit between here and the assertion below: the STATE is identical across the two renders,
    // which is precisely the condition a state-keyed cache answered stale.
    view.rerender(createElement(Reader, { index: 1 }));
    expect(screen.getByTestId('line').textContent).toBe('beta');
  });
});
