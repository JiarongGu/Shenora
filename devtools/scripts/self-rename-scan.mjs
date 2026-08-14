// self-rename-scan — sentences naming the SAME identifier on both sides of a relation.
//
// 🔴 THE ARTEFACT THIS CATCHES IS A RENAME THAT RAN OVER ITS OWN SUBJECT. A repo-wide sweep replaces the
// old name everywhere — including the one sentence whose subject WAS the old name — and leaves
// "`X` depends on `X`", "`X` → `X`", "`X` layers on `X`". The result is grammatical, passes every gate,
// and is nonsense at the exact spot a reader goes to learn what changed.
//
// Found FIVE of these across the docs on 2026-08-09/10 (ARCHITECTURE, REVIEW-GUIDE, DECISIONS' header,
// D65's own entry, webview2-hosting) — three of them AFTER two separate prose audits had run clean.
// doc-drift and cite-scan cannot see it: both names exist, and they are the same name.
//
// ⚠ Never a gate. Most hits are legitimate repetition ("setting `Commit` makes `Commit` exempt"), so it
// prints a worklist and a human decides — the same standing as stale-scan.
// 🔴 IT READ ONLY `.md` UNTIL 2026-08-10, WHICH LEFT THE HIGHEST-STAKES PROSE UNWATCHED. A shipped XML
// doc comment IS prose — it renders in an adopter's IDE straight from the nupkg, where this repo never
// reads it. `WinFormsUiDispatcher` told adopters that "Shenora.Windows and Shenora.Windows consume it
// across the package boundary" for eight days: the same name on both sides, produced by D37's merge
// sweep, in the paragraph explaining why the type is public. Docs get audited by eye; XML docs do not.
//
// ⚠ An XML doc WRAPS, so the two halves of a self-rename routinely sit on different lines — a per-line
// match would have added the file type and stayed blind to the actual defect. Comment blocks are joined
// before matching.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { outOfScope } from './git-scope.mjs';

// The script's own location. This read `process.cwd()` until 2026-08-10 — the same defect fixed in
// cite-scan, stale-scan and retired-audit one commit earlier, and MISSED here, which is the tell that a
// sweep driven by a list of files is a sweep that stops at the edge of the list.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

// Out of scope = what a clone does not have, asked of git rather than listed by name (`git-scope.mjs`),
// so this file never has to learn the name of the next scratch directory. Nothing about `local/` changes:
// it is still skipped, still because it is private and an informal ARCHIVE.
const SKIP_FILE = /(^|[\/])CHANGELOG\.md$/;   // history by construction, like doc-drift treats it
/** Prose that SHIPS: XML doc comments in C#, JSDoc in TS. Same failure, wider blast radius. */
const CODE_EXT = /\.(cs|ts|tsx)$/;

const files = [];
const walk = (dir) => {
  let entries = [];
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (outOfScope(path.relative(repo, full))) continue;
    // Another checkout (agent worktree, nested clone) — see doc-drift's `isNestedCheckout`. A worktree is
    // not gitignored, so the query above does not cover it and this rule stays.
    if (e.isDirectory() && fs.existsSync(path.join(full, '.git'))) continue;
    if (e.isDirectory()) walk(full);
    else if ((e.name.endsWith('.md') || CODE_EXT.test(e.name)) && !SKIP_FILE.test(full)) {
      files.push(full.split(path.sep).join('/'));
    }
  }
};
for (const root of ['docs', '.claude', 'src']) walk(path.join(repo, root));
for (const f of ['README.md', 'CLAUDE.md', 'TASKS.md']) {
  if (fs.existsSync(path.join(repo, f))) files.push(path.join(repo, f));
}

/**
 * The units a sentence can live in.
 *
 * For markdown that is a LINE. For code it is a whole COMMENT BLOCK, joined — an XML doc wraps at ~110
 * columns, so the two halves of a self-rename land on different lines and a per-line matcher sees
 * neither. `WinFormsUiDispatcher`'s defect spanned lines 12–13 and is invisible to a line scan.
 *
 * `<c>X</c>`, `cref="X"` and `{@link X}` are normalised to backticks so ONE matcher serves every
 * language rather than three that drift apart.
 */
function units(file, text) {
  const lines = text.split(/\r?\n/);
  if (!CODE_EXT.test(file)) return lines.map((line, i) => [i + 1, line]);

  const out = [];
  let start = 0, buf = null;
  const flush = () => { if (buf !== null) out.push([start + 1, buf]); buf = null; };
  for (let i = 0; i < lines.length; i++) {
    const comment = lines[i].match(/^\s*(?:\/\/\/?|\*)\s?(.*)$/);
    if (!comment) { flush(); continue; }
    if (buf === null) { start = i; buf = comment[1]; } else { buf += ' ' + comment[1]; }
  }
  flush();
  return out.map(([n, s]) => [n, s
    .replace(/<c>([^<]+)<\/c>/g, '`$1`')
    .replace(/<see\s+cref="(?:[^"]*[.:])?([A-Za-z0-9_]+)"\s*\/?>/g, '`$1`')
    .replace(/\{@link\s+([A-Za-z0-9_.]+)\}/g, '`$1`')]);
}

let hits = 0;
for (const file of files) {
  const rel = path.relative(repo, file).split(path.sep).join('/');
  for (const [lineNo, line] of units(file, fs.readFileSync(file, 'utf8'))) {
    // The same backticked identifier twice with only NON-CODE text between them.
    // ⚠ The window was 45 chars and missed a real one in REVIEW-GUIDE's commit table, where the two
    // halves of "`Shenora.WinForms` bootstrap/window-state/single-instance; `Shenora.WebView2` hosting"
    // sit ~40–90 chars apart after the rename collapsed both to the same name. A tuned threshold is a
    // guess about prose; the honest bound is "same sentence", so it runs to the end of the line.
    const m = line.match(/`([A-Za-z][\w.]{4,})`([^`]{2,140})`\1`/);
    if (!m) continue;
    hits++;
    console.log(`${rel}:${lineNo}  …\`${m[1]}\`${m[2]}\`${m[1]}\``);
  }
}
console.log(`\nself-rename-scan: ${hits} line(s) naming one identifier twice in a clause.`);
console.log('TRIAGE BY HAND — legitimate repetition is common; you are looking for a sentence that says');
console.log('a thing relates to ITSELF, which is what a rename sweep leaves behind. Never fails a build.');
