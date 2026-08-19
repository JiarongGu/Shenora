// The DEVICE FLOW, end to end, without a device.
//
// 🔴 Every case here is a defect that actually shipped and was found by hand against a real Mac. Found
// once, by someone who happened to have hardware plugged in — which is not a gate. These pin the same
// behaviour against a scriptable target, so the next change to `ios.ts` gets told immediately and the
// only thing left needing a Mac is whether the Mac obeys the commands it is sent.
//
// ⚠ What this deliberately does NOT claim: that a build succeeds, that codesign works, that a phone
// accepts an install. Those are the last mile and no fake can answer them. What it proves is that the
// right commands go to the right place in the right order — which is where every defect in this file
// actually lived.
import { describe, it, expect, afterEach, vi } from 'vitest';
import { FakeTarget } from './fake-target.js';
import { build, findApp, checkExtensions, buildProject, buildDir } from '../ios.js';
import { pushTree, filesToPush } from './push.js';
import { provisionBundleIds, installedProfileIds } from './provision.js';
import type { DeployConfig } from '../config.js';

let labelSeq = 0;
const freshLabel = () => `mac-${++labelSeq}.local`;

const cfg = {
  root: 'D:\\work\\MyRepo',
  project: 'samples/App/App.csproj',
  configuration: 'Debug',
  bundleId: 'com.example.app',
  tfm: 'net10.0-ios',
  androidTfm: 'net10.0-android',
} as unknown as DeployConfig;

/** Every command below is quiet; the assertions read the recorded calls, not the console. */
const hush = () => {
  const out = vi.spyOn(console, 'log').mockImplementation(() => {});
  const err = vi.spyOn(console, 'error').mockImplementation(() => {});
  return { out, err, restore: () => { out.mockRestore(); err.mockRestore(); } };
};

const savedExit = process.exitCode;
afterEach(() => {
  vi.restoreAllMocks();
  // ⚠ The commands set `process.exitCode` on failure, and the suite shares one process.
  process.exitCode = savedExit;
});

describe('a signing build goes through the GUI session', () => {
  it('uses gui when the target is REMOTE and the build signs', () => {
    // 🔴 codesign cannot reach a login-keychain key from an ssh session, so a signing build MUST be
    // handed to the Mac's own GUI session. Nothing about a failure here looks like a threading problem:
    // it fails with errSecInternalComponent, which reads as a certificate fault.
    const target = new FakeTarget({ isRemote: true, label: freshLabel(), probes: [{ match: 'echo $HOME', out: '/Users/you' }] });
    const t = hush();
    build(target, cfg, 'ios-arm64', ' -p:CodesignKey=x', '');
    t.restore();

    expect(target.via('gui')).toHaveLength(1);
    expect(target.via('gui')[0]).toContain('dotnet build');
    expect(target.via('sh')).toHaveLength(0);
  });

  it('uses plain sh for a SIMULATOR build, which signs ad-hoc', () => {
    // The quiet direction: routing everything through gui would make every simulator build pay for a
    // detached Terminal session and a polled marker file.
    const target = new FakeTarget({ isRemote: true, label: freshLabel(), probes: [{ match: 'echo $HOME', out: '/Users/you' }] });
    const t = hush();
    build(target, cfg, 'iossimulator-arm64', '', '');
    t.restore();

    expect(target.via('sh')).toHaveLength(1);
    expect(target.via('gui')).toHaveLength(0);
  });

  it('uses plain sh on a LOCAL Mac even when signing — it is already in a GUI session', () => {
    const target = new FakeTarget({ isRemote: false, label: freshLabel() });
    const t = hush();
    build(target, cfg, 'ios-arm64', ' -p:CodesignKey=x', '');
    t.restore();

    expect(target.via('gui')).toHaveLength(0);
    expect(target.via('sh')).toHaveLength(1);
  });

  it('PRINTS the gui log when the build fails, because gui cannot stream', () => {
    // 🔴 `gui` runs detached in another session, so its output exists only as a return value. Without
    // this the whole failure was the single line "the build failed — see the output above", with
    // nothing above it.
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      probes: [{ match: 'echo $HOME', out: '/Users/you' }],
      responses: [{ match: 'dotnet build', status: 1, out: 'error CS9999: something specific\n' }],
    });
    const t = hush();
    build(target, cfg, 'ios-arm64', ' -p:CodesignKey=x', '');
    const printed = t.out.mock.calls.flat().join('\n');
    t.restore();

    expect(printed).toContain('error CS9999: something specific');
  });

  it('names the provisioning fix when that is what failed', () => {
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      probes: [{ match: 'echo $HOME', out: '/Users/you' }],
      responses: [{
        match: 'dotnet build',
        status: 1,
        out: 'error : Could not find any available provisioning profiles for App on iOS.',
      }],
    });
    const t = hush();
    build(target, cfg, 'ios-arm64', ' -p:CodesignKey=x', '');
    const said = t.err.mock.calls.flat().join('\n');
    t.restore();

    expect(said).toContain('com.example.app');       // names YOUR id, not "no profiles exist"
    expect(said).toContain('Apple ID');
  });
});

