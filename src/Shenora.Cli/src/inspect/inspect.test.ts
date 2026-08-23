// The inspect service's claims, and one of them is a security boundary.
import { describe, it, expect, afterEach } from 'vitest';
import http from 'node:http';
import { createInspectService, InspectState, isLoopback, plainAddress, lanAddresses } from './service.js';
import { inspectPage } from './page.js';
import { positionals, cmdInspectEval, warnIfShellRewrote } from './commands.js';

const servers: http.Server[] = [];
afterEach(() => {
  for (const s of servers.splice(0)) s.close();
});

/** Start a service bound to every interface, so a request can arrive from off-loopback. */
async function serve(): Promise<{ port: number; state: InspectState }> {
  const state = new InspectState();
  const { server } = createInspectService({ state, page: () => inspectPage() });
  servers.push(server);
  await new Promise<void>((resolve) => server.listen(0, '0.0.0.0', resolve));
  const address = server.address();
  if (typeof address === 'string' || address === null) throw new Error('no port');
  // ⚠ Says what happened. Under a FULL `verify` — where the .NET build and 1,792 tests saturate the box
  // first — this has come back as 0 despite the listen callback having fired, and the only symptom was
  // `fetch failed / Caused by: bad port` from a URL reading `:0`, which reads as a broken fetch rather
  // than a bind that did not take. Isolated, this suite passed 8/8.
  if (!address.port) throw new Error('the test server reported port 0 — the bind did not take');
  return { port: address.port, state };
}

const get = async (host: string, port: number, path: string): Promise<number> =>
  (await fetch(`http://${host}:${port}${path}`)).status;

/**
 * A non-loopback address that can actually REACH this server, or null.
 *
 * ⚠ Not `lanAddresses()[0]`. That was the first version and it flaked: this machine's first address
 * belongs to a Hyper-V/WSL virtual adapter, and a connection to one is not reliably routed back to a
 * listener on `0.0.0.0`. The failure looked like the gate misbehaving when it was the network refusing
 * — the worst kind of red, because the honest reading is "the security test is unreliable" and the
 * next step after that is deleting it.
 */
async function reachableLan(port: number): Promise<string | null> {
  for (const address of lanAddresses()) {
    try {
      await fetch(`http://${address}:${port}/api/inspect/actions?device=probe`);
      return address;
    } catch {
      // That interface cannot reach us; try the next.
    }
  }
  return null;
}

describe('the trust boundary', () => {
  it('accepts only the three loopback spellings', () => {
    expect(isLoopback('127.0.0.1')).toBe(true);
    expect(isLoopback('::1')).toBe(true);
    expect(isLoopback('::ffff:127.0.0.1')).toBe(true);
    // The ones that matter: a LAN peer is NOT an operator.
    // ⚠ Addresses here are RFC 5737 documentation ranges, not a plausible-looking home subnet — the
    // repo's sensitive scanner cannot tell an invented private address from a real one, and is right
    // not to try. It blocked the first version of this file for exactly that.
    expect(isLoopback('203.0.113.50')).toBe(false);
    expect(isLoopback('::ffff:203.0.113.50')).toBe(false);
    expect(isLoopback('10.0.0.7')).toBe(false);
    expect(isLoopback(undefined)).toBe(false);
    expect(isLoopback('')).toBe(false);
  });

  it('serves the device half to a LAN peer and hides the operator half from it', async () => {
    const { port } = await serve();
    const lan = await reachableLan(port);
    if (!lan) {
      // Honest skip: with no reachable non-loopback address there is no way to BE an off-box peer, and a
      // silent pass here would be the gate reporting green on a machine that never tested it.
      console.warn('inspect: no reachable LAN address — the off-box half of this test did not run');
      return;
    }

    // From loopback: the operator half exists.
    expect(await get('127.0.0.1', port, '/api/inspect/devices')).toBe(200);
    expect(await get('127.0.0.1', port, '/api/inspect/results')).toBe(200);

    // 🔴 From the LAN — the same routes must be INVISIBLE. Connecting to this machine's own LAN address
    // gives the server a non-loopback peer, which is exactly what a phone would be.
    expect(await get(lan, port, '/api/inspect/devices')).toBe(404);
    expect(await get(lan, port, '/api/inspect/results')).toBe(404);

    // ...while the device's own half still answers, because a device cannot authenticate.
    expect(await get(lan, port, '/api/inspect/actions?device=phone')).toBe(200);
    expect(await get(lan, port, '/')).toBe(200);
  });

  it('refuses to run a host command for a LAN peer', async () => {
    const { port } = await serve();
    const lan = await reachableLan(port);
    if (!lan) return;
    const res = await fetch(`http://${lan}:${port}/api/inspect/host`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: 'echo pwned' }),
    });
    // 404, not 403: a route that runs commands should not confirm it exists to someone who cannot use it.
    expect(res.status).toBe(404);
  });

  it('never consults a header for the peer address', () => {
    // A regression guard with teeth: if the gate is ever rewritten to read X-Forwarded-For, this string
    // appears in the module and the test fails. The address must stay a SOCKET fact.
    const source = String(createInspectService);
    expect(source.toLowerCase()).not.toContain('x-forwarded-for');
  });
});

