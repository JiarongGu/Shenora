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
//   4. UNDOCUMENTED PACKAGES. Added 2026-08-05, earned the day before: `Shenora.IO.Compression` shipped
//      as a new nupkg with NO entry in docs/ARCHITECTURE.md and no gate said a word — every check here
//      looked at claims that WERE made, none at a shipped thing nobody described. So each packable
//      project must be named in README.md's package table and in ARCHITECTURE.md. Exact, not heuristic:
//      the name either appears or it does not, which is the bar the header above sets.
//
// Usage: node devtools/scripts/doc-drift.mjs [--list]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { citedOutOfScope, outOfScope, scopeViaGit } from './git-scope.mjs';

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
  // ⚠ Only CHANGELOG.md and the retired-names list remain. `docs/ROADMAP.md` and `docs/archive/` were
  // both DELETED on 2026-08-07 — and their presence here is what made them deletable: a file the drift
  // gate treats as history-by-definition is, by the repo's own definition, an archive. That is a useful
  // test to apply to the next doc that wants an exemption.
  // ⚠ `docs/reference/namespace-moves.md` joined on 2026-08-18 and passes that test differently from
  // the two deleted ones: its ENTIRE left column is old fully-qualified names — saying where each went
  // is the file's only job — and it is GENERATED (`dev.mjs namespace-moves`), so the "an exempt file
  // rots unnoticed" hazard does not apply. Nobody maintains it; a release regenerates it.
  /^(devtools\/retired-names\.txt|CHANGELOG\.md|docs\/reference\/namespace-moves\.md)$/;

/** `Name  # why it went` per line; blank lines and `#` comments ignored. */
// A retired entry is a LITERAL phrase, so every regex metacharacter in it is escaped before it becomes
// a matcher. `stale-scan.mjs` carries the same helper and the measurement that earned it.
const literal = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

