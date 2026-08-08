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

const repo = process.cwd();
const TAG = process.argv[2] ?? 'v0.10.0';

function gitShow(rev, file) {
  try { return execFileSync('git', ['show', `${rev}:${file}`], { encoding: 'utf8', cwd: repo }); }
  catch { return ''; }
}

function listBaselines(rev) {
  const out = execFileSync('git', ['ls-tree', '-r', '--name-only', rev, '--',
    'tests/Shenora.Tests/Api/Baselines/'], { encoding: 'utf8', cwd: repo });
  return out.split(/\r?\n/).filter((l) => l.endsWith('.txt'));
}

// A type line starts at column 0; members are indented. Grab the fully-qualified name, then keep the
// last dotted segment — generics and base lists trimmed.
const TYPE = /^(?:sealed |abstract |static |readonly |partial )*(?:class|interface|enum|struct|delegate|record) ([A-Za-z0-9_.`+<>]+)/;

function typeNames(rev) {
  const names = new Set();
  for (const file of listBaselines(rev)) {
    for (const line of gitShow(rev, file).split(/\r?\n/)) {
      const m = TYPE.exec(line);
      if (!m) continue;
      const full = m[1].replace(/`\d+/g, '');
      names.add(full.split('.').pop());
    }
  }
  return names;
}

const before = typeNames(TAG);
const now = typeNames('HEAD');

const retired = new Set(fs.readFileSync(path.join(repo, 'devtools', 'retired-names.txt'), 'utf8')
  .split(/\r?\n/).map((l) => l.replace(/#.*$/, '').trim()).filter(Boolean));

const gone = [...before].filter((n) => !now.has(n)).sort();
const unregistered = gone.filter((n) => !retired.has(n));

console.log(`shipped types at ${TAG}: ${before.size}   at HEAD: ${now.size}`);
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
