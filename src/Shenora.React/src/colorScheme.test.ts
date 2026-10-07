import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { ColorScheme } from './colorScheme.js';
import { FakeTransport } from './testing/fakeTransport.js';

function createScheme() {
  const transport = new FakeTransport();
  const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });
  return { transport, scheme: new ColorScheme(bridge) };
}

describe('ColorScheme', () => {
  it('reads the setting from its answer', async () => {
    const { transport, scheme } = createScheme();

    const pending = scheme.get();
    expect(transport.posted.map((r) => `${r.module}.${r.type}`)).toEqual(['SHENORA.COLOR_SCHEME.GET']);
    transport.respondToLast({ scheme: 'dark' });

    await expect(pending).resolves.toBe('dark');
  });

  it('sets it as the host spells its enum', () => {
    // The host reads this as its `ColorScheme` ENUM, camelCase on the wire: any other spelling is refused at the
    // boundary rather than leaving the scheme where it was.
    const { transport, scheme } = createScheme();

    void scheme.set('light');

    expect(transport.posted.map((r) => `${r.module}.${r.type}`)).toEqual(['SHENORA.COLOR_SCHEME.SET']);
    expect(transport.posted[0]?.payload).toEqual({ scheme: 'light' });
  });
});
