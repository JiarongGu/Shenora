// cite-scan — identifiers a doc cites in `code spans` that exist NOWHERE in the source tree.
//
// 🔴 WHY IT IS A THIRD TOOL, next to doc-drift and stale-scan. Both of those start from
// `retired-names.txt`: they answer "is a name we KNOW went still described as current?". This one
// starts from the DOCS and needs no list at all — which is the only way to catch the failure that
// actually keeps happening, where step 2 of the rename rule was skipped so the name was never
// retired, and therefore no gate could ever match it.
//
// Found on its first run (2026-08-09), all five invisible to both existing tools:
//   D1  `OperationLifecycleInvariantTests` — enforcement claimed as still running; deleted with D66
//   D48 `RestartManagerFileLockInspector` — a type that never existed (it is …LockInspector)
//   D58 `IMediaRenderTarget`              — never shipped; cited as where the player hands its output
//   D59 `ConvertWith` + its pinning test  — both renamed the DAY that entry was written
//   ARCHITECTURE `Mp4RemuxerResult`       — the type is MediaRemuxerResult
//
// ⚠ A REVIEW TOOL, never a gate, and never in `verify` — the same standing as stale-scan. It reports
// every external API a doc legitimately names (`AVPlayerLayer`, `MediaExtractor`, `WidgetBundle`) and
// every adopter-side type in a migration table, so most hits are correct. **The triage is the
// deliverable**; only a human can tell "their API" from "our deleted one".
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { outOfScope } from './git-scope.mjs';

// ⚠ THE SCRIPT'S OWN LOCATION, not `process.cwd()`, which this used until 2026-08-10. Discovery walks
// `docs/` and `.claude/` relative to this, so running the tool from anywhere but the repo root found ONE
// doc instead of thirty-six and reported the rest of the repo's prose as clean — a silent partial scan,
// which is the same failure as the hand-written file list this discovery replaced, one level up.
// `wire-reference.mjs` already resolved its root this way.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

// 🔴 DISCOVERED, NEVER LISTED — this tool's own subject, applied to itself. It shipped with a
// hand-written array of seven files, and the docs tree grew `docs/guides/`, `docs/getting-started.md`
// and `docs/reference/` in the same release: the gate silently stopped covering the newest prose, which
// is the prose most likely to be wrong. A list of what to check is a list that goes stale.
//
// ⚠ `.claude/` IS in scope and matters MORE than `docs/`: a rule is read as instructions, so a dead
// path there misroutes every session. (`RULES_INDEX.md` pointed at `src/Shenora.Ipc/`, a package D65
// folded, while the rule it indexes had already corrected its own copy of the same line.)
// ⚠ `local/` is excluded — it is an informal ARCHIVE, and so is `CHANGELOG.md`; both record what was
// true THEN, exactly as `doc-drift` already treats them. It is no longer excluded BY NAME: what a clone
// does not have is git's question to answer (`git-scope.mjs`), which covers `local/`, `.superpowers/`,
// `devtools/_*` and whatever the next scratch directory is called, without this line being edited again.
const SKIP_FILE = /(^|[\\/])(CHANGELOG\.md|TEMPLATE\.md)$/;
const findDocs = (dir, out = []) => {
  let entries = [];
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return out; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (outOfScope(path.relative(repo, full))) continue;
    // A directory that is itself a checkout (a worktree's `.git` FILE, a nested clone's `.git` dir)
    // belongs to another copy of the repo — see `isNestedCheckout` in doc-drift.mjs for the measurement.
    // A worktree is NOT gitignored, so this stays a rule of its own.
    if (e.isDirectory() && fs.existsSync(path.join(full, '.git'))) continue;
    if (e.isDirectory()) findDocs(full, out);
    else if (e.name.endsWith('.md') && !SKIP_FILE.test(full)) out.push(full.split(path.sep).join('/'));
  }
  return out;
};

/** Repo-relative, for display and for reading — the walk is absolute so the cwd cannot change the result. */
const relative = (p) => path.relative(repo, p).split(path.sep).join('/');

const docs = process.argv.slice(2).filter((a) => !a.startsWith('-'));
const discovered = docs.length === 0;
if (discovered) {
  for (const root of ['docs', '.claude']) docs.push(...findDocs(path.join(repo, root)).map(relative));
  for (const f of ['CLAUDE.md', 'README.md', 'TASKS.md']) {
    if (fs.existsSync(path.join(repo, f))) docs.push(f);
  }
}

// ⚠ SAY HOW MANY. This tool's clean answer is SILENCE, and silence is also what scanning nothing looks
// like — so the count is the cheapest thing that tells those two apart. Measured while fixing the root
// above: run from `devtools/`, discovery found ONE doc and reported twenty phantom missing identifiers,
// and without a count that run reads like a scan.
if (discovered) {
  console.log(`cite-scan: ${docs.length} doc(s) discovered under docs/, .claude/ and the repo root.`);
  if (docs.length === 0) {
    console.error('cite-scan: NOTHING TO SCAN — the repo layout is not what this expects.');
    process.exitCode = 1;
  }
}

