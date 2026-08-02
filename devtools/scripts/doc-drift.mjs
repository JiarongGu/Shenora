#!/usr/bin/env node
// doc-drift — the gate the prose never had.
//
// WHY THIS EXISTS. Every code invariant in this repo has a test; no prose claim had anything. A
// whole-codebase review (2026-08-01) found 8 of its ~13 findings in comments and docs — the surface
// consumers read and nothing compiles. Two of those were not opinions, they were checkable facts
// stated wrongly, and this script checks exactly those two. It deliberately does NOT attempt a
// general "does this symbol exist" sweep: docs are full of BCL names, TS symbols, file names and
// deliberately-historical references, so a fuzzy matcher would drown a real signal in false
// positives and get switched off. Two precise checks that cannot cry wolf beat one that can.
//
//   1. DEPENDENCY GRAPH. The worst finding of that review: README.md and docs/ADOPTION.md both drew
//      `Shenora.Windows -> Shenora.Ipc`, an edge that has never existed, while four code comments
//      state the opposite invariant. An adopter following it writes a BaseFacade and gets an
//      unresolved-namespace error the docs said could not happen. The csproj files are the truth;
//      this compares the documented arrows against them.
//
//   2. RETIRED NAMES. When a public name is removed, prose about it does not disappear — it silently
//      becomes a lie (`RegisterWaiting`, `ResumePayload`, `LoginWindow`, a `TASKS.md H3` that no
//      longer exists). But this repo's docs are amendment STACKS: naming a retired symbol while
//      explaining why it went is correct and valuable. So the rule is not "never mention it", it is
//      "mention it only in the PAST TENSE" — a retired name must appear near a historical marker
//      (used to / former / renamed / removed / no longer / until / was). That distinguishes
//      "RequestResume keys on ResumePayload" (a lie) from "keyed on ResumePayload for one release"
//      (history), which is exactly the line the review had to walk by hand.
//
// Usage: node devtools/scripts/doc-drift.mjs [--list]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const listOnly = process.argv.includes('--list');
const problems = [];

// ── 1. The documented dependency graph vs the csproj files ────────────────────────────────────────

