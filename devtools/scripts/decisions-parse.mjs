// decisions-parse — the ONE way to find `docs/DECISIONS.md`'s entries and where each one ends.
//
// 🔴 WHY THIS EXISTS, in the words the copies themselves carried. Three tools split this file three
// ways — `decisions-index` (the generated table of contents), `decision-audit` (are entries still true?)
// and `doc-shape` (the per-entry line cap) — and two of them ran a BYTE-IDENTICAL `sectionAfter` with
// the same bug fixed twice, separately:
//
//   "🔴 THE LAST ENTRY ENDS AT ITS SECTION, NOT AT EOF — the same defect `doc-shape` carried, in a
//    second copy of the same idea. ⚠ Two tools splitting the same file two ways is how one gets fixed
//    and the other does not."   — decision-audit.mjs, which then kept its own copy anyway.
//
// So this is not a tidiness refactor: it is the third copy not being written.
//
// ⚠ SCOPE, deliberately narrow. `decisions-index` keeps its own `readEntries`, because it needs
// something the other two do not — a title JOINED across wrapped lines, and the tombstone form
// `- **D40 · D41 — …**`. Folding that in here would change what the audit and the shape check MATCH,
// which is a behaviour change wearing a refactor's clothes. What is shared is what was duplicated.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

/** `docs/DECISIONS.md`, repo-relative — named once so a move is one edit. */
export const DECISIONS_REL = 'docs/DECISIONS.md';

/**
 * An entry's opening line: `- **D48 — the file-operation engine…**`.
 * ⚠ Captures the FIRST D-number only, which is what both consumers have always done — a tombstone
 * (`D40 · D41`) reports as `D40` here. `decisions-index` is the one that spells both.
 */
export const MARK = /^-\s+\*\*(D(\d+))\s*(?:—|-|–)?\s*(.*)$/;

/** The file's lines, split on either ending. */
export function decisionLines() {
  return fs.readFileSync(path.join(repo, DECISIONS_REL), 'utf8').split(/\r?\n/);
}

/** Every entry mark: `{ id, n, title, at }`, in file order. */
export function decisionMarks(lines) {
  const marks = [];
  lines.forEach((line, at) => {
    const m = MARK.exec(line);
    // The TITLE is carried, not just the number: a report saying "D70 is 28 lines" says nothing about
    // what D70 IS, so a reader must open the file for every row — and skipping that is how "39 over
    // the cap, none of them media" got written about a list whose largest entry was media.
    if (m) marks.push({ id: m[1], n: Number(m[2]), title: m[3] ?? '', at });
  });
  return marks;
}

/**
 * Where an entry's body ends: the next `## ` heading, NOT end-of-file.
 * 🔴 Running the last entry's span to `lines.length` swallows everything after it — the whole
 * "## Anti-goals" section — so whichever entry happened to be last reported ~100 lines whatever it
 * actually said, and could not be brought under a cap by editing it at all. A check no edit can
 * satisfy is one its reader learns to skip.
 */
export function sectionAfter(lines, at) {
  for (let i = at + 1; i < lines.length; i++) if (/^##\s/.test(lines[i])) return i;
  return lines.length;
}