function retiredNames() {
  if (!fs.existsSync(RETIRED_FILE)) return [];
  return fs.readFileSync(RETIRED_FILE, 'utf8').split(/\r?\n/)
    .map((l) => l.replace(/#.*$/, '').trim())
    .filter(Boolean);
}

// Words that mark a sentence as history rather than a current claim. Checked over a WINDOW around
// the match, not just its own line: this repo's prose wraps at ~100 columns and an amendment's
// "superseded / used to / before publish" routinely sits several lines above the name it is about.
// ⚠ `was`/`were`/`had` carry NO trailing space, and that is a fix rather than a style choice (2026-08-05).
// They used to be written `was ` — a trailing space followed by the `\b` at the end of the group, which
// requires a WORD character after the space. So `was `Shenora.WebView2`` did not match (the next
// character is a backtick), nor did `was ~9,300 lines`, nor `was "…"`. In a repo whose prose says
// "X was `Something`" constantly, the single most common past-tense shape was silently not counting as
// history — so the gate fired on correctly-written sentences and taught its readers to work around it.
// `\bwas\b` is both simpler and right; it still cannot match inside `wash` or `wasteful`.
// `merged` is new for the same reason `superseded` is here: it is how this repo says a thing stopped
// existing separately (D37 merged three package ids into one).
const HISTORY = /\b(used to|use to|formerly|former|previously|rename[sd]?|removed|no longer|until|was|were|had|used only|superseded|retired|deleted|cut|dropped|replaced|merged|before publish|history|historical|obsolete|legacy|once|sketch|originally|no such|used the)\b/i;
// 🔴 STAYS AT 6, and this is the second time it has been TESTED rather than argued (2026-08-14, after
// the DECISIONS.md amendment stacks were removed — the stated reason the window could finally be
// tightened). Measured at 3: FIVE findings, four of them correct past tense whose history word merely
// sits 4–6 lines from the name, one real. **A gate that adds four false positives to surface one defect
// is a gate people switch off**, and the four were not sloppy prose — this repo wraps at ~100 columns
// and a "superseded / used to / no longer" routinely lands several lines from the name it governs.
// ⚠ The hypothesis "the stacks are gone, so the window can shrink" was WRONG, and the probe that
// supported it was wrong too: it walked only `docs/`, while every one of the five findings lives in
// `.claude/` or `tests/`. **A sampling probe that does not scan the gate's own file set is not a
// measurement of the gate.** Trust the gate's output over an approximation of it.
// ⚠ Also note the window is ASYMMETRIC by construction — `slice(i - N, i + N)` looks BACK N and FORWARD
// N-1. Left as is deliberately: history markers lead their subject far more often than they trail it.
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

// ⚠ MSBuild files are in this set, and that was a HOLE until 2026-08-07. The walk read `.md|.cs|.ts|.tsx`,
// which meant a csproj could state a retired name as current forever with every gate green — and one did:
// `Shenora.Media.csproj` justified its Core reference with "`MediaRangeServer` speaks
// `WebViewResourceRequest`", a type D45 moved to Core and RENAMED, so the sentence described a kit that had
// not existed for days. This is the same class the 2026-08-05 review found in
// `Shenora.IO.Compression`'s `<Description>` (which opened with the retired "Shenora archives") and only
// half-closed: that pass taught `retired-names.txt` about package IDS, and left the FILE TYPE gap open, so
// the very next csproj drift was invisible again.
//
// A csproj is the highest-stakes prose in the repo after the README, because `<Description>` is SHIPPED —
// it is what nuget.org renders on the package page, where an adopter reads it and this repo never does.
// `.props`/`.targets` join for the same reason: `src/Directory.Build.props` carries the version and enough
// prose to go stale the same way.
/**
 * 🔴 A DIRECTORY THAT IS ITSELF A CHECKOUT IS NOT PART OF THIS ONE — skip it whole.
 *
 * A git WORKTREE carries a `.git` FILE pointing at the real gitdir; a nested clone or submodule carries
 * a `.git` directory. Either way the property is the same and it is the one that matters: those files
 * belong to another copy of the repo, usually at another commit.
 *
 * MEASURED 2026-08-10, the moment the first subagent worktree appeared under `.claude/worktrees/`:
 * doc-drift went from GREEN to 114 "stale claim" failures, cite-scan from 36 docs to 77, stale-scan from
 * 305 occurrences to 594, self-rename-scan from 14 lines to 30. Every one of those hits named a REAL
 * path — in the wrong copy — so the output looks like a repo that has suddenly rotted, and the fix a
 * reader reaches for is to edit files that are about to be deleted.
 *
 * ⚠ **The name-based deny-list could not have caught this**, which is the lesson worth keeping: it lists
 * directories somebody thought of (`node_modules`, `bin`, `obj`, …), and it was complete right up until a
 * new KIND of directory appeared. This asks what the directory IS instead, so the next one is covered
 * before anybody names it.
 */
function isNestedCheckout(dir) {
  return fs.existsSync(path.join(dir, '.git'));
}

// 🔴 WHAT THIS WALK SKIPS IS GIT'S ANSWER, NOT A LIST OF NAMES. The property the gate means is "no file
// a READER can reach states a retired name as current", so the question is "would a clone have this
// file?" — which git already answers, exactly and for free (`git-scope.mjs` carries the query and the
// fail-safe). It used to be a hand-maintained deny-list, and the list is the thing that kept failing:
// `devtools/_*` was added 2026-08-05 (15 of one run's 34 hits came from throwaway consumers no adopter
// will ever read), `.superpowers/` on 2026-08-12 (8 stale claims in planning notes that exist on one
// machine), each after the gate had already misfired — and the comment on `isNestedCheckout` below had
// written the verdict on that shape before either was added: a name list "lists directories somebody
// thought of", complete right up until a new kind of directory appears.
//
// ⚠ The direction of the replacement is the whole point. Git's answer is UNTRACKED-and-ignored, so it
// can never cover a TRACKED file — the `docs/_x.md` / `src/_y.cs` hole an earlier `_`-prefix rule opened
// is unrepresentable here. A skip rule wider than the ignore rule it mirrors is a gate hole.
// ⚠ `local/` falls out of the same query but is skipped for a DIFFERENT reason — it is private AND an
// informal ARCHIVE (`persist-working-state.md`), recording what was true THEN. Do not "helpfully" bring
// it back into scope because the mechanism no longer names it.
function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (outOfScope(path.relative(repo, full))) continue;
    if (entry.isDirectory() && isNestedCheckout(full)) continue;
    if (entry.isDirectory()) walk(full, out);
    else if (/\.(md|cs|ts|tsx|csproj|props|targets)$/.test(entry.name) && !entry.name.endsWith('.actual')) out.push(full);
  }
  return out;
}