/** Every `A -> B` project reference, read from the packable csproj files themselves. */
function actualGraph() {
  const srcDir = path.join(repo, 'src');
  const edges = new Map(); // project -> Set(referenced project)
  for (const entry of fs.readdirSync(srcDir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const csproj = path.join(srcDir, entry.name, `${entry.name}.csproj`);
    if (!fs.existsSync(csproj)) continue;
    const xml = fs.readFileSync(csproj, 'utf8');
    const refs = [...xml.matchAll(/<ProjectReference\s+Include="[^"]*?([A-Za-z0-9_.]+)\.csproj"/g)]
      .map((m) => m[1]);
    edges.set(entry.name, new Set(refs));
  }
  return edges;
}

/**
 * Documented arrows, from the fenced graph blocks in README/ADOPTION. Both files draw the same
 * diamond as an ASCII figure; rather than parse art, this looks for the CLAIM that most often goes
 * wrong — a chain written as `A -> B -> C` in prose or a figure line — and for the specific
 * false edge that shipped. Kept narrow on purpose: a parser that tries to understand every drawing
 * is a parser that breaks on the next drawing.
 */
function documentedEdges(text) {
  const found = [];
  // `Shenora.X` followed by an arrow and another `Shenora.Y`, anywhere (prose or figure), including
  // chains: A -> B -> C yields A->B and B->C.
  const chain = /Shenora\.[A-Za-z0-9_.]+(?:\s*(?:->|→|─+>)\s*(?:`?)Shenora\.[A-Za-z0-9_.]+(?:`?))+/g;
  for (const match of text.match(chain) ?? []) {
    const names = [...match.matchAll(/Shenora\.[A-Za-z0-9_.]+/g)].map((m) => m[0]);
    for (let i = 0; i < names.length - 1; i++) found.push([names[i], names[i + 1]]);
  }
  return found;
}

function checkDependencyGraph() {
  const actual = actualGraph();
  const known = new Set(actual.keys());
  for (const rel of ['README.md', path.join('docs', 'ADOPTION.md')]) {
    const file = path.join(repo, rel);
    if (!fs.existsSync(file)) continue;
    const text = fs.readFileSync(file, 'utf8');
    for (const [from, to] of documentedEdges(text)) {
      if (!known.has(from) || !known.has(to)) continue;   // not a packable project pair
      if (actual.get(from)?.has(to)) continue;            // the edge is real
      problems.push(
        `${rel}: documents "${from} -> ${to}", but ${from}.csproj does not reference ${to}.\n` +
        `      Real references for ${from}: ${[...(actual.get(from) ?? [])].join(', ') || '(none)'}.\n` +
        '      An adopter following a wrong graph gets an unresolved-namespace error the docs deny.');
    }
  }
}

// ── 2. Retired names must be spoken about in the past tense ───────────────────────────────────────

const RETIRED_FILE = path.join(repo, 'devtools', 'retired-names.txt');

// History BY DEFINITION — these files exist to record what the kit USED to be, so a retired name or
// a since-deleted doc path in them is ACCURATE, not stale. Exempting them is what keeps both checks
// signal rather than noise.
//
// `docs/archive/` is exempt WHOLESALE, by path prefix rather than by filename (2026-08-02). That
// folder's entry criterion — "this is finished and will not change" — is the same property this list
// encodes, so a new archive file is exempt the moment it is created instead of tripping the gate
// until someone remembers to extend a regex.
const HISTORY_BY_DEFINITION =
  /^(devtools\/retired-names\.txt|CHANGELOG\.md|docs\/ROADMAP\.md)$|^docs\/archive\//;

/** `Name  # why it went` per line; blank lines and `#` comments ignored. */
function retiredNames() {
  if (!fs.existsSync(RETIRED_FILE)) return [];
  return fs.readFileSync(RETIRED_FILE, 'utf8').split(/\r?\n/)
    .map((l) => l.replace(/#.*$/, '').trim())
    .filter(Boolean);
}

// Words that mark a sentence as history rather than a current claim. Checked over a WINDOW around
// the match, not just its own line: this repo's prose wraps at ~100 columns and an amendment's
// "superseded / used to / before publish" routinely sits several lines above the name it is about.
const HISTORY = /\b(used to|use to|formerly|former|previously|rename[sd]?|removed|no longer|until|was |were |had |used only|superseded|retired|deleted|cut|dropped|replaced|before publish|history|historical|obsolete|legacy|once|sketch|originally|no such|used the)\b/i;
const CONTEXT_LINES = 6;

// An explicit escape hatch for a whole passage — a preserved design SKETCH, an amendment stack, a
// rename table. `doc-drift:history` suppresses from that line to the next markdown heading (or 40
// lines in a code file). Deliberately explicit: a reviewer adding it is stating "this passage is
// history", which is exactly the judgement the checker cannot make.
const HISTORY_MARKER = /doc-drift:history/;

/** Line indexes covered by an explicit marker. */
function suppressedLines(lines, isMarkdown) {
  const covered = new Set();
  for (let i = 0; i < lines.length; i++) {
    if (!HISTORY_MARKER.test(lines[i])) continue;
    const limit = isMarkdown ? lines.length : Math.min(lines.length, i + 40);
    for (let j = i; j < limit; j++) {
      if (isMarkdown && j > i && /^#{1,6}\s/.test(lines[j])) break;
      covered.add(j);
    }
  }
  return covered;
}

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (/^(node_modules|bin|obj|dist|\.git|publish|local)$/.test(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (/\.(md|cs|ts|tsx)$/.test(entry.name) && !entry.name.endsWith('.actual')) out.push(full);
  }
  return out;
}

function checkRetiredNames() {
  const retired = retiredNames();
  if (retired.length === 0) return;
  // Word-boundary match, so `Resumable` does not fire on `ResumePayload` and vice versa.
  const patterns = retired.map((name) => [name, new RegExp(`(?<![A-Za-z0-9_])${name}(?![A-Za-z0-9_])`)]);

  for (const file of walk(repo)) {
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    if (HISTORY_BY_DEFINITION.test(rel)) continue;
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    const suppressed = suppressedLines(lines, rel.endsWith('.md'));
    for (let i = 0; i < lines.length; i++) {
      if (suppressed.has(i)) continue;
      for (const [name, pattern] of patterns) {
        if (!pattern.test(lines[i])) continue;
        const context = lines.slice(Math.max(0, i - CONTEXT_LINES), i + CONTEXT_LINES).join(' ');
        if (HISTORY.test(context)) continue;   // spoken about in the past tense — fine
        problems.push(
          `${rel}:${i + 1}: names the RETIRED symbol "${name}" as a current fact.\n` +
          `      ${lines[i].trim().slice(0, 110)}\n` +
          '      Say what replaced it, or mark the sentence as history (used to / former / removed / until).');
      }
    }
  }
}

// ── 3. No pointer to a doc that does not exist ────────────────────────────────────────────────────
//
// Added when the 0.2.0 cleanup RETIRED three implemented design docs: the moment a doc can be
// deleted, every `docs/x.md` in prose or a code comment becomes a candidate dangling pointer, and
// nothing checked them. Cheap and exact — the path either resolves or it does not, so unlike the
// other two checks this one needs no heuristic at all.

function checkDocLinks() {
  // `docs/foo.md`, with or without backticks/parens, anywhere in prose or a comment.
  const reference = /(?<![\w./-])((?:docs|\.claude)\/[A-Za-z0-9._\-/]+\.md)/g;
  for (const file of walk(repo)) {
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    // Same exemption as the retired-name check, for the same reason: these files RECORD what the
    // repo used to contain, so naming a since-deleted doc is accurate rather than broken. Git
    // history holds the file itself.
    if (HISTORY_BY_DEFINITION.test(rel)) continue;
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      // A retired doc named in a HISTORY note is fine — that is the point of recording the retirement.
      if (HISTORY.test(lines[i])) continue;
      for (const [, target] of lines[i].matchAll(reference)) {
        if (fs.existsSync(path.join(repo, target))) continue;
        problems.push(
          `${rel}:${i + 1}: points at "${target}", which does not exist.\n` +
          `      ${lines[i].trim().slice(0, 110)}\n` +
          '      Retire a doc by REDIRECTING its pointers (usually to a DECISIONS D<n>), not just deleting it.');
      }
    }
  }
}

// ── run ───────────────────────────────────────────────────────────────────────────────────────────

if (listOnly) {
  console.log('retired names watched:', retiredNames().join(', ') || '(none)');
  const actual = actualGraph();
  for (const [from, refs] of actual) console.log(`  ${from} -> ${[...refs].join(', ') || '(none)'}`);
  process.exit(0);
}

checkDependencyGraph();
checkRetiredNames();
checkDocLinks();

if (problems.length === 0) {
  console.log('  ok  doc-drift: dependency graph matches the csproj files; no retired name stated as ' +
              'current; every docs/ pointer resolves');
  process.exit(0);
}
console.error(`\n\x1b[31m✖ doc-drift: ${problems.length} stale claim(s) in prose:\x1b[0m`);
for (const p of problems) console.error(`  ${p}`);
console.error('\nProse is the one surface with no compiler. See devtools/retired-names.txt to retire a name.\n');
process.exit(1);
