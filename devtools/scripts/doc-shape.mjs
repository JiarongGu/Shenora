// doc-shape — the SHAPE rules the docs are supposed to hold, made mechanical.
//
// 🔴 WHY THIS EXISTS. Until 2026-08-14 `DECISIONS.md`'s header said *"Amend an entry by appending a
// dated note — never silently rewrite"*, and that one sentence was the drift engine:
//   1. docs could only GROW — a correction annotated the wrong sentence instead of replacing it, so
//      DECISIONS.md reached 3,207 lines for 74 entries and 47 % of them still stated something untrue;
//   2. it BLINDED the gate that would have caught the drift — `doc-drift` suppresses any hit within 6
//      lines of a history word (`was|were|until|no longer|replaced|…`), and an amendment stack keeps
//      that suppression permanently on. `retired-names.txt` already said so and nothing acted on it.
// The rule is now correct-in-place, with the WHY in the commit message. This gate is what stops the
// old habit coming back, because the repo's own scoring says a rule loses and a mechanism wins.
//
// Report-only by default (the `stale-scan` standing). `--check` makes it fail, for `verify`.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { outOfScope } from './git-scope.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const check = process.argv.includes('--check');

// ⚠ SEVERITY IS DELIBERATE, and the split is the repo's own rule: correctness stops a release, style
// warns (`phase-workflow.md`, earned when a size budget was made fatal and then blocked shipping over
// 0.2 KB). A doc narrating its own past is a CORRECTNESS defect — it is the thing that blinds
// `doc-drift` — so it fails. A long entry is a style budget, so it warns and never blocks.
const findings = [];
const flag = (file, line, msg, why, severity = 'fail') => findings.push({ file, line, msg, why, severity });

// ── 1. no dated self-narration in a TRACKED doc ───────────────────────────────────────────────────
//
// The shapes an amendment actually takes in this repo, harvested from the 153 such lines DECISIONS.md
// carried. Each says "this document was wrong", which is a fact about the DOCUMENT — git already has
// it, and in always-loaded context it is paid for on every session forever.
//
// ⚠ The test that separates this from legitimate history: a fact about the SYSTEM stays ("the renderer
// dies ~8 s in under `timeout`"), a fact about the DOCUMENTATION goes ("this rule cited RunSta until
// 2026-08-09"). Only the second names a doc element as its subject, which is what these match.
// ⚠ `this`, never `the`. Written with `(this|the)` it fired on "…discarding uncommitted edits the
// file already carried" — a fact about GIT, in a gotcha that must stay. A doc narrating itself always
// points AT itself, so the demonstrative is the whole signal; `the file` is just English.
// 🔴 PAST TENSE ONLY, AND ONLY WITH A CORRECTION MARKER IN THE SAME BLOCK. Both narrowings were forced
// by real false positives, and both say the same thing: naming a doc element is not the signal — saying
// it was WRONG is.
//   * `says` (present) removed. `ARCHITECTURE.md says what it is and this file says why` is D57 stating
//     which doc holds what, which is the DECISION, not narration about it.
//   * a correction marker is now required nearby, because `"who holds this file?" said cannot tell` —
//     D63, about a file on DISK inside a quoted question — matched `this file … said` and is a fact
//     about the SYSTEM.
// Dated narration needs neither narrowing and is caught by the next pattern regardless, so nothing that
// used to fail here escapes: every real instance found in this repo carried `until`, `used to` or a date.
const CORRECTION_NEARBY =
  /\b(until|used to|use to|no longer|previously|originally|formerly|was (wrong|false|stale|untrue)|were (wrong|false)|corrected|amended|superseded|for (two|three|four|\d+) days)\b/i;

const SELF_NARRATION = [
  [/\bthis (line|entry|paragraph|bullet|sentence|block|claim|file|rule|doc|table|section|header)\b[^.]{0,80}\b(said|listed|called|carried|stated|read|announced|claimed|asserted|used to)\b/i,
    'narrates what this document used to say', CORRECTION_NEARBY],
  [/\buntil 20\d\d-\d\d-\d\d\b/i, 'dates when the prose was wrong'],
  [/\b(corrected|amended|amendment) (in place |here |above |below )?(on )?20\d\d-\d\d-\d\d\b/i,
    'stamps a correction into the prose'],
  [/\bwhich was (also )?(false|wrong|untrue)\b/i, 'narrates a past wrong claim'],
  [/\bthis (was|is) (wrong|false|stale|obsolete)\b/i, 'narrates a past wrong claim'],
];

