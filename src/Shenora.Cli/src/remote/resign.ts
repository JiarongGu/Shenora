// Re-signing a built app with a fresh profile — the step between an expired free-team profile and a working
// install, without rebuilding.
import { q, fail } from '../exec.js';
import type { Target } from './target.js';

/** The script's own report lines. A line starting with neither is a tool's output, shown as it is. */
const SAY = 'RESIGN:';
const DIE = 'RESIGN-FAIL:';

/**
 * The script that re-signs `source` (an `.ipa` or `.app` on the build machine) into `<work>.ipa`.
 *
 * 🔴 **INSIDE-OUT, and the order is the trap:** a bundle's signature seals what is inside it, so anything
 * signed after its container breaks the container's seal. `codesign` accepts the wrong order and the app then
 * fails at LAUNCH, not at signing. So: each bundle's frameworks and dylibs, then the bundle; every extension
 * before the app that holds it; and `codesign --verify --deep --strict` on the result before it is packed.
 *
 * Per bundle, the newest UNEXPIRED profile whose application identifier names that bundle id — an exact one
 * over a wildcard — and the keychain identity whose certificate is in that profile, matched by its SHA-1. The
 * entitlements come from the profile, which is what the device checks them against.
 *
 * ⚠ Nested bundles deeper than `PlugIns/*.appex` (a watch app) are not handled.
 */
export function resignScript(source: string, work: string): string {
  return `set -uo pipefail
SRC=${q(source)}
WORK=${q(work)}
OUT="$WORK.ipa"
say() { echo "${SAY} $*"; }
die() { echo "${DIE} $*"; exit 1; }

rm -rf "$WORK" "$OUT"
mkdir -p "$WORK/Payload" || die "could not create $WORK"
case "$SRC" in
  *.ipa) ditto -x -k "$SRC" "$WORK" || die "could not unpack $SRC" ;;
  *.app) ditto "$SRC" "$WORK/Payload/$(basename "$SRC")" || die "could not copy $SRC" ;;
  *) die "not an .ipa or an .app: $SRC" ;;
esac
APP=$(ls -d "$WORK"/Payload/*.app 2>/dev/null | head -1)
[ -n "$APP" ] || die "there is no .app inside $SRC"

NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)
IDENTITIES=$(security find-identity -v -p codesigning)
PROFILE_DIRS=("$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles" "$HOME/Library/MobileDevice/Provisioning Profiles")

# The newest unexpired profile for bundle id $1, an exact application identifier over a wildcard one.
profile_for() {
  local id=$1 exact="" exact_exp="" wild="" wild_exp="" dir f appid exp
  for dir in "\${PROFILE_DIRS[@]}"; do
    for f in "$dir"/*.mobileprovision; do
      [ -f "$f" ] || continue
      security cms -D -i "$f" > "$WORK/p.plist" 2>/dev/null || continue
      appid=$(plutil -extract Entitlements.application-identifier raw "$WORK/p.plist" 2>/dev/null) || continue
      exp=$(plutil -extract ExpirationDate raw "$WORK/p.plist" 2>/dev/null) || continue
      [[ "$exp" > "$NOW" ]] || continue
      case "\${appid#*.}" in
        "$id") if [[ -z "$exact_exp" || "$exp" > "$exact_exp" ]]; then exact=$f; exact_exp=$exp; fi ;;
        '*') if [[ -z "$wild_exp" || "$exp" > "$wild_exp" ]]; then wild=$f; wild_exp=$exp; fi ;;
      esac
    done
  done
  printf '%s' "\${exact:-$wild}"
}

# The keychain identity whose certificate is in the decoded profile at $WORK/p.plist.
identity_for() {
  local i=0 cert sha
  while cert=$(plutil -extract "DeveloperCertificates.$i" raw "$WORK/p.plist" 2>/dev/null); do
    sha=$(printf '%s' "$cert" | base64 -D | shasum -a 1 | cut -d' ' -f1 | tr '[:lower:]' '[:upper:]')
    if printf '%s' "$IDENTITIES" | grep -q "$sha"; then printf '%s' "$sha"; return; fi
    i=$((i + 1))
  done
}

# Every framework and dylib directly under $1/Frameworks — before the bundle that holds them.
sign_nested() {
  local n out
  [ -d "$1/Frameworks" ] || return 0
  for n in "$1"/Frameworks/*.framework "$1"/Frameworks/*.dylib; do
    [ -e "$n" ] || continue
    out=$(codesign -f -s "$SHA" "$n" 2>&1) || die "codesign refused $(basename "$n"): $out"
  done
}

sign_bundle() {
  local b=$1 id prof exp out
  id=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$b/Info.plist" 2>/dev/null) \\
    || die "$(basename "$b") has no bundle id"
  prof=$(profile_for "$id")
  [ -n "$prof" ] || die "no unexpired provisioning profile covers $id — mint one with \\\`shenora ios provision\\\`"
  security cms -D -i "$prof" > "$WORK/p.plist" 2>/dev/null || die "could not read the profile for $id"
  SHA=$(identity_for)
  [ -n "$SHA" ] || die "no signing identity in this keychain matches the profile for $id"
  exp=$(plutil -extract ExpirationDate raw "$WORK/p.plist")
  plutil -extract Entitlements xml1 -o "$WORK/ent.plist" "$WORK/p.plist" || die "the profile for $id has no entitlements"
  cp "$prof" "$b/embedded.mobileprovision" || die "could not embed the profile in $(basename "$b")"
  sign_nested "$b"
  out=$(codesign -f -s "$SHA" --entitlements "$WORK/ent.plist" --generate-entitlement-der "$b" 2>&1) \\
    || die "codesign refused $(basename "$b"): $out"
  say "signed $id (its profile is valid until $exp)"
}

if [ -d "$APP/PlugIns" ]; then
  for x in "$APP"/PlugIns/*.appex; do
    if [ -d "$x" ]; then sign_bundle "$x"; fi
  done
fi
sign_bundle "$APP"
out=$(codesign --verify --deep --strict "$APP" 2>&1) || die "the re-signed app fails verification: $out"
rm -f "$WORK/p.plist" "$WORK/ent.plist"
(cd "$WORK" && zip -qry "$OUT" Payload) || die "could not pack $OUT"
say "ipa $OUT"
`;
}