function checkRetiredNames() {
  const retired = retiredNames();
  if (retired.length === 0) return;
  // Word-boundary match, so `Resumable` does not fire on `ResumePayload` and vice versa.
  // Entries are LITERAL, which is what `retired-names.txt` says of itself and was not true until
  // 2026-08-13 — see the note in `stale-scan.mjs`, which had the same defect in the same shape.
  const patterns = retired.map((name) => [name, new RegExp(`(?<![A-Za-z0-9_])${literal(name)}(?![A-Za-z0-9_])`)]);

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
          // An entry with a space is a retired CLAIM, not a symbol — say which, because the fix differs:
          // a name gets replaced, while a claim has to be rewritten or moved into the past tense.
          `${rel}:${i + 1}: names the RETIRED ${name.includes(' ') ? 'CLAIM' : 'symbol'} "${name}" as a current fact.\n` +
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
  // Any multi-segment path ending in `.md`, with or without backticks/parens, in prose or a comment.
  // ⚠ It used to require a `docs/` or `.claude/` PREFIX, and that hole was found on 2026-08-07 by
  // deleting `docs/archive/`: six rows of `docs/README.md`'s own inventory pointed at the deleted
  // files as `archive/tasks.md` — RELATIVE to the containing directory, the natural way to write a
  // link in a table of neighbours — and the checker could not see any of them. The router is exactly
  // where that spelling is most likely and a dangling pointer costs the most.
  // A segment is still REQUIRED (`foo/bar.md`, not `README.md`) so that generic mentions of a bare
  // filename do not become candidates; resolution then tries repo-root AND the file's own directory,
  // because both spellings appear in this repo and both are correct.
  const reference = /(?<![\w./-])([A-Za-z0-9._-]+(?:\/[A-Za-z0-9._-]+)*\/[A-Za-z0-9._-]+\.md)/g;
  for (const file of walk(repo)) {
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    // Same exemption as the retired-name check, for the same reason: these files RECORD what the
    // repo used to contain, so naming a since-deleted doc is accurate rather than broken. Git
    // history holds the file itself.
    if (HISTORY_BY_DEFINITION.test(rel)) continue;
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    const dir = path.dirname(file);
    for (let i = 0; i < lines.length; i++) {
      // A retired doc named in a HISTORY note is fine — that is the point of recording the retirement.
      if (HISTORY.test(lines[i])) continue;
      for (const [, target] of lines[i].matchAll(reference)) {
        // 🔴 `local/` IS ABSENT ON PURPOSE, AND EVERYWHERE BUT ONE MACHINE. It is gitignored private
        // context (`sensitive-info.md`), so it does not exist in CI, in a fresh clone, or in an agent
        // worktree — and 45 tracked files point into it deliberately. Treating those as dangling made
        // `doc-drift` report 51 failures in ANY checkout that was not the owner's, which is not a
        // warning about the docs, it is the gate telling every other environment the repo is broken.
        //
        // ⚠ MEASURED 2026-08-10, and the damage is not the noise — it is what the noise MAKES PEOPLE DO.
        // A subagent hitting this copied `local/sensitive-patterns.txt` into its worktree and mirrored
        // five private `.md` files to get a green run, then removed them. It was right that it needed
        // them and right to be uneasy; duplicating `PROJECT_NOTES.md` is the exact hazard
        // `persist-working-state.md` names. A gate that can only be satisfied by copying private files
        // around is a gate that manufactures the leak this repo has a rule against.
        //
        // The pointer is still CHECKED on the machine that has `local/` — where a typo is real and
        // findable. This exempts only the case where absence is the designed state.
        if (target.startsWith('local/') && !fs.existsSync(path.join(repo, 'local'))) continue;
        if (fs.existsSync(path.join(repo, target)) || fs.existsSync(path.resolve(dir, target))) continue;
        problems.push(
          `${rel}:${i + 1}: points at "${target}", which does not exist.\n` +
          `      ${lines[i].trim().slice(0, 110)}\n` +
          '      Retire a doc by REDIRECTING its pointers (usually to a DECISIONS D<n>), not just deleting it.');
      }
    }
  }
}

// ── 4. Every shipped package is described somewhere a consumer reads ──────────────────────────────

/** The packable projects, by the same text match the metadata-baseline test uses. */
function packableProjects() {
  const srcDir = path.join(repo, 'src');
  return fs.readdirSync(srcDir, { withFileTypes: true })
    .filter((e) => e.isDirectory())
    .map((e) => e.name)
    .filter((name) => {
      const csproj = path.join(srcDir, name, `${name}.csproj`);
      return fs.existsSync(csproj)
        && fs.readFileSync(csproj, 'utf8').includes('<IsPackable>true</IsPackable>');
    });
}

// Case-SENSITIVE, and fenced against a longer identifier on the right. Both matter here and both are
// verified: `Shenora.IO` must not be satisfied by `Shenora.iOS` (a different package that differs only
// in case) nor by `Shenora.IO.Compression` (a different package that merely starts with it).
function namesPackage(text, name) {
  return new RegExp(`${literal(name)}(?![A-Za-z0-9_.])`).test(text);
}

function checkPackagesAreDocumented() {
  const packable = packableProjects();
  if (packable.length === 0) {
    problems.push('no packable project found under src/ — this check would pass for the wrong reason.');
    return;
  }
  // DECISIONS.md joined this list after the 0.10.0 release, where its header — written the day before as
  // "the package set lives HERE, once" — still said eight packable projects and omitted Shenora.Launcher.
  // The one place a fact is supposed to live is the place worth gating.
  for (const rel of ['README.md', path.join('docs', 'ARCHITECTURE.md'), path.join('docs', 'DECISIONS.md')]) {
    const file = path.join(repo, rel);
    // FAIL CLOSED. `checkDependencyGraph` above skips a missing file because it checks claims that
    // may or may not be made; this one checks that a REQUIRED document exists and says something, so
    // a missing file is the loudest possible version of the failure, not a reason to pass.
    if (!fs.existsSync(file)) {
      problems.push(`${rel.replace(/\\/g, '/')} does not exist, so no package can be documented in it.`);
      continue;
    }
    const text = fs.readFileSync(file, 'utf8');
    for (const name of packable) {
      if (namesPackage(text, name)) continue;
      problems.push(
        `${rel.replace(/\\/g, '/')}: never names the shipped package "${name}".\n` +
        '      A package nobody describes is one an adopter cannot find, and no other gate looks for it.');
    }
  }
}