// History BY DEFINITION — the same exemption doc-drift grants, for the same reason. `local/` is an
// informal ARCHIVE (owner, 2026-08-08): its whole value is recording what was true THEN.
const EXEMPT = /^(CHANGELOG\.md|devtools\/retired-names\.txt|local\/)/;

const docs = [];
const findDocs = (dir) => {
  let entries = [];
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    const rel = path.relative(repo, full).split(path.sep).join('/');
    if (outOfScope(rel) || EXEMPT.test(rel)) continue;
    if (e.isDirectory()) {
      if (fs.existsSync(path.join(full, '.git'))) continue;
      findDocs(full);
    } else if (e.name.endsWith('.md') && e.name !== 'TEMPLATE.md') docs.push(rel);
  }
};
for (const root of ['docs', '.claude']) findDocs(path.join(repo, root));
for (const f of ['CLAUDE.md', 'README.md', 'TASKS.md']) {
  if (fs.existsSync(path.join(repo, f))) docs.push(f);
}

// ⚠ Say how many. A clean answer here is silence, and so is scanning nothing.
console.log(`doc-shape: ${docs.length} tracked doc(s) in scope (CHANGELOG.md, retired-names.txt and local/ are history by definition).`);
if (docs.length === 0) {
  console.error('doc-shape: NOTHING TO SCAN — the repo layout is not what this expects.');
  process.exit(1);
}

// 🔴 MATCHED OVER JOINED BLOCKS, NOT PER LINE — this repo wraps prose at ~100 columns, so a
// self-narrating sentence routinely straddles the break. Found by sabotaging this very check:
// `windows-dev-gotchas.md` ended a line with "…cited an `OptimizedFormTests.RunSta` helper until"
// and carried "2026-08-09" onto the next, so a per-line matcher saw neither half and the file read
// as clean. `phase-workflow.md` already records the same failure for `self-rename-scan`; a per-line
// matcher is the recurring bug in this repo's prose tooling, not a one-off.
const blocks = (lines) => {
  const out = [];
  let start = -1; let buf = [];
  const flush = () => { if (buf.length) out.push({ start, text: buf.join(' ') }); start = -1; buf = []; };
  lines.forEach((l, i) => {
    if (l.trim() === '') { flush(); return; }
    if (start < 0) start = i;
    buf.push(l.trim());
  });
  flush();
  return out;
};

for (const rel of docs) {
  const lines = fs.readFileSync(path.join(repo, rel), 'utf8').split(/\r?\n/);
  for (const block of blocks(lines)) {
    for (const [re, why, requires] of SELF_NARRATION) {
      const m = block.text.match(re);
      if (!m) continue;
      if (requires && !requires.test(block.text)) continue;
      // Report the line the MATCH starts on, not the block's — a 30-line block would otherwise
      // point at prose that is fine and make the finding untraceable.
      const before = block.text.slice(0, m.index);
      const offset = before.length === 0 ? 0
        : lines.slice(block.start).findIndex((l, k) =>
          lines.slice(block.start, block.start + k + 1).map((x) => x.trim()).join(' ').length > before.length);
      flag(rel, block.start + Math.max(0, offset) + 1, m[0].slice(0, 100), why);
      break;
    }
  }
}

// ── 2. DECISIONS.md: entry shape and number integrity ─────────────────────────────────────────────
const decRel = 'docs/DECISIONS.md';
const ENTRY_CAP = 15;
const decPath = path.join(repo, decRel);
if (fs.existsSync(decPath)) {
  const lines = fs.readFileSync(decPath, 'utf8').split(/\r?\n/);
  const marks = [];
  lines.forEach((l, i) => {
    const m = l.match(/^-\s+\*\*(D(\d+))\s*(?:—|-|–)?/);
    if (m) marks.push({ id: m[1], n: Number(m[2]), at: i });
  });

  // ⚠ NO duplicate-number check here on purpose: `doc-drift` check (6) already owns it, and two
  // owners for one invariant is the drift this gate exists to stop. Its own comment carries the D51
  // measurement that earned it.
  marks.forEach((m, j) => {
    const span = (j + 1 < marks.length ? marks[j + 1].at : lines.length) - m.at;
    if (span > ENTRY_CAP) {
      flag(decRel, m.at + 1, `${m.id} is ${span} lines (cap ${ENTRY_CAP})`,
        'an entry is a decision + why + the constraint it imposes; measurements and audits belong in the commit that landed them',
        'warn');
    }
  });
}

