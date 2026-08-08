// stale-scan — every RETIRED name stated anywhere, with NO history-word suppression.
//
// 🔴 WHY THIS IS A SEPARATE TOOL AND NOT A STRICTER doc-drift.
// `doc-drift` is a GATE: it must be quiet on correct prose, so it suppresses any retired name within
// 6 lines of a history word (`was`, `replaced`, `used to`, …). That suppression is right — this repo's
// docs are amendment stacks by design and would otherwise fail constantly. But it means the gate is
// BLINDEST exactly where the risk is highest, because a stale claim lives among amendment prose.
// Measured 2026-08-08: a planted claim stayed GREEN in TASKS.md and in ARCHITECTURE.md alike.
//
// So this is a REVIEW TOOL, not a gate. It NEVER fails the build: most hits are correct past tense
// ("this was called CoBrowseSession until…") and only a human can tell those from a live lie. It
// produces the worklist; the triage is yours. That triage is the step no gate performs.
//
// ⚠ RUN IT IN THE SAME COMMIT AS A RENAME. D66 added its names to retired-names.txt and did not run a
// scan, and `docs/ADOPTION.md` went on telling adopters to call `services.AddShenoraOperations()` —
// a deleted API — for three commits, with every gate green.
import fs from 'node:fs';
import path from 'node:path';

const repo = process.cwd();
const RETIRED_FILE = path.join(repo, 'devtools', 'retired-names.txt');

/** `Name  # why it went` per line; blank lines and `#` comments ignored — same parse as doc-drift. */
function retiredNames() {
  return fs.readFileSync(RETIRED_FILE, 'utf8').split(/\r?\n/)
    .map((l) => l.replace(/#.*$/, '').trim())
    .filter(Boolean);
}

// History BY DEFINITION — an old name in these is accurate, not stale. Mirrors doc-drift's own
// exemption, plus `local/` (private, and an informal ARCHIVE: its value is recording what was true THEN).
const SKIP_DIR = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'local']);
const SKIP_FILE = /(CHANGELOG\.md|retired-names\.txt)$/;
const EXT = /\.(cs|md|ts|tsx|csproj|props|targets)$/;

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    // `devtools/_*` is the gitignored scratch convention.
    if (SKIP_DIR.has(entry.name) || entry.name.startsWith('_')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (EXT.test(entry.name) && !SKIP_FILE.test(entry.name)) out.push(full);
  }
  return out;
}

const args = process.argv.slice(2);
const only = args.find((a) => !a.startsWith('-'));       // optional path filter, e.g. `docs`
const names = retiredNames();
const matchers = names.map((name) => [name, new RegExp(`\\b${name}\\b`)]);

const byFile = new Map();
let total = 0;
for (const file of walk(repo)) {
  const rel = path.relative(repo, file).replace(/\\/g, '/');
  if (only && !rel.startsWith(only.replace(/\\/g, '/'))) continue;

  fs.readFileSync(file, 'utf8').split(/\r?\n/).forEach((line, i) => {
    const hit = matchers.filter(([, re]) => re.test(line)).map(([name]) => name);
    if (hit.length === 0) return;
    if (!byFile.has(rel)) byFile.set(rel, []);
    byFile.get(rel).push(`  ${String(i + 1).padStart(5)}: [${hit.join(', ')}]  ${line.trim().slice(0, 100)}`);
    total++;
  });
}

for (const [file, hits] of [...byFile].sort((a, b) => b[1].length - a[1].length)) {
  console.log(`\n${file}  (${hits.length})`);
  console.log(hits.join('\n'));
}

console.log(`\nstale-scan: ${total} occurrence(s) of ${names.length} retired name(s) in ${byFile.size} file(s).`);
console.log('TRIAGE BY HAND — most are correct PAST TENSE and must stay. You are looking for a retired');
console.log('name stated as a CURRENT fact, which is what doc-drift cannot see. This never fails a build.');