// Naming every package is not the same as counting them right: a reader who trusts "there are eight"
// stops looking after eight. The count is prose, so nothing had ever checked it, and it was wrong within
// a day. Spelled-out numbers are allowed because that block reads better with them.
const NUMBER_WORDS = {
  one: 1, two: 2, three: 3, four: 4, five: 5, six: 6,
  seven: 7, eight: 8, nine: 9, ten: 10, eleven: 11, twelve: 12,
};

function checkPackageCountClaim() {
  const rel = 'docs/DECISIONS.md';
  const file = path.join(repo, 'docs', 'DECISIONS.md');
  if (!fs.existsSync(file)) {
    problems.push(`${rel} does not exist, so the canonical package set has nowhere to live.`);
    return;
  }
  // Case-INSENSITIVE: the claim is as valid starting a sentence ("There are five…") as mid-clause, and a
  // gate that fails on a capital letter teaches people to reword the sentence it is reading.
  const match = /there are ([A-Za-z]+|\d+) packable projects/i.exec(fs.readFileSync(file, 'utf8'));
  // FAIL CLOSED. If the sentence is reworded out of existence this check would otherwise pass forever
  // while silently checking nothing — the "gate that fails open" class this repo has already been bitten by.
  if (!match) {
    problems.push(`${rel}: no "there are <n> packable projects" claim found.\n`
      + '      That sentence is the canonical count and this check reads it; reword it and the gate goes\n'
      + '      blind, so the wording is load-bearing. Restore it, or update doc-drift deliberately.');
    return;
  }
  const claimed = /^\d+$/.test(match[1]) ? Number(match[1]) : NUMBER_WORDS[match[1].toLowerCase()];
  if (claimed === undefined) {
    problems.push(`${rel}: "there are ${match[1]} packable projects" — not a number this check understands.\n`
      + `      Use a digit or one of: ${Object.keys(NUMBER_WORDS).join(', ')}.`);
    return;
  }
  const actual = packableProjects().length;
  if (claimed !== actual) {
    problems.push(`${rel}: claims ${claimed} packable projects, but src/ has ${actual} `
      + `(${packableProjects().join(', ')}).\n`
      + '      A reader who trusts the count stops looking once they have that many.');
  }
  checkOtherPackageCounts(actual);
}

/**
 * The same count, wherever else it is claimed and however it is phrased.
 *
 * 🔴 THE CHECK ABOVE READ ONE SENTENCE IN ONE FILE. `docs/ARCHITECTURE.md` — the AS-BUILT MAP — opened
 * with "Six packable projects … `Core`, `Ipc`" long after D65 folded IPC in and renamed `Shenora.Core`,
 * and no gate looked, because the wording was not the one sentence the canonical check matches. Found
 * 2026-08-10 by reading. A count is the most checkable claim a doc can make, so leaving all but one
 * instance unchecked was the gap, not the wording.
 */
function checkOtherPackageCounts(actual) {
  const claim = /(?:there are\s+)?([A-Za-z]+|\d+)\s+packable projects/gi;
  for (const file of walk(repo)) {
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    if (rel === 'docs/DECISIONS.md') continue;              // the canonical one, already checked above
    const text = fs.readFileSync(file, 'utf8');
    for (const m of text.matchAll(claim)) {
      // ⚠ Skip a count quoted as HISTORY ("said 'Six packable projects' until…"), which is how this
      // repo corrects itself — the correction would otherwise re-fire the gate it documents.
      const line = text.slice(0, m.index).split(/\r?\n/).pop() + m[0];
      if (/said|until|used to|was |former|no longer/i.test(line)) continue;
      const n = /^\d+$/.test(m[1]) ? Number(m[1]) : NUMBER_WORDS[m[1].toLowerCase()];
      if (n === undefined || n === actual) continue;
      problems.push(`${rel}: claims ${m[1]} packable projects, but src/ has ${actual}.\n`
        + '      The canonical count lives in DECISIONS.md, but a stale copy anywhere is read as true by\n'
        + '      whoever opens that file first.');
    }
  }
}

// ── run ───────────────────────────────────────────────────────────────────────────────────────────

if (listOnly) {
  console.log('retired names watched:', retiredNames().join(', ') || '(none)');
  console.log('packable projects:', packableProjects().join(', ') || '(none)');
  const actual = actualGraph();
  for (const [from, refs] of actual) console.log(`  ${from} -> ${[...refs].join(', ') || '(none)'}`);
  process.exit(0);
}

// ── 6. Every DECISIONS entry number is unique ────────────────────────────────────────────────────
//
// A D-number is a permanent address: shipped XML docs cite them (Mp4Remuxer says D51, UpdateStage says
// D50), so two entries sharing one silently redirects a published reference. Added 2026-08-07 because
// exactly that happened — `D51` was written TWICE on consecutive days and the collision survived four
// sessions, because the file is appended to at the bottom and nobody reads the middle. Cheap and exact:
// the numbers either repeat or they do not.

