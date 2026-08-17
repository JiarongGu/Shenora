// decisions-index — DECISIONS.md opens with the LIST of what was decided, generated from the entries.
//
// 🔴 WHY THIS EXISTS (owner, 2026-08-14: *"I dont think currently its been proerly list all the
// decisions"*). The file held 75 decisions and no way to see them: answering "what has been decided
// about packaging?" meant scrolling 1,400 lines, and a decision nobody can find is one that gets taken
// again. A numbered rationale needs a table of contents more than most documents, because its entries
// are addressed by NUMBER from code and XML docs — you arrive knowing `D48` and needing to know what
// D48 is.
//
// ⚠ GENERATED, never hand-maintained, for the reason this repo distrusts second copies: a hand-written
// index is a second statement of every title that drifts the first time one is reworded, and it drifts
// SILENTLY because nobody reads an index to check it. `--check` fails the build when it is stale, which
// is the same contract `wire-reference` has.
//
// The index states each entry's own title and nothing else. That is deliberate: if a title does not
// read as a decision, the fix is the ENTRY, not a cleverer summary here — the index is a mirror, and a
// mirror that flatters is worth nothing.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const rel = 'docs/DECISIONS.md';
const file = path.join(repo, rel);

const START = '<!-- decisions-index:start -->';
const END = '<!-- decisions-index:end -->';

/** Every entry's id and its decision line, read from the entry itself. */
export const readEntries = (text) => {
  const lines = text.split(/\r?\n/);
  const entries = [];

  for (let i = 0; i < lines.length; i++) {
    // `- **D48 — the file-operation engine is its own LAYER…**` — and the tombstone form `D40 · D41`.
    const head = lines[i].match(/^-\s+\*\*(D\d+(?:\s*·\s*D\d+)*)\s*(?:—|–|-)\s*(.*)$/);
    if (!head) continue;

    // A title wraps, so read on until the bold closes. Bounded: a title that never closes is a
    // malformed entry, and swallowing the rest of the file would hide it rather than report it.
    let title = head[2];
    for (let j = i + 1; !title.includes('**') && j < Math.min(i + 6, lines.length); j++) {
      title += ' ' + lines[j].trim();
    }

    const close = title.indexOf('**');
    title = (close >= 0 ? title.slice(0, close) : title).replace(/\s+/g, ' ').trim();
    entries.push({ id: head[1], title });
  }

  return entries;
};

/**
 * ONE SENTENCE, cut at a real boundary — never at a character count.
 *
 * ⚠ A hard truncation is how a summary comes to say something the entry does not: cutting
 * "no dependency between them" mid-clause inverts it. So this takes the first sentence whole, however
 * long, and only treats `. ` as a boundary when it is not inside a code span or an abbreviation.
 */
export const firstSentence = (title) => {
  let ticks = 0;
  for (let i = 0; i < title.length; i++) {
    if (title[i] === '`') ticks++;
    if (title[i] !== '.') continue;
    if (ticks % 2 === 1) continue;                       // inside `code.span`
    const next = title[i + 1];
    if (next !== undefined && next !== ' ') continue;    // `0.10.0`, `dev.mjs`
    const before = title.slice(0, i);
    if (/\b[A-Z]$/.test(before)) continue;               // an initial
    return before.trim() + '.';
  }
  return title;
};

const build = (entries) => [
  START,
  '',
  '| | |',
  '|---|---|',
  ...entries.map((e) => `| **${e.id}** | ${firstSentence(e.title)} |`),
  '',
  END,
].join('\n');

const main = () => {
  const check = process.argv.includes('--check');
  const text = fs.readFileSync(file, 'utf8');
  const entries = readEntries(text);

  if (entries.length === 0) {
    console.error('decisions-index: no entries found — the entry shape changed, which this cannot guess at');
    process.exitCode = 1;
    return;
  }

  const wanted = build(entries);
  const from = text.indexOf(START);
  const to = text.indexOf(END);

  if (from < 0 || to < 0) {
    console.error(`decisions-index: ${rel} carries no index markers (${START} … ${END})`);
    process.exitCode = 1;
    return;
  }

  const current = text.slice(from, to + END.length);
  // Compared with endings NORMALIZED: `wanted` is '\n'-joined while an autocrlf checkout hands this
  // file over as CRLF — the exact split the WRITER below already handles, and the check half did
  // not, so the release runner failed a gate every LF checkout passed (2026-08-18). Content is the
  // claim being checked; the line ending is the checkout's business.
  if (current.replaceAll('\r\n', '\n') === wanted) {
    console.log(`  ok  decisions-index: ${entries.length} decisions listed, matching the entries`);
    return;
  }

  if (check) {
    console.error(`  FAIL  decisions-index: the index no longer matches the entries in ${rel}`);
    console.error('        Regenerate with: node devtools/dev.mjs decisions-index');
    process.exitCode = 1;
    return;
  }

  // ⚠ CRLF is what a checkout has here, and rewriting the file with LF would show every line as
  // changed — the anomaly `git diff --numstat` is supposed to catch made routine.
  const eol = text.includes('\r\n') ? '\r\n' : '\n';
  const next = text.slice(0, from) + wanted.split('\n').join(eol) + text.slice(to + END.length);
  fs.writeFileSync(file, next);
  console.log(`decisions-index: wrote ${entries.length} decisions into ${rel}`);
};

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) main();
