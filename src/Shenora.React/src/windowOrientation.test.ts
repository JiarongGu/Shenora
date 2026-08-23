import { describe, expect, it } from 'vitest';
import { ShenoraBridge } from './bridge.js';
import { ShenoraEventBus } from './eventBus.js';
import { WindowOrientation } from './windowOrientation.js';
import { FakeTransport } from './testing/fakeTransport.js';

function createOrientation() {
  const transport = new FakeTransport();
  const bridge = new ShenoraBridge({ transport, eventBus: new ShenoraEventBus() });
  return { transport, orientation: new WindowOrientation(bridge) };
}

describe('WindowOrientation', () => {
  it('sends the two routes, and the orientation as the host spells it', () => {
    const { transport, orientation } = createOrientation();

    void orientation.lock('landscape');
    void orientation.unlock();

    expect(transport.posted.map((r) => `${r.module}.${r.type}`)).toEqual([
      'SHENORA.ORIENTATION.LOCK',
      'SHENORA.ORIENTATION.UNLOCK',
    ]);
    // 🔴 The host reads this as its `WindowOrientation` ENUM, whose wire form is camelCase — so a value
    // of any other shape is an InvalidPayloadValue at the boundary rather than a lock that does nothing.
    expect(transport.posted[0]?.payload).toEqual({ orientation: 'landscape' });
    expect(transport.posted[1]?.payload).toBeUndefined();
  });

  it('carries a refusal through rather than swallowing it', async () => {
    // The shell that cannot hold an orientation answers CAPABILITY_NOT_SUPPORTED. A page is expected to
    // branch on the capability instead — but if it calls anyway, the failure must reach it, because a
    // resolved promise would read as "locked" and the layout would be wrong with no way to find out.
    const { transport, orientation } = createOrientation();

    const pending = orientation.lock('portrait');
    transport.fail(transport.posted[0]!.id, 'CAPABILITY_NOT_SUPPORTED');

    await expect(pending).rejects.toMatchObject({ code: 'CAPABILITY_NOT_SUPPORTED' });
  });
});