// ── 3. TASKS.md holds OPEN work only ──────────────────────────────────────────────────────────────
// The prose rule has been there since 2026-08-05 and was broken twice: the file reached 502 lines
// holding six open tasks, then 570 lines holding three and 25 done-blocks. A `✅` is the same defect
// as `DONE` — it reads as progress while it is really an entry that failed to leave.
const tasksPath = path.join(repo, 'TASKS.md');
if (fs.existsSync(tasksPath)) {
  fs.readFileSync(tasksPath, 'utf8').split(/\r?\n/).forEach((line, i) => {
    if (/^\s*[-*]?\s*(✅|\[x\]|\*\*DONE\*\*|\bDONE\b:)/.test(line)) {
      flag('TASKS.md', i + 1, line.trim().slice(0, 100),
        'an entry is OPEN or GONE — a done marker is an entry that failed to leave, and the file stops tracking remaining work');
    }
  });
}

// ── 4. PROJECT_NOTES.md is CURRENT state, not a session log ───────────────────────────────────────
// Checked by explicit path even though `local/` is out of git scope: this is the file the rule
// `persist-working-state.md` points every session at, and it had 47 headings across SESSION 22,
// ROUND 5/4/2, HISTORY and SUPERSEDED, with `## STATUS — live` buried at line 97.
const notesRel = 'local/PROJECT_NOTES.md';
const notesPath = path.join(repo, notesRel);
if (fs.existsSync(notesPath)) {
  const lines = fs.readFileSync(notesPath, 'utf8').split(/\r?\n/);
  const sessions = [];
  lines.forEach((line, i) => {
    if (/^#{1,3}.*\b(SESSION\s+\d+|ROUND\s+\d+)\b/i.test(line)) sessions.push(i + 1);
    if (/^#{1,3}\s*.{0,4}\b(HISTORY|SUPERSEDED)\b/i.test(line)) {
      flag(notesRel, i + 1, line.trim().slice(0, 100),
        'this file is CURRENT state; history MOVES to local/archive/ (a move, never a rewrite — local/ is not in git)');
    }
  });
  if (sessions.length > 1) {
    flag(notesRel, sessions[1], `${sessions.length} SESSION/ROUND headings`,
      'only the current session belongs here; the rest MOVE to local/archive/');
  }
}

// ── report ────────────────────────────────────────────────────────────────────────────────────────
const byFile = new Map();
for (const f of findings) byFile.set(f.file, [...(byFile.get(f.file) ?? []), f]);

// ⚠ In --check mode the WARN rows are collapsed to one line per file. Printing 43 two-line style rows
// inside every `verify` run is how a gate's output stops being read at all — and the rows a reader must
// act on are the FAIL ones. The full list is one command away, which the summary says.
for (const [file, list] of [...byFile].sort((a, b) => b[1].length - a[1].length)) {
  const shown = check ? list.filter((f) => f.severity === 'fail') : list;
  const hidden = list.length - shown.length;
  if (shown.length === 0 && hidden === 0) continue;
  console.log(`\n=== ${file} — ${list.length} ===`);
  for (const f of shown) {
    console.log(`  ${f.severity === 'warn' ? 'warn ' : 'FAIL '} ${f.file}:${f.line}  ${f.msg}\n      → ${f.why}`);
  }
  if (hidden) console.log(`  warn  ${hidden} style finding(s) — \`node devtools/dev.mjs doc-shape\` lists them.`);
}

const fails = findings.filter((f) => f.severity === 'fail');
const warns = findings.length - fails.length;
console.log(`\ndoc-shape: ${fails.length} failing + ${warns} warning finding(s) across ${byFile.size} file(s).`);
if (!check) {
  console.log('Report mode — nothing gates here. `verify` runs this with --check, where the FAIL rows gate.');
} else if (fails.length) {
  console.error('doc-shape FAILED. Correct the prose in place; the WHY goes in the commit message, not the doc.');
  process.exitCode = 1;
} else if (warns) {
  // ⚠ Say it out loud. A warning nobody prints on a GREEN run is a warning nobody ever reads, and the
  // whole point of the warn tier is that it stays visible without blocking a release.
  console.log(`doc-shape: ok — ${warns} style warning(s) above, which never gate.`);
}
