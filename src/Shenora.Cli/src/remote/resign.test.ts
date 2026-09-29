// `ios resign`: what the script does in which order, and what the command does with its answer. Whether the
// Mac's codesign accepts the result is the last mile no test here can answer (`docs/design/cli-remote.md`).
import { describe, it, expect, afterEach, vi } from 'vitest';
import { FakeTarget } from './fake-target.js';
import { resignScript, readResignOutput, resignOn } from './resign.js';

const savedExit = process.exitCode;
afterEach(() => {
  vi.restoreAllMocks();
  process.exitCode = savedExit;
});

const hush = () => {
  vi.spyOn(console, 'log').mockImplementation(() => {});
  vi.spyOn(console, 'error').mockImplementation(() => {});
};

/** The script's body after its function definitions — the part that decides the ORDER. */
const mainOf = (script: string) => script.slice(script.lastIndexOf('\n}\n'));

describe('the re-sign script signs inside-out', () => {
  const script = resignScript('/builds/App.ipa', '/tmp/shenora-resign-x');

  it('signs every extension before the app, and verifies before it packs', () => {
    // 🔴 A container's signature seals what is inside it: an extension signed after the app breaks the app's
    // seal, and codesign accepts that order — the app then fails at LAUNCH.
    const main = mainOf(script);
    const extension = main.indexOf('sign_bundle "$x"');
    const app = main.indexOf('sign_bundle "$APP"');
    const verify = main.indexOf('codesign --verify --deep --strict "$APP"');
    const pack = main.indexOf('zip -qry');
    // Each one FOUND first: a missing marker is -1, which is "before" everything.
    for (const at of [extension, app, verify, pack]) expect(at).toBeGreaterThan(-1);
    expect(extension).toBeLessThan(app);
    expect(app).toBeLessThan(verify);
    expect(verify).toBeLessThan(pack);
  });

  it("signs a bundle's own frameworks before the bundle", () => {
    const body = script.slice(script.indexOf('sign_bundle() {'));
    const nested = body.indexOf('sign_nested "$b"');
    const bundle = body.indexOf('--entitlements "$WORK/ent.plist"');
    expect(nested).toBeGreaterThan(-1);
    expect(bundle).toBeGreaterThan(-1);
    expect(nested).toBeLessThan(bundle);
  });

  it('embeds the profile and takes the entitlements from it', () => {
    expect(script).toContain('cp "$prof" "$b/embedded.mobileprovision"');
    expect(script).toContain('plutil -extract Entitlements xml1 -o "$WORK/ent.plist"');
  });

  it('skips an expired profile and prefers an exact bundle id over a wildcard', () => {
    expect(script).toContain('[[ "$exp" > "$NOW" ]] || continue');
    expect(script).toContain('${exact:-$wild}');
  });

  it('quotes the paths it is given', () => {
    const odd = resignScript("/Users/a b/it's.app", '/tmp/w');
    expect(odd).toContain(`SRC='/Users/a b/it'\\''s.app'`);
  });
});

describe('reading what the script said', () => {
  it('finds the .ipa and what was signed', () => {
    const outcome = readResignOutput([
      'RESIGN: signed com.example.app.widget (its profile is valid until 2026-10-06T00:00:00Z)',
      'RESIGN: signed com.example.app (its profile is valid until 2026-10-06T00:00:00Z)',
      'RESIGN: ipa /tmp/shenora-resign-x.ipa',
    ].join('\n'));
    expect(outcome.ipa).toBe('/tmp/shenora-resign-x.ipa');
    expect(outcome.said).toHaveLength(2);
    expect(outcome.failure).toBeNull();
  });

  it('carries the reason a run stopped', () => {
    const outcome = readResignOutput('RESIGN-FAIL: no unexpired provisioning profile covers com.example.app');
    expect(outcome.ipa).toBeNull();
    expect(outcome.failure).toContain('no unexpired provisioning profile');
  });
});

describe('resignOn', () => {
  it('signs through the GUI session on a remote Mac and brings the .ipa back', () => {
    // 🔴 codesign cannot reach a login-keychain key from an ssh session.
    const target = new FakeTarget({
      isRemote: true,
      responses: [{ match: 'set -uo pipefail', out: 'RESIGN: ipa /tmp/shenora-resign-x.ipa\n' }],
    });
    hush();
    expect(resignOn(target, '/builds/App.ipa', 'App-resigned.ipa')).toBe(true);

    expect(target.via('gui')).toHaveLength(1);
    expect(target.via('pull')).toEqual(['/tmp/shenora-resign-x.ipa -> App-resigned.ipa']);
  });

  it('brings nothing back from a failed run, and says why', () => {
    const target = new FakeTarget({
      isRemote: true,
      responses: [{ match: 'set -uo pipefail', status: 1, out: 'RESIGN-FAIL: no signing identity in this keychain matches the profile for com.example.app\n' }],
    });
    const errors: string[] = [];
    vi.spyOn(console, 'log').mockImplementation(() => {});
    vi.spyOn(console, 'error').mockImplementation((m: unknown) => { errors.push(String(m)); });

    expect(resignOn(target, '/builds/App.ipa', 'App-resigned.ipa')).toBe(false);
    expect(target.via('pull')).toHaveLength(0);
    expect(errors.join('\n')).toContain('no signing identity');
  });
});