function checkDecisionNumbers() {
  const file = path.join(repo, 'docs', 'DECISIONS.md');
  if (!fs.existsSync(file)) return;

  const seen = new Map();
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    // Entry headings only — `- **D<n> — …`. A mention of D51 inside a body is a citation, not a heading.
    const match = /^- \*\*D(\d+)\s*—/.exec(lines[i]);
    if (!match) continue;
    const number = match[1];
    if (seen.has(number)) {
      problems.push(
        `docs/DECISIONS.md:${i + 1}: D${number} is already used at line ${seen.get(number)}.\n` +
        `      ${lines[i].trim().slice(0, 110)}\n` +
        '      A D-number is a permanent address cited from shipped XML — give this one the next free number.');
    } else {
      seen.set(number, i + 1);
    }
  }
}


/**
 * Every verb `dev.mjs` implements must be reachable from its own usage line.
 *
 * 🔴 THE USAGE LINE IS THE ONLY DISCOVERY SURFACE FOR A VERB. `stale-scan`, `cite-scan`,
 * `retired-audit`, `update-probe` and `android-jdk` were all absent from it — each shipped with a
 * working `case` and, for three of them, a RULE telling you to run it, while the one place a reader
 * looks said the command did not exist. Found 2026-08-10 during the doc audit.
 *
 * ⚠ This is doc-drift's remit exactly: a tool's own help is prose about the tool, and it goes stale the
 * same way a doc does — silently, and while everything still works for whoever already knew.
 */
/**
 * 🔴 A DURABLE FILE MAY CITE A PATH NO CLONE HAS ONLY IF THE SAME BREATH SAYS SO.
 *
 * Gitignored working state does not exist in CI, in a fresh clone, or for an adopter — and MEASURED
 * 2026-08-10, four of the six `devtools/_*` paths cited across this repo were already gone on the very
 * machine that made them (`_transport-spike`, `_p11-consumer`, `_p6-consumer`, `_p7-profiles`). The two
 * that survive do so by accident.
 *
 * There are two ways to name one and they fail differently:
 *   - as PROVENANCE for a measurement already taken — "a throwaway spike (gitignored) closed the gap".
 *     Fine: it is history, and the reader is told not to go looking.
 *   - as APPARATUS the reader is expected to open — "the harness is in `devtools/_staledate/`".
 *     Broken, because it is not there. That is what `TASKS.md` said on 2026-08-10 for the ONE open box
 *     whose entire content is "go re-run this experiment", and the harness had died with the session
 *     that wrote it.
 *
 * The marker word is the whole test, and it is cheap: say "gitignored", "throwaway", "untracked",
 * "scratch", or that it is gone, and the reader knows to rebuild rather than to search.
 *
 * 🔴 THE RULE IS "YOU CITED SOMETHING NO CLONE WILL HAVE", NOT "YOU CITED ONE SPECIFIC DIRECTORY", and
 * that distinction is not theoretical: this matched the literal string `devtools/_` until 2026-08-13, so
 * a citation to a `.superpowers/…` task report SHIPPED IN A TRACKED TEST FILE and was caught by a human
 * reading the diff. Every candidate path is now put to `git check-ignore` (see `git-scope.mjs`), so the
 * next scratch directory is covered before anybody names it — including one invented after this line.
 *
 * ⚠ TWO CLASSES OF IGNORED PATH ARE EXEMPT, and both are about the READER, which is what this checks:
 *   - BUILD/TOOL OUTPUT (`bin/`, `obj/`, `dist/`, `node_modules/`, `publish/`, `wwwroot/`). Ignored, yes
 *     — but one documented command away for anyone, so `obj/project.assets.json` in ADOPTION.md and
 *     `publish/packages/` in RELEASING.md name something the reader can have. Demanding a "gitignored"
 *     marker there adds a word and no information, and a gate that fires on correct prose gets ignored.
 *   - `local/`, cited deliberately by ~33 lines of tracked prose because it is this repo's documented
 *     private companion. `checkDocLinks` above already exempts it for the same reason, with the
 *     measurement: a gate that can only be satisfied by copying private files around manufactures the
 *     leak `sensitive-info.md` exists to prevent.
 * ⚠ HALF of that exemption's failure mode is safe, not all of it, and the difference decides how long
 * these two lists may get. OMITTING a build-output name is loud (a false positive someone fixes), which
 * is the argument for keeping them short. INCLUDING one is SILENT: a cited path is dropped before the
 * git query if ANY segment matches, so `.superpowers/out/report.md` would never be reported. `out` is the
 * loosest word here and the first to reconsider if this ever needs trimming.
 *
 * ⚠ `.mjs` IS DELIBERATELY NOT SCANNED (the walk's extension filter). A devtools script naming
 * `devtools/_mac/` is not citing evidence, it is naming a directory it CREATES — the tool is the thing
 * that makes the path true. Scanning them produced only false positives, which is how a gate teaches
 * people to ignore it.
 */
