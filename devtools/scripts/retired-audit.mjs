// retired-audit — which PUBLIC TYPES left the SHIPPED surface without being registered in
// retired-names.txt? Those are breaks an adopter meets with no warning and no gate.
//
// 🔴 RUN IT BEFORE CUTTING A RELEASE. `stale-scan` answers "is this retired name still described as
// current?"; this answers the question BEFORE it — "is this removal recorded at all?" Neither gate can:
// they read `retired-names.txt`, so a name that never reached that file is invisible to both. Measured
// 2026-08-08: 19 public types had left the surface since v0.10.0 and SIX were unregistered
// (`DropZoneFacade`, `FileDialogFacade`, `WindowCommandFacade`, `OperationsFacade`, `IOperation`,
// `OperationServiceCollectionExtensions`) — one rename family and one deletion, each site compiling
// perfectly the whole time.
//
// The API baselines are tracked in git, so the shipped surface is recoverable exactly. Compares the
// baselines at a TAG against today's, by SHORT type name — namespaces moved wholesale in D53/D55/D65,
// so a full-name diff would report every type in the kit.
//
// ⚠ NOT part of `verify`, deliberately: it needs tags, and CI clones are not always deep. It is a
// RELEASE step (docs/RELEASING.md) and exits non-zero on findings so it can gate one later.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { baselineFilesAt, baselineText } from './api-baselines.mjs';
import { fileURLToPath } from 'node:url';

// The script's own location, like every other tool here — see the note in stale-scan.mjs.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const TAG = process.argv[2] ?? 'v0.10.0';
// The second rev is an argument so ANY two points can be compared — `retired-audit v0.9.0 v0.10.0` audits
// a shipped release after the fact. It also makes the `required` detector testable against real history
// rather than a fake: swap the revs and a property that LOST the modifier is seen as GAINING it.
const REV = process.argv[3] ?? 'HEAD';

// 🔴 BOTH baseline directories, via the shared reader — this used to list `Api/Baselines/` only, and
// `Shenora.Android`/`Shenora.iOS` have no file there at all: their whole surface lives in
// `MetadataBaselines/`. So the gate for UNRECORDED REMOVALS could not see a public type leaving either
// mobile package — the exact failure this tool's header cites as its reason for existing, in the two
// packages it never covered. Found 2026-08-18 while fixing the same blind spot in `namespace-moves`.
const gitShow = (rev, file) => baselineText(rev, file);
const listBaselines = (rev) => baselineFilesAt(rev);

// A type line starts at column 0; members are indented. Grab the fully-qualified name, then keep the
// last dotted segment — generics and base lists trimmed.
const TYPE = /^(?:sealed |abstract |static |readonly |partial )*(?:class|interface|enum|struct|delegate|record) ([A-Za-z0-9_.`+<>]+)/;

/**
 * Type short-names, plus every `required` property as `Type.Prop`.
 *
 * 🔴 Why `required` is read here at all: **adding it to a property that already shipped is a HARD BREAK**
 * — every adopter's object initializer stops compiling — and it is invisible to all three name gates,
 * because the type and the member keep their names. The baselines carry the modifier, so the question is
 * answerable exactly, from data already tracked.
 */
function surface(rev) {
  const types = new Set();
  const required = new Set();
  for (const file of listBaselines(rev)) {
    let type = null;
    for (const line of gitShow(rev, file).split(/\r?\n/)) {
      const m = TYPE.exec(line);
      if (m) {
        type = m[1].replace(/`\d+/g, '').split('.').pop();
        types.add(type);
        continue;
      }
      if (!type || !/^\s+\S/.test(line) || !/\brequired\b/.test(line)) continue;
      const prop = /([A-Za-z0-9_]+)\s*\{/.exec(line.trim());
      if (prop) required.add(`${type}.${prop[1]}`);
    }
  }
  return { types, required };
}

const beforeSurface = surface(TAG);
const nowSurface = surface(REV);
const before = beforeSurface.types;
const now = nowSurface.types;