// Build the haystack: every source file the kit ships or tests.
const roots = ['src', 'tests', 'samples', 'devtools'];
// ⚠ Every language the repo builds, or the scan invents findings: `ShenoraActivityAttributes` lives in
// a .swift file and `ShenoraLiveActivityModule` in a .targets file, and both read as "deleted API".
const exts = new Set(['.cs', '.ts', '.tsx', '.mjs', '.js', '.csproj', '.props', '.json',
  '.swift', '.targets', '.plist', '.h', '.cpp', '.cmake', '.yml', '.txt']);
let hay = '';
// ⚠ THE HAYSTACK MUST BE WHAT A CLONE HAS, and this walk read `devtools/_*` until 2026-08-13 — so a type
// surviving only in one machine's throwaway spike answered "yes, that symbol still exists" and the doc
// citing it read as correct. That is this tool's failure direction that COSTS something: a false negative
// is invisible, where a false positive lands in a triage list a human is already reading. Same query as
// the doc walk above, for the opposite reason.
const walk = (dir) => {
  let entries = [];
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (outOfScope(path.relative(repo, full))) continue;
    if (e.isDirectory()) {
      // Another checkout's source is not this one's — it would make a deleted symbol look alive.
      if (fs.existsSync(path.join(full, '.git'))) continue;
      walk(full);
    } else if (exts.has(path.extname(e.name))) {
      // ⚠ The NAME counts as source too. `ShellContracts.cs` declares several interfaces and contains
      // its own basename nowhere, so a doc citing `ShellContracts` read as a deleted type — a false
      // positive that costs exactly as much triage as a true one.
      hay += `${path.basename(e.name, path.extname(e.name))}\n`;
      try { hay += fs.readFileSync(full, 'utf8') + '\n'; } catch { /* unreadable is not a citation */ }
    }
  }
};
for (const r of roots) walk(path.join(repo, r));

// An identifier worth checking: PascalCase or Xxx_with_underscores, long enough not to be prose.
//
// ⚠ TWO capitals required, and that is what keeps this readable. A markdown code span is matched per
// LINE, so a span opened on the previous line pairs wrongly and drags ordinary prose in — one capital
// admitted `Reasoning`, `Expedition` and `Canvas` as "missing APIs". Every real identifier this is for
// has a second hump (`ConvertWith`, `IMediaRenderTarget`, `AsJPEG`); a single-hump English word does not.
// ⚠ An UNDERSCORE lowers the bar to one capital, and that is a whole class rather than a nicety: this
// repo names its tests `Something_happens_when_x`, which carries a single capital and would otherwise be
// filtered out with the prose. A cited test that no longer exists is one of the most misleading claims a
// doc can make — it reads as "this is pinned" — and the first run of this tool found exactly that
// (`ConvertWith_accepts_a_pipeline_so_a_registered_converter_is_consulted`, in D59).
const looksLikeCode = (s) =>
  /^[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9_]+)?$/.test(s) && s.length >= 6 && /[a-z]/.test(s)
  && (/[A-Z].*[A-Z]/.test(s) || s.includes('_'));

/** A repo-relative path worth resolving. `docs/` is excluded — `doc-drift` already gates those. */
const REPO_PATH = /^(src|tests|samples|devtools|\.claude|\.github)\//;

for (const doc of docs) {
  const text = fs.readFileSync(path.join(repo, doc), 'utf8');
  const lines = text.split(/\r?\n/);
  const seen = new Map();   // identifier -> first line number
  lines.forEach((line, i) => {
    for (const m of line.matchAll(/`([^`]+)`/g)) {
      // A span may hold a call or a member path; check each segment.
      for (const part of m[1].split(/[^A-Za-z0-9_]+/)) {
        if (looksLikeCode(part) && !seen.has(part)) seen.set(part, i + 1);
      }
    }
  });

  const missing = [...seen].filter(([id]) => !hay.includes(id)).sort((a, b) => a[1] - b[1]);
  console.log(`\n=== ${doc} — ${seen.size} cited identifier(s), ${missing.length} NOT FOUND in source ===`);
  for (const [id, line] of missing) console.log(`  ${doc}:${line}  ${id}`);

  // ── the same question for PATHS ──────────────────────────────────────────────────────────────────
  // `doc-drift` resolves `docs/` pointers only, so a doc naming `src/…`, `tests/…` or `samples/…` can rot
  // with every gate green. It does: REVIEW-GUIDE.md sent a reviewer to five source directories that D53,
  // D55 and D65 had moved — in the very table that tells them where to look.
  const paths = new Map();
  lines.forEach((line, i) => {
    for (const m of line.matchAll(/`([^`]+)`/g)) {
      for (const token of m[1].split(/[\s,;()<>"']+/)) {
        const clean = token.replace(/[.,:;]+$/, '');
        if (REPO_PATH.test(clean) && !paths.has(clean)) paths.set(clean, i + 1);
      }
    }
  });

  const gone = [...paths]
    // ⚠ A glob is a PATTERN, not a path — `Baselines/*.txt` and `devtools/_*` are correct and would
    // otherwise be reported every run, which is how a noisy tool teaches people to ignore it.
    .filter(([p]) => !p.includes('*') && !p.includes('|') && !p.includes('...'))
    .filter(([p]) => !fs.existsSync(path.join(repo, p)))
    .sort((a, b) => a[1] - b[1]);

  console.log(`--- ${paths.size} repo path(s) cited, ${gone.length} MISSING ---`);
  for (const [p, line] of gone) console.log(`  ${doc}:${line}  ${p}`);
}