function checkThrowawayCitations() {
  // Any repo-relative-looking path with at least one `/`. Deliberately GENEROUS, because git is what
  // decides: an `and/or` scraped out of prose costs one lookup in a batch and is never reported, while a
  // narrow regex is how the last version came to know about exactly one directory.
  const CITE = /(?<![\w./-])((?:[A-Za-z0-9._][A-Za-z0-9._-]*\/)+[A-Za-z0-9._][A-Za-z0-9._-]*\/?)/g;
  const MARKED = /\b(gitignored|untracked|throwaway|scratch|temporary|temp|gone|rebuild|not in the repo|does not exist|gitignore)\b/i;
  // ⚠ CASE-INSENSITIVE, to match how git answered. `core.ignorecase` is true on Windows, so git calls
  // `Bin/App.dll` — a path inside a ZIP FIXTURE in ZipUpdateSourceTests — ignored by the `bin/` rule. An
  // exemption spelled in one case only would have reported a string that is not a repo path at all.
  const REGENERABLE = /(^|\/)(bin|obj|dist|out|node_modules|publish|wwwroot)(\/|$)/i;
  const ARCHIVE = /^local(\/|$)/;

  // PASS 1 — every sighting, so the git query below is ONE process for the whole repo rather than one
  // per file. (`git check-ignore --stdin` takes the batch; a per-call version of this would spawn ~950.)
  const sightings = [];
  for (const file of walk(repo)) {
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    if (HISTORY_BY_DEFINITION.test(rel)) continue;
    if (!/\.(md|cs|ts|tsx|csproj|props|targets)$/.test(rel)) continue;
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      for (const m of lines[i].matchAll(CITE)) {
        // ⚠ A PATTERN IS NOT A PATH, AND BOTH SPELLINGS OF ONE HAD TO BE TAUGHT HERE. `devtools/_*`
        // names the CONVENTION, and the token scraped out of it (`devtools/_`) is ignored by git just as
        // truly as a real path — so without this the gate fires on every sentence that explains the
        // convention, this comment included. `.claude/worktrees/agent-<id>/` is the same thing with a
        // PLACEHOLDER instead of a star (windows-dev-gotchas.md, telling you how to delete one), and it
        // scrapes as `.claude/worktrees/agent-`. Whatever follows the token is what gives it away.
        //
        // 🔴 THE `?? ''` THIS USED TO CARRY MADE THE GUARD SWALLOW EVERY END-OF-LINE CITATION, because
        // `'*<{'.includes('')` IS TRUE. Measured on the tracked tree the day it landed: 59 of 2089
        // scraped tokens (2.8 %) were dropped that way, against 107 for a real `*`/`<`/`{` — and since
        // CITE's class includes `.`, a path ENDING A SENTENCE at a wrap point went with them, which is
        // the commonest shape in prose written like this repo's. It was also the one way this rewrite
        // could be WIDER than the name list it replaced (the old code had no next-char guard at all),
        // i.e. the exact defect class the header warns about. The tell was cheap and specific: after the
        // fix, `devtools/README.md`'s freshly-added "gitignored" could be deleted and the gate stayed
        // green — a gate that cannot re-catch the finding it just made someone fix is not a gate.
        // `undefined` means END OF LINE, and end of line is not a pattern.
        const next = lines[i][m.index + m[0].length];
        if (next !== undefined && '*<{'.includes(next)) continue;
        // ⚠ THE SENTENCE'S FULL STOP IS PART OF THE SCRAPED TOKEN, because CITE's class contains `.` —
        // and a token is put to git VERBATIM, so `x/y.actual.` would be answered "not ignored" while
        // `x/y.actual` is ignored. So trailing `.`/`/` come off, and the stripped form is the one worth
        // PRINTING, since a reader copies it.
        // 🔴 STRIPPING THE SLASH IS ONLY SAFE BECAUSE `citedOutOfScope` ASKS BOTH SPELLINGS — git's
        // answer DEPENDS on the trailing slash for a directory that is not on disk, so this strip used to
        // throw the answer away for exactly the paths a clone lacks. It caught a live one the hour it was
        // fixed. ⚠ And `cite-scan` is NOT the precedent this comment used to claim: it strips ``.,:;` and
        // never `/`, because it resolves with `fs.existsSync` rather than by asking git.
        const key = m[1].replace(/[./]+$/, '');
        if (REGENERABLE.test(key) || ARCHIVE.test(key)) continue;
        sightings.push({ rel, line: i + 1, token: key, key, text: lines[i], lines, index: i });
      }
    }
  }

  // PASS 2 — one query. FAIL SAFE: if git cannot answer (no git, not a checkout, a batch it rejected),
  // fall back to the two directories this check knew by name before, rather than treating an empty
  // answer as "nothing is out of scope". That is the historical behaviour, never less of it.
  let ignored = citedOutOfScope(sightings.map((s) => s.key));
  if (ignored === null || !scopeViaGit()) {
    ignored = new Set(sightings.map((s) => s.key).filter((k) => /^(devtools\/_|\.superpowers(\/|$))/.test(k)));
    console.error('  !!  doc-drift: citation check fell back to the built-in scratch list — git could not '
      + 'say which cited paths are ignored, so a NEW scratch directory would not be caught here.');
  }

  for (const s of sightings) {
    if (!ignored.has(s.key)) continue;
    // The marker may sit a line either side — these citations are usually mid-sentence in wrapped prose.
    const context = s.lines.slice(Math.max(0, s.index - 1), s.index + 2).join(' ');
    if (MARKED.test(context)) continue;
    problems.push(
      `${s.rel}:${s.line}: cites ${s.token} as if a reader could open it.\n`
      + `      ${s.text.trim().slice(0, 100)}\n`
      + '      git says that path is ignored — absent in CI, in a fresh clone, and for an adopter.\n'
      + '      Say "gitignored"/"throwaway"/"gone" so it reads as provenance, or carry what is needed to\n'
      + '      REBUILD it. An apparatus a reader cannot open is worse than none: it stops them looking.');
  }
}