describe('what gets handed to dotnet', () => {
  it('🔴 names the PROJECT, never the checkout root', () => {
    // Handed the root, `dotnet build` builds whatever solution is there — on this kit's own tree that
    // meant the Windows sample and the test project, failing about a project nobody asked for.
    const target = new FakeTarget({ isRemote: true, label: freshLabel(), probes: [{ match: 'echo $HOME', out: '/Users/you' }] });
    const t = hush();
    build(target, cfg, 'ios-arm64', '', '');
    t.restore();

    const command = target.via('sh')[0]!;
    expect(command).toContain('/Users/you/MyRepo/samples/App/App.csproj');
    expect(buildProject(cfg, target)).toBe('/Users/you/MyRepo/samples/App/App.csproj');
    expect(buildDir(cfg, target)).toBe('/Users/you/MyRepo/samples/App');
  });
});

describe('freshness — refusing to install a leftover', () => {
  const dir = '/Users/you/MyRepo/samples/App/bin/Debug/net10.0-ios/ios-arm64';
  const base = {
    isRemote: true,
    label: freshLabel(),
    exists: [dir],
    probes: [{ match: 'echo $HOME', out: '/Users/you' }],
  };

  it('accepts an app newer than the build that claims it', () => {
    const target = new FakeTarget({
      ...base,
      listings: { [dir]: ['App.app'] },
      mtimes: { [`${dir}/App.app`]: 2_000_000 },
    });
    expect(findApp(target, cfg, 'ios-arm64', 1_000_000)).toBe(`${dir}/App.app`);
  });

  it('🔴 asks the WHOLE bundle, not one file inside it', () => {
    // Measured on a real Mac: immediately after a successful incremental build the `.app` was 34
    // seconds old and its Info.plist 3.9 DAYS old. Reading the plist refused a build that had just
    // succeeded on screen. `newestMtimeMs` is the only honest clock here.
    const target = new FakeTarget({
      ...base,
      listings: { [dir]: ['App.app'] },
      mtimes: { [`${dir}/App.app`]: 2_000_000 },   // the newest thing anywhere inside
    });
    expect(findApp(target, cfg, 'ios-arm64', 1_999_000)).not.toBeNull();
  });

  it('refuses one that predates the build', () => {
    const target = new FakeTarget({
      ...base,
      listings: { [dir]: ['App.app'] },
      mtimes: { [`${dir}/App.app`]: 1_000 },
    });
    expect(findApp(target, cfg, 'ios-arm64', 5_000_000)).toBeNull();
  });

  it('treats an unreadable time as NOT fresh', () => {
    // "Unknown" must never read as "just built" — that is how a connection failure becomes a stale
    // install that reports success.
    const target = new FakeTarget({ ...base, listings: { [dir]: ['App.app'] } });
    expect(findApp(target, cfg, 'ios-arm64', 5_000_000)).toBeNull();
  });
});

describe('app extensions are verified BEFORE install', () => {
  const app = '/Users/you/MyRepo/samples/App/bin/Debug/net10.0-ios/ios-arm64/App.app';

  it('flags an extension with no embedded profile', () => {
    // An extension provisioned separately installs happily and never launches — and a simulator cannot
    // catch it, because it does not enforce signing.
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      exists: [`${app}/PlugIns`],
      listings: { [`${app}/PlugIns`]: ['Widget.appex'] },
    });
    const { checked, problems } = checkExtensions(target, app);

    expect(checked).toBe(1);
    expect(problems.join(' ')).toContain('Widget.appex');
  });

  it('is quiet when there are no extensions at all', () => {
    const target = new FakeTarget({ isRemote: true, label: freshLabel(), probes: [{ match: 'echo $HOME', out: '/Users/you' }] });
    expect(checkExtensions(target, app)).toEqual({ checked: 0, problems: [] });
  });
});