export interface ResignOutcome {
  /** The re-signed `.ipa`, on the build machine; null when the script did not produce one. */
  ipa: string | null;
  /** The script's own report lines, prefix removed — what was signed, or why it stopped. */
  said: string[];
  /** Why it stopped, from the script; null when it did not say. */
  failure: string | null;
}

/** Read the script's output. Pure, so what a failed run reports can be tested without a Mac. */
export function readResignOutput(out: string): ResignOutcome {
  const said: string[] = [];
  let ipa: string | null = null;
  let failure: string | null = null;
  for (const line of out.split('\n').map((l) => l.trim())) {
    if (line.startsWith(DIE)) failure = line.slice(DIE.length).trim();
    else if (line.startsWith(`${SAY} ipa `)) ipa = line.slice(`${SAY} ipa `.length).trim();
    else if (line.startsWith(SAY)) said.push(line.slice(SAY.length).trim());
  }
  return { ipa, said, failure };
}

/**
 * Re-sign `source` on the target and bring the `.ipa` back to `localOut`.
 *
 * 🔴 **Through `target.gui` on a remote Mac**, for the reason `provision` and a device build use it: codesign
 * cannot reach a login-keychain key from an ssh session.
 */
export function resignOn(target: Target, source: string, localOut: string): boolean {
  const work = `/tmp/shenora-resign-${Date.now().toString(36)}`;
  const script = resignScript(source, work);
  const r = target.isRemote
    ? target.gui(script, { tag: 'resign', timeoutMs: 10 * 60_000 })
    : target.sh(script, { quiet: true });
  const outcome = readResignOutput(r.out);
  for (const line of outcome.said) console.log(`shenora: ${line}`);

  if (r.status !== 0 || !outcome.ipa) {
    // The tool's own lines are the only evidence when the script did not say why.
    if (!outcome.failure && r.out.trim()) console.log(r.out.trimEnd());
    return fail(`the re-sign failed: ${outcome.failure ?? 'the script stopped without saying why'}.`);
  }

  const pulled = target.pull(outcome.ipa, localOut);
  target.sh(`rm -rf ${q(work)} ${q(outcome.ipa)}`, { quiet: true });
  if (!pulled) return fail(`the .ipa was re-signed on ${target.label} but could not be copied back.`);
  console.log(`shenora: ${localOut}`);
  return true;
}