function checkDevVerbsAreDiscoverable() {
  const rel = 'devtools/dev.mjs';
  const file = path.join(repo, rel);
  if (!fs.existsSync(file)) return;
  const source = fs.readFileSync(file, 'utf8');

  const implemented = [...new Set([...source.matchAll(/^\s*case '([a-z0-9-]+)':/gm)].map((m) => m[1]))];
  // Everything the help block prints — the usage line plus the grouped lines printed under it. Taken as
  // one blob because a verb is discoverable if it appears ANYWHERE a reader running `dev.mjs` sees.
  // ⚠ ANCHOR ON THE MAIN USAGE LINE SPECIFICALLY. `dev.mjs` prints a SECOND, per-verb usage string
  // ("usage: node devtools/dev.mjs responsiveness <fx> <fy>…") 200 lines earlier, and an `indexOf` on
  // the common prefix lands there — reading a region that mentions no verbs at all while appearing to
  // work. That version passed its own sabotage, which is the only reason it was caught.
  const helpStart = source.indexOf('usage: node devtools/dev.mjs <');
  const help = helpStart < 0 ? '' : source.slice(helpStart, helpStart + 1200);
  if (!help) {
    problems.push(`${rel}: no usage block found, so no verb can be discoverable.`);
    return;
  }

  const undiscoverable = implemented.filter((v) => !help.includes(v)).sort();
  if (undiscoverable.length > 0) {
    problems.push(`${rel}: verb(s) implemented but absent from the usage output: `
      + `${undiscoverable.join(', ')}. The usage line is the only place a verb is discoverable — a tool `
      + 'nobody can find is a tool nobody runs, however well it works or however many rules point at it.');
  }
}

/**
 * 🔴 THE OTHER DIRECTION: a verb PROSE NAMES that `dev.mjs` does not implement.
 *
 * The check above asks "is every implemented verb discoverable?" and that is only half of it. The half it
 * misses shipped for months: `RealSourceSegmentTests` told every reader to run `node devtools/dev.mjs
 * media-decode`, describing what it would hand to ffmpeg and to a WebView2 MediaSource — and the verb had
 * never been written. Nothing noticed, because a doc naming a tool that does not exist breaks no build,
 * resolves no link, and renames nothing.
 *
 * ⚠ IT IS WORSE THAN A DEAD LINK. A reader who cannot find the command assumes the gap is theirs; a
 * reviewer reads the sentence as evidence the check exists and stops asking for one. The claim was written
 * from the design, which is exactly the failure `doc-claims.md` names — and it survived every gate in this
 * repo until someone tried to run it.
 *
 * ⚠ Only the FIRST token after `dev.mjs` is a verb — `test clipboard`, `android probes` and
 * `knowledge new <name>` all pass their remainder to the verb. A placeholder (`<cmd>`, `<build|test|…>`)
 * is not a claim about anything and is skipped.
 */
function checkNamedDevVerbsExist() {
  const dev = path.join(repo, 'devtools/dev.mjs');
  if (!fs.existsSync(dev)) return;
  // ⚠ NOT line-anchored: `case 'wgc': case 'click': … case 'input': {` puts six verbs on ONE line, and a
  // `^\s*case` pattern sees only the first. The check above gets away with that anchor because it only
  // asks whether implemented verbs are advertised; here it would report real verbs as fictional.
  const implemented = new Set(
    [...fs.readFileSync(dev, 'utf8').matchAll(/case '([a-z0-9-]+)':/g)].map((m) => m[1]));

  const named = new Map();     // verb -> first "file:line" that names it
  for (const file of walk(repo)) {
    if (!/\.(md|cs|ts|tsx|mjs)$/.test(file)) continue;
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    // dev.mjs itself declares them; CHANGELOG and retired-names are history by definition.
    if (rel === 'devtools/dev.mjs' || rel === 'CHANGELOG.md' || rel.includes('retired-names')) continue;
    if (outOfScope(rel)) continue;

    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
      for (const m of lines[i].matchAll(/dev\.mjs\s+([A-Za-z][A-Za-z0-9-]*)/g)) {
        const verb = m[1];
        if (implemented.has(verb) || named.has(verb)) continue;

        // ⚠ IS THIS A COMMAND OR A SENTENCE? `dev.mjs` is also an ordinary subject — "(dev.mjs sets it
        // from project.config.mjs)" and "set by dev.mjs from …" both parse as `dev.mjs <verb>` and name
        // no command at all. A real invocation is either fenced in backticks or spelled `node …/dev.mjs`.
        const before = lines[i].slice(Math.max(0, m.index - 24), m.index);
        if (!before.endsWith('`') && !/node\s+\S*$/.test(before)) continue;

        named.set(verb, `${rel}:${i + 1}`);
      }
    }
  }

  if (named.size > 0) {
    const said = [...named].map(([v, at]) => `${v} (${at})`).join(', ');
    problems.push(`prose names dev.mjs verb(s) that do not exist: ${said}. Either write the verb or stop `
      + 'promising it — a reader who cannot find the command assumes the gap is theirs, and a reviewer '
      + 'reads the sentence as evidence the check already exists.');
  }
}