describe('the host seam', () => {
  /**
   * 🔴 D63: a seam is done when something ASKS for it, not when it compiles. An absent target and a
   * working one are indistinguishable from outside, so this supplies a FAKE and asserts it was used.
   */
  it('runs the operator\'s command on the target and returns what it said', async () => {
    const calls: string[] = [];
    const fake = {
      label: 'me@mac.local',
      isRemote: true,
      sh(command: string) { calls.push(command); return { status: 0, out: 'Xcode 26.5\n' }; },
      probe: () => '', exists: () => false, list: () => [], mtimeMs: () => null, newestMtimeMs: () => null,
      join: (...p: string[]) => p.join('/'), basename: (p: string) => p, dirname: (p: string) => p,
      push: () => true, pull: () => true, gui() { return { status: 0, out: '' }; }, close() {},
    };
    const { server } = createInspectService({ page: () => inspectPage(), host: () => fake });
    servers.push(server);
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (typeof address === 'string' || address === null) throw new Error('no port');

    const res = await fetch(`http://127.0.0.1:${address.port}/api/inspect/host`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: 'xcodebuild -version' }),
    });
    const body = await res.json() as { ok: boolean; out: string; host: string };

    expect(calls).toEqual(['xcodebuild -version']);   // it was USED, not merely present
    expect(body.ok).toBe(true);
    expect(body.out).toContain('Xcode 26.5');
    expect(body.host).toBe('me@mac.local');
  });

  it('says so plainly when no Mac is configured, rather than pretending to run', async () => {
    const { server } = createInspectService({ page: () => inspectPage() });
    servers.push(server);
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (typeof address === 'string' || address === null) throw new Error('no port');

    const res = await fetch(`http://127.0.0.1:${address.port}/api/inspect/host`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: 'echo hi' }),
    });
    expect(res.status).toBe(409);
  });
});

describe('the cursor', () => {
  it('never consumes, so two devices do not steal each other\'s work', () => {
    const state = new InspectState();
    state.queue('eval', '1 + 1');
    expect(state.actionsSince(0, 'a').actions).toHaveLength(1);
    // The same action is still there for the second device.
    expect(state.actionsSince(0, 'b').actions).toHaveLength(1);
  });

  it('returns the head even when nothing is new, so a first poll can skip the backlog', () => {
    const state = new InspectState();
    state.queue('eval', 'old');
    state.queue('eval', 'older');
    const { actions, latest } = state.actionsSince(2);
    expect(actions).toHaveLength(0);
    // A page starting here asks for >2 next time and never replays the two it missed.
    expect(latest).toBe(2);
  });

  it('gives a targeted action only to its device', () => {
    const state = new InspectState();
    state.queue('eval', 'mine', 'phone-a');
    expect(state.actionsSince(0, 'phone-a').actions).toHaveLength(1);
    expect(state.actionsSince(0, 'phone-b').actions).toHaveLength(0);
    // An untargeted one goes to everybody.
    state.queue('eval', 'everyone');
    expect(state.actionsSince(0, 'phone-b').actions).toHaveLength(1);
  });

  it('keeps a device\'s report across later polls that carry none', () => {
    const state = new InspectState();
    state.touch('phone', '203.0.113.9', '{"shell":"WKWebView"}');
    state.touch('phone', '203.0.113.9');       // a plain heartbeat
    expect(state.devices.get('phone')?.report).toBe('{"shell":"WKWebView"}');
    expect(state.devices.get('phone')?.polls).toBe(2);
  });
});

describe('the device roster', () => {
  it('records the address from the socket, not from the body', async () => {
    const { port, state } = await serve();
    await fetch(`http://127.0.0.1:${port}/api/inspect/hello`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ device: 'phone', address: '10.9.9.9' }),
    });
    // 🔴 The address a device claims is ignored. It is the one fact a page cannot learn about itself,
    // which is exactly why it must come from the connection.
    expect(state.devices.get('phone')?.address).not.toBe('10.9.9.9');
    expect(plainAddress('::ffff:127.0.0.1')).toBe('127.0.0.1');
  });
});