describe('push', () => {
  it('🔴 deletes what it previously sent and would no longer send', () => {
    // The first version only added, and the Mac's older checkout kept files this kit had since renamed —
    // so IFileLockInspector existed twice and the KIT failed to compile on a tree that is clean here.
    // ⚠ The manifest must OVERLAP what is being sent, or the guard below refuses to delete at all —
    // which is the correct answer for an unrelated tree and made the first version of this test wrong.
    const mine = filesToPush(process.cwd())!;
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      probes: [{ match: 'shenora-push-manifest', out: `${mine[0]}\nsrc/Old.cs\n` }],
    });
    const t = hush();
    const result = pushTree(target, process.cwd(), '/Users/you/MyRepo');
    t.restore();

    expect(result).not.toBeNull();
    // It removed the stale list through a FILE and xargs — never an argument list, which would meet
    // ssh's 8 KB ceiling and be truncated while reporting success.
    expect(target.via('sh').join('\n')).toContain('xargs');
    expect(target.via('sh').join('\n')).toContain('rm -f');
    // ...and it recorded what it sent, so the NEXT push knows what to take away.
    expect(target.via('push').join('\n')).toContain('shenora-push-manifest');
  });

  it('🔴 REFUSES to delete into a tree that is not this project', () => {
    // The git-index fallback is right for a first push into this project's checkout and catastrophic
    // against an unrelated one: every file that repo tracks is "not in the current list". Overlap is the
    // test — two checkouts of one project share paths, two unrelated ones share none.
    const target = new FakeTarget({
      isRemote: true,
      label: freshLabel(),
      // No manifest; the git index names a completely different project.
      probes: [{ match: 'ls-files', out: 'their/App.java\ntheir/Main.kt\n' }],
    });
    const t = hush();
    pushTree(target, process.cwd(), '/Users/you/SomeoneElse');
    const warned = t.err.mock.calls.flat().join('\n');
    t.restore();

    expect(warned).toContain('does not look like this project');
    expect(target.via('sh').join('\n')).not.toContain('xargs');   // nothing was deleted
  });

  it('still deletes when the trees DO overlap', () => {
    // The other direction, or the guard would simply disable deletion everywhere.
    const mine = filesToPush(process.cwd())!;
    const target = new FakeTarget({
      isRemote: true,
      label: freshLabel(),
      probes: [{ match: 'shenora-push-manifest', out: `${mine[0]}\nsrc/Gone.ts\n` }],
    });
    const t = hush();
    pushTree(target, process.cwd(), '/Users/you/MyRepo');
    t.restore();

    expect(target.via('sh').join('\n')).toContain('xargs');
  });

  it('unpacks into a directory it creates first', () => {
    const target = new FakeTarget({ isRemote: true, label: freshLabel(), probes: [{ match: 'echo $HOME', out: '/Users/you' }] });
    const t = hush();
    pushTree(target, process.cwd(), '/Users/you/Fresh');
    t.restore();

    const unpack = target.via('sh').find((c) => c.includes('tar -xzf'))!;
    expect(unpack).toContain('mkdir -p');
    expect(unpack.indexOf('mkdir -p')).toBeLessThan(unpack.indexOf('tar -xzf'));
  });
});

describe('provisioning', () => {
  it('asks Xcode once per bundle id, through the GUI session', () => {
    // Xcode's stored Apple ID session has the same audit-session problem as the keychain, so this
    // cannot go over plain ssh.
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      probes: [
        { match: 'echo $HOME', out: '/Users/you' },
        { match: 'application-identifier', out: 'TEAM1.com.example.app\nTEAM1.com.example.app.widget' },
      ],
    });
    const t = hush();
    const result = provisionBundleIds(target, 'TEAM1', ['com.example.app', 'com.example.app.widget']);
    t.restore();

    const gui = target.via('gui');
    expect(gui).toHaveLength(2);
    expect(gui[0]).toContain('-allowProvisioningUpdates');
    expect(gui[0]).toContain('com.example.app');
    expect(gui[1]).toContain('com.example.app.widget');
    expect(result?.missing).toEqual([]);
  });

  it('🔴 reports what is ON DISK, not what xcodebuild said', () => {
    // A build can succeed against a profile it already had, so exit 0 does not mean a profile now
    // exists for the id asked for. "Provisioned successfully" followed by a device build failing for
    // want of a profile is exactly the false success this CLI exists to prevent.
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      probes: [
        { match: 'echo $HOME', out: '/Users/you' },
        { match: 'application-identifier', out: 'TEAM1.com.example.other' },   // NOT the one asked for
      ],
    });
    const t = hush();
    const result = provisionBundleIds(target, 'TEAM1', ['com.example.app']);
    t.restore();

    expect(result?.minted).toEqual(['com.example.app']);     // xcodebuild said fine…
    expect(result?.missing).toEqual(['com.example.app']);    // …and the disk disagrees
  });

  it('strips the team prefix when reading installed profiles', () => {
    const target = new FakeTarget({
      isRemote: true, label: freshLabel(),
      probes: [{ match: 'application-identifier', out: 'ABCDE12345.com.example.app' }],
    });
    expect(installedProfileIds(target, '/Users/you')).toEqual(['com.example.app']);
  });
});