// ── 9. A doc comment carrying TWO <summary> elements ──────────────────────────────────────────────
//
// 🔴 THE DEFECT IS AN INSERTION, NOT A TYPO, WHICH IS WHY READING NEVER FINDS IT. A declaration added
// at the top of a file ADOPTS the doc block above it, and the previous owner keeps compiling with
// whatever stub is left. Proven by history: `git show 1d095d4` has a 52-line design essay as
// `ComputedRemuxExtensions`'s class doc; D72 inserted `MediaPlanOutcome` and `IComputedRemuxRoute`
// above it and the essay transferred to a four-member ENUM, leaving the call an adopter WRITES
// documented as "Registration for the computed-remux route." Six members shipped this way (2026-08-14).
//
// ⚠ NO EXISTING GATE COULD SEE IT, and the reason generalises. `doc-drift`'s other checks, `cite-scan`,
// `stale-scan` and `self-rename-scan` all ask whether a SENTENCE is true; every sentence here is true.
// What is wrong is WHICH MEMBER it is attached to — a fact that exists only in the emitted XML, and one
// the compiler never warns about because two summaries are legal.
//
// Checked at SOURCE rather than against `bin/**/*.xml`: it needs no build, it covers `tests/` and every
// project regardless of `GenerateDocumentationFile`, and it can name a file:line.
function checkDoubledSummaries() {
  for (const file of walk(repo)) {
    if (!file.endsWith('.cs')) continue;
    const rel = path.relative(repo, file).replace(/\\/g, '/');
    const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);

    // One doc block = every `///` line preceding a declaration. ⚠ Blank lines, attributes and
    // preprocessor directives must NOT break the run: the compiler joins across them, which is exactly
    // how the `Mp4Remuxer.Remux` case hid — its two summaries are separated by a blank line, so a
    // naive contiguous-run parser reports it clean.
    let start = 0, summaries = [];
    for (let i = 0; i <= lines.length; i++) {
      const line = (lines[i] ?? 'end').trim();
      if (line.startsWith('///')) {
        if (summaries.length === 0) start = i + 1;
        // Only a tag opening its own line counts, so a `<summary>` quoted inside a <code> sample or
        // discussed in prose cannot fire this.
        if (/^\/\/\/\s*<summary>/.test(line)) summaries.push(i + 1);
        continue;
      }
      if (line === '' || line.startsWith('[') || line.startsWith('#')) continue;  // see above
      if (summaries.length > 1) {
        problems.push(`${rel}:${summaries[1]}: this doc comment carries ${summaries.length} <summary> `
          + `elements (the first opens at :${summaries[0]}), so one member ships another member's `
          + 'documentation and its rightful owner ships none. Almost always an insertion: a declaration '
          + 'added above an existing doc block adopts it. Move the orphaned block down to the member it '
          + 'describes — do not merge them.');
      }
      summaries = [];
    }
  }
}

checkDependencyGraph();
checkRetiredNames();
checkDocLinks();
checkPackagesAreDocumented();
checkPackageCountClaim();
checkDecisionNumbers();
checkThrowawayCitations();
checkDevVerbsAreDiscoverable();
checkNamedDevVerbsExist();
checkDoubledSummaries();

if (problems.length === 0) {
  console.log('  ok  doc-drift: dependency graph matches the csproj files; no retired name stated as ' +
              'current; every docs/ pointer resolves; every packable project is documented and counted; ' +
              'no duplicate DECISIONS number; every dev.mjs verb is discoverable and every verb prose '
              + 'names exists; '
              + 'no path git calls ignored is cited as if a reader could open it; '
              + 'no doc comment carries two <summary> elements');
  process.exit(0);
}
console.error(`\n\x1b[31m✖ doc-drift: ${problems.length} stale claim(s) in prose:\x1b[0m`);
for (const p of problems) console.error(`  ${p}`);
console.error('\nProse is the one surface with no compiler. See devtools/retired-names.txt to retire a name.\n');
process.exit(1);
