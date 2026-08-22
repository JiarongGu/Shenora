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
import { fileURLToPath } from 'node:url';
import { outOfScope } from './git-scope.mjs';

// The script's own location, like every other tool here. This read `process.cwd()` and so had to be run
// from the repo root — it crashed loudly elsewhere rather than under-reporting, which is the safe
// direction, but this is the tool the rename rule says to run IN THE SAME COMMIT, i.e. exactly when
// someone is in a hurry and in some other directory.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const RETIRED_FILE = path.join(repo, 'devtools', 'retired-names.txt');

/** `Name  # why it went` per line; blank lines and `#` comments ignored — same parse as doc-drift. */
function retiredNames() {
  return fs.readFileSync(RETIRED_FILE, 'utf8').split(/\r?\n/)
    .map((l) => l.replace(/#.*$/, '').trim())
    .filter(Boolean);
}

// History BY DEFINITION — an old name in these is accurate, not stale. Mirrors doc-drift's own exemption.
//
// 🔴 `namespace-moves.md` IS THE OLD→NEW TABLE ITSELF, so every row names a retired thing ON PURPOSE and
// not one of them can ever be a finding. It contributed 103 of 183 hits on 2026-08-23 — 56 % of the output
// of a report whose whole value is that a human reads all of it. ⚠ A triage list padded with rows that
// cannot be wrong is how the two REAL hits in that same run (a csproj naming a namespace that never
// existed, and ARCHITECTURE.md claiming `Shenora.Ipc`'s namespace "stayed") went unread for weeks.
const SKIP_FILE = /(CHANGELOG\.md|retired-names\.txt|namespace-moves\.md)$/;
const EXT = /\.(cs|md|ts|tsx|csproj|props|targets)$/;

// 🔴 WHAT IS OUT OF SCOPE IS GIT'S ANSWER, NOT A NAME LIST — `git-scope.mjs` carries the query, the
// reasoning and the fail-safe. A hit in gitignored working state is a claim no reader can reach, and it
// drowns the worklist this tool exists to keep triageable: `local/` (private, and an informal ARCHIVE —
// its value is recording what was true THEN), `.superpowers/` planning material, `devtools/_*` probes.
//
// ⚠ THE OLD RULE HERE WAS THE EXACT HOLE doc-drift RECORDS, and it sat in this file for its whole life:
// `entry.name.startsWith('_')` skipped a `_`-prefixed entry ANYWHERE, while `.gitignore` says
// `devtools/_*`. A tracked `docs/_x.md` or `src/_y.cs` would have been invisible to the rename worklist —
// latent only because this repo happens to track no such file today (`git ls-files` finds none). Git's
// answer is untracked-and-ignored, so it can never cover a tracked file; the hole is now unrepresentable
// rather than merely unhit.
function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (outOfScope(path.relative(repo, full))) continue;
    // A directory that is itself a checkout (agent worktree, nested clone) is another copy of the repo
    // at another commit — scanning it doubled this tool's worklist. See doc-drift's `isNestedCheckout`.
    // Not gitignored, so the query above cannot answer it: this stays a separate rule.
    if (entry.isDirectory() && fs.existsSync(path.join(full, '.git'))) continue;
    if (entry.isDirectory()) walk(full, out);
    else if (EXT.test(entry.name) && !SKIP_FILE.test(entry.name)) out.push(full);
  }
  return out;
}

const args = process.argv.slice(2);
const only = args.find((a) => !a.startsWith('-'));       // optional path filter, e.g. `docs`
const names = retiredNames();
// 🔴 ESCAPED, because `retired-names.txt` calls its entries "literal phrases" TWICE and they were
// interpolated raw — so the file's own description of itself was false in both gates. Consequences
// measured 2026-08-13: `Type.Member` entries had a `.` that matched ANY character, and the one entry
// carrying parentheses became a capture GROUP, i.e. it matched the phrase without them. Both over-fire
// rather than under-fire, so nothing was being missed — but an entry containing `[` would have thrown at
// construction and taken the whole gate down, and the person adding it would have been reading the word
// "literal". The fix is to make the file's claim TRUE rather than to soften the claim.
const literal = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const matchers = names.map((name) => [name, new RegExp(`\\b${literal(name)}\\b`)]);

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