const retired = new Set(fs.readFileSync(path.join(repo, 'devtools', 'retired-names.txt'), 'utf8')
  .split(/\r?\n/).map((l) => l.replace(/#.*$/, '').trim()).filter(Boolean));

const gone = [...before].filter((n) => !now.has(n)).sort();
const unregistered = gone.filter((n) => !retired.has(n));

console.log(`shipped types at ${TAG}: ${before.size}   at ${REV}: ${now.size}`);
console.log(`\nLEFT THE SURFACE: ${gone.length}`);
console.log(`  registered in retired-names.txt: ${gone.length - unregistered.length}`);
console.log(`\n🔴 GONE AND UNREGISTERED (${unregistered.length}) — an adopter meets these with no warning:`);
for (const n of unregistered) console.log(`  ${n}`);

if (unregistered.length > 0) {
  console.error(`\nAdd each to devtools/retired-names.txt with what replaced it, then run`);
  console.error(`\`node devtools/dev.mjs stale-scan\` — registering a name is what makes the prose`);
  console.error(`citing it findable. A removal with no entry is a break nothing warns about.`);
  process.exitCode = 1;   // never process.exit(): an abrupt exit REPLACES the code
} else {
  console.log('\nok — every removal since the tag is recorded.');
}

// ── `required` DELTAS — the contract change that keeps every name ─────────────────────────────────
//
// ⚠ Only a type present in BOTH surfaces can have its contract broken. Without that filter the first
// measurement reported TEN gains and every one was an artefact: a property carried through a type
// RENAME (WinFormsHostOptions.MainForm → WindowsHostOptions.MainForm, D37) or a type that did not exist
// at the tag, where there was no prior contract to break. Measured 2026-08-12 — filtered, the real
// count since v0.10.0 is ZERO gains and ONE loss.
const bothTypes = (key) => {
  const type = key.split('.')[0];
  return before.has(type) && now.has(type);
};
const gained = [...nowSurface.required].filter((r) => !beforeSurface.required.has(r) && bothTypes(r)).sort();
const lost = [...beforeSurface.required].filter((r) => !nowSurface.required.has(r) && bothTypes(r)).sort();

// The home for a break is the CHANGELOG (phase-workflow: "note breaks under `### Breaking`"), so that is
// what "recorded" means here — not a retired-names entry, which is for a name that WENT.
const unreleased = gitShow('HEAD', 'CHANGELOG.md').split(/^## /m).find((s) => /^Unreleased/i.test(s)) ?? '';
const recorded = (key) => {
  const [type, prop] = key.split('.');
  return unreleased.includes(key) || (unreleased.includes(type) && unreleased.includes(prop));
};

console.log(`\n\`required\` properties at ${TAG}: ${beforeSurface.required.size}   at ${REV}: ${nowSurface.required.size}`);
if (lost.length > 0) {
  // NOT a failure: dropping `required` compiles for every adopter. It invalidates PROSE, which is why it
  // is printed — the guide that explained the old contract is what goes stale (MediaConversionOptions
  // .Convert, D70: two docs still called it `required` two days later, with every gate green).
  console.log(`\n⚠ NO LONGER \`required\` (${lost.length}) — compiles fine; re-read the prose that explained it:`);
  for (const r of lost) console.log(`  - ${r}${recorded(r) ? '' : '   (not in the CHANGELOG either)'}`);
}

const undocumented = gained.filter((r) => !recorded(r));
if (gained.length > 0) {
  console.log(`\n🔴 NEWLY \`required\` (${gained.length}) — a HARD BREAK: an adopter's object initializer stops compiling:`);
  for (const r of gained) console.log(`  + ${r}${recorded(r) ? '   in CHANGELOG Unreleased' : '   ❌ NOT in the CHANGELOG'}`);
}
if (undocumented.length > 0) {
  console.error(`\nEach must be named in CHANGELOG.md under \`### Breaking\` in \`## Unreleased\`. Making an`);
  console.error(`existing property required is a source break that no name gate can see, because nothing`);
  console.error(`was renamed — the type and the member are exactly as they were.`);
  process.exitCode = 1;
} else if (gained.length === 0) {
  console.log('ok — no property became `required` since the tag.');
}
