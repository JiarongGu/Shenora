// Minting a provisioning profile, which is the one signing step nothing else in the loop can do.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { q, fail } from '../exec.js';
import type { Target } from './target.js';

/** Where the stub lands on the Mac. Disposable — it is rebuilt from the package on every run. */
const PROVISION_DIR = 'shenora-provision';

/**
 * The stub's files, relative to `assets/ios-provision/`.
 *
 * 🔴 **A real Xcode project, because only Xcode can mint a profile.** The .NET iOS SDK CONSUMES a
 * provisioning profile and never creates one, so `deploy --device` on an unprovisioned bundle id fails
 * with *"Could not find any available provisioning profiles"* — an error about the app, caused by a step
 * the toolchain does not offer. `xcodebuild -allowProvisioningUpdates` needs a project to run against;
 * this is the smallest one that compiles.
 */
const STUB_FILES = ['ShenoraProvision/main.swift', 'ShenoraProvision.xcodeproj/project.pbxproj'];

/** The package's own `assets/` — beside `dist/`, since this file runs from there once built. */
function assetsDir(): string {
  return path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', 'assets', 'ios-provision');
}

/** Put the stub on the Mac, fresh. */
function uploadStub(target: Target, home: string): boolean {
  const remote = `${home}/${PROVISION_DIR}`;
  const local = assetsDir();
  if (!fs.existsSync(local)) {
    return fail(`the provisioning stub is missing from this install (${local}).`,
      '  Reinstall @shenora/cli — `assets/` ships with it.');
  }
  // Rebuilt each time: a stale stub is a silent difference between what this tool thinks it asked for and
  // what Xcode read.
  if (target.sh(`rm -rf ${q(remote)} && mkdir -p ${q(`${remote}/ShenoraProvision`)}`
    + ` ${q(`${remote}/ShenoraProvision.xcodeproj`)}`, { quiet: true }).status !== 0) {
    return fail(`could not prepare ${remote} on ${target.label}.`);
  }
  for (const rel of STUB_FILES) {
    if (!target.push(path.join(local, rel), `${remote}/${rel}`)) {
      return fail(`could not copy ${rel} to ${target.label}.`);
    }
  }
  return true;
}

/** Every bundle id installed profiles cover, read from the profiles themselves. */
export function installedProfileIds(target: Target, home: string): string[] {
  // ⚠ `security cms -D` decodes the profile; the id inside is `TEAM.bundle.id`, so the team prefix is
  // stripped to compare against what the app actually declares.
  const raw = target.probe(
    `for f in ${q(`${home}/Library/Developer/Xcode/UserData/Provisioning Profiles`)}/*.mobileprovision; do`
    + ` security cms -D -i "$f" 2>/dev/null`
    + ` | plutil -extract Entitlements.application-identifier raw - 2>/dev/null; done`);
  return raw
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    // Drop the leading team id: `ABCDE12345.com.example.app` -> `com.example.app`.
    .map((l) => l.replace(/^[A-Z0-9]+\./, ''));
}

export interface ProvisionResult {
  minted: string[];
  missing: string[];
}

/**
 * The team to provision against — configured, or READ OFF the Mac's own signing certificate.
 *
 * 🔴 **Derived rather than required, because a team id identifies a developer ACCOUNT** and
 * `shenora.deploy.json` is normally tracked — requiring it there means either committing it or being
 * unable to provision at all.
 *
 * The id is the certificate's Organisational Unit, which is where Apple puts it.
 */
export function teamId(target: Target, configured: string | undefined): string | null {
  const set = configured?.trim();
  if (set) return set;

  const name = target.probe(
    `security find-identity -v -p codesigning | head -1 | sed -n 's/.*"\\(.*\\)".*/\\1/p'`);
  const derived = name
    ? target.probe(`security find-certificate -c ${q(name)} -p`
      + ` | openssl x509 -noout -subject | tr ',' '\\n' | sed -n 's/.*OU=//p' | head -1`)
    : '';
  if (derived) return derived.trim();

  fail('there is no codesigning identity on that Mac, so there is no team to provision against.',
    '  Xcode → Settings → Accounts, sign in with the Apple ID that owns the device.\n'
    + `  Or set "team" in ${'shenora.deploy.json'} if you know the id — though it identifies your\n`
    + '  account, so think twice if that file is committed.');
  return null;
}

/**
 * Ask Xcode to create a profile for each bundle id.
 *
 * 🔴 **Through `target.gui`, never plain ssh.** Xcode's stored Apple ID session has the same audit-session
 * problem as the login keychain, so an ssh `xcodebuild -allowProvisioningUpdates` cannot reach the account
 * that would authorise the request.
 *
 * ⚠ **Every EXTENSION needs its own profile.** An app extension is provisioned separately from its
 * container, and forgetting it fails at the very end of a device install with an error naming the APP — so
 * the caller passes the extension ids too.
 */
export function provisionBundleIds(target: Target, team: string, bundleIds: string[]): ProvisionResult | null {
  const home = target.probe('echo $HOME');
  if (!home) {
    fail(`could not read the home directory on ${target.label}.`);
    return null;
  }
  if (!uploadStub(target, home)) return null;

  const minted: string[] = [];
  for (const id of bundleIds) {
    console.log(`\nshenora: provisioning ${id}…`);
    const script = `cd ${q(`${home}/${PROVISION_DIR}`)}\n`
      + `xcodebuild -project ShenoraProvision.xcodeproj -target ShenoraProvision \\\n`
      + `  -sdk iphoneos -configuration Debug -allowProvisioningUpdates \\\n`
      + `  SYMROOT=${q(`${home}/${PROVISION_DIR}/build`)} OBJROOT=${q(`${home}/${PROVISION_DIR}/build/obj`)} \\\n`
      + `  PRODUCT_BUNDLE_IDENTIFIER=${q(id)} DEVELOPMENT_TEAM=${q(team)} CODE_SIGN_STYLE=Automatic`
      + ` 2>&1 | tail -40`;
    const r = target.isRemote
      ? target.gui(script, { tag: 'provision', timeoutMs: 10 * 60_000 })
      : target.sh(script);
    if (r.status !== 0 && r.out.trim()) console.log(r.out.trimEnd());
    if (r.status === 0) minted.push(id);
  }

  // 🔴 Report what is ON DISK, not what xcodebuild said. A build can succeed against a profile it already
  // had, so a zero exit does not mean a profile now exists for the id that was asked for — and
  // "provisioned successfully" followed by a device build failing for want of a profile is a false success.
  const have = installedProfileIds(target, home);
  const missing = bundleIds.filter((id) => !have.includes(id));
  return { minted, missing };
}