describe('the page', () => {
  it('starts its cursor at the head rather than zero', () => {
    // Pinned as TEXT because the behaviour lives in browser JS the suite cannot execute. If the line
    // changes shape this fails and asks for a fresh look, which is the most this file can honestly do.
    expect(inspectPage()).toContain('if (since === null) { since = d.latest;');
  });

  it('re-announces on a reconnect edge', () => {
    expect(inspectPage()).toContain('if (!on) { on = true; hello()');
    expect(inspectPage()).toContain(".catch(function () { on = false;");
  });

  it('escapes a title rather than letting it close the tag', () => {
    expect(inspectPage({ title: '</title><script>x' })).not.toContain('</title><script>x');
  });

  it('serves JavaScript that actually parses', () => {
    // 🔴 The page's script is a STRING, so `tsc` never looks at it and the text assertions above pass
    // just as happily over a syntax error. It would then break completely on the one device whose
    // devtools you cannot open — which is the device this whole feature exists for.
    const html = inspectPage({ appOrigin: 'http://example.test:8080' });
    const body = html.slice(html.indexOf('<script>') + 8, html.indexOf('</script>'));
    expect(body.length).toBeGreaterThan(1000);
    // Compiles without running: a syntax error throws, and nothing here touches a DOM.
    expect(() => new Function(body)).not.toThrow();
  });

  it('puts the app origin in as JSON, so an apostrophe cannot break the script', () => {
    const html = inspectPage({ appOrigin: "http://x/'+alert(1)+'" });
    const body = html.slice(html.indexOf('<script>') + 8, html.indexOf('</script>'));
    expect(() => new Function(body)).not.toThrow();
  });
});

describe('eval reports failure in the EXIT CODE, not only in text', () => {
  /**
   * 🔴 Measured against a real WebKit: a throwing expression printed `(threw) Can't find variable: nope`
   * and exited 0, so `inspect eval … && next-step` marched on after a failed probe. A diagnostic that
   * reports a failure it does not signal is the same false success this CLI polices in builds, arriving
   * through the one command whose entire job is to tell you the truth about a device.
   */
  it('sets a non-zero exit code when the device says it threw', async () => {
    const saved = process.exitCode;
    const state = new InspectState();
    const { server } = createInspectService({ state, page: () => inspectPage() });
    servers.push(server);
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (typeof address === 'string' || address === null) throw new Error('no port');
    const args = ['--port', String(address.port)];

    try {
      process.exitCode = saved;
      // ⚠ The answer must arrive AFTER the command reads its cursor, not before. Recording it first
      // made this time out — which is the cursor behaving correctly: `cmdInspectEval` reads the results
      // head before queueing precisely so a fast device cannot answer into a window nobody is watching,
      // and a result already in the past is, correctly, not a reply to a question not yet asked.
      const running = cmdInspectEval(args, 'nope.missing');
      await new Promise((r) => setTimeout(r, 150));
      state.record('phone', 'eval', false, "Can't find variable: nope");
      const ok = await running;

      expect(ok).toBe(false);
      expect(process.exitCode).toBe(1);
    } finally {
      // ⚠ Restored: the suite shares one process, so a left-behind code fails an unrelated later test.
      process.exitCode = saved;
    }
  });
});

describe('positionals', () => {
  it('does not read a flag value as a word', () => {
    expect(positionals(['--port', '7699', 'xcodebuild', '-version'])).toEqual(['xcodebuild', '-version']);
    expect(positionals(['location.href', '--device', 'phone'])).toEqual(['location.href']);
  });
});

describe('shell-mangled expressions', () => {
  /**
   * 🔴 Git Bash rewrites anything path-shaped on its way to argv, so a regex literal arrives mangled:
   * `k=>/chrome/i.test(k)` becomes `k=>C:/Program Files/Git/chrome/i.test(k)`. The device then reports
   * "missing ) after argument list" — an error about an expression the user did not write. Nothing can
   * PREVENT it (the damage precedes this process), so it is named instead.
   */
  it('names the shell when a Git path appears inside an expression', () => {
    const say: string[] = [];
    const error = console.error;
    console.error = (...a: unknown[]) => { say.push(a.join(' ')); };
    try {
      warnIfShellRewrote('Object.keys(window).filter(k=>C:/Program Files/Git/chrome/i.test(k))');
    } finally {
      console.error = error;
    }
    expect(say.join('\n')).toContain('your SHELL rewrote');
    expect(say.join('\n')).toContain('MSYS_NO_PATHCONV=1');
  });

  it('stays SILENT for an ordinary expression', () => {
    // The quiet direction: warning on every eval would train people to ignore it, and most expressions
    // mention no path at all.
    const say: string[] = [];
    const error = console.error;
    console.error = (...a: unknown[]) => { say.push(a.join(' ')); };
    try {
      warnIfShellRewrote('location.href');
      warnIfShellRewrote('[1,2,3].filter(n=>n>1).length');
    } finally {
      console.error = error;
    }
    expect(say).toEqual([]);
  });
});
