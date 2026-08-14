// git-scope — "would a CLONE have this path?", asked of GIT instead of a list of directory names.
//
// 🔴 WHY THIS EXISTS. Every prose scanner here needs the same answer: a file that exists only on this
// machine is not prose a reader can reach, so scanning it makes the tool's verdict depend on which
// working junk happened to be lying around (measured twice — 15 of one doc-drift run's 34 hits came
// from `devtools/_p6-consumer/` and `_p7-profiles/`; the day `.superpowers/` appeared, doc-drift
// reported 8 stale claims in planning notes that exist in no clone, no CI run and no adopter's hands).
// Each scanner grew its own hand-maintained deny-list for that, and each list grew by accretion:
// `node_modules`, `bin`, `obj`, `dist`, `publish`, `local`, `devtools/_*`, then `.superpowers` on
// 2026-08-12. doc-drift's own comment about `isNestedCheckout` had already written the verdict on that
// shape — a name list "lists directories somebody thought of", and it is complete right up until a new
// KIND of directory appears — and the very next scratch directory was still added BY NAME.
//
// THE QUERY, once per process, whole repo:
//
//     git ls-files -z --others --ignored --exclude-standard --directory
//
// = every UNTRACKED path git is ignoring, with a wholly-ignored directory collapsed to a single entry
// (`local/`, `.superpowers/`, `devtools/_dpi-probe/`, `src/Shenora/obj/`). One process, not one call
// per file, and it prunes the walk at the directory rather than after descending into `node_modules`.
//
// 🔴 `--others` IS THE LOAD-BEARING FLAG, because it means UNTRACKED: a TRACKED file can never appear
// in this set however many ignore patterns match it. That is precisely the hole doc-drift records in
// its own history — an earlier attempt skipped every `_`-prefixed entry ANYWHERE, so a `docs/_x.md` or
// `src/_y.cs` silently vanished from all four checks and a gate that must FAIL went quiet. **A skip
// rule wider than the ignore rule it mirrors is a gate hole**, and this one cannot be wider, because
// git's rule IS the rule. Verified 2026-08-13 in a throwaway repo: with `_*` ignored,
// `git add -f docs/_tracked.md` leaves that file OUT of this listing while its untracked twin is in it.
//
// ⚠ `--exclude-standard` also honours `.git/info/exclude` and the user's global excludesFile, so the
// set can be wider than this repo's `.gitignore` on one machine (here, a global rule hides
// `.claude/settings.local.json`). THE TWO QUERIES BELOW ANSWER THAT DIFFERENTLY, and the difference is
// worth knowing before someone "hardens" one of them:
//   - THE WALK is airtight. Whatever a global rule says, `--others` excludes tracked files, so no
//     tracked prose can leave the scan. Measured 2026-08-13 with a hostile global excludesFile of
//     `*.md` + `*.cs`: the walk still found all 450 files, because every one of them is tracked.
//   - THE CITATION QUERY is not, and cannot be: `check-ignore` matches PATTERNS, and it is asked about
//     paths that need not exist (a historical name, a placeholder, a relative link). Under that same
//     hostile global ignore, doc-drift produced 25 false positives — e.g. `docs/ADOPTION.md:35` citing
//     `guides/missions.md`. LOUD, so not a hole, and a rare enough configuration to be worth naming
//     rather than defending against. ⚠ The defence NOT to reach for is `--no-index`: it would make
//     check-ignore answer for tracked paths too, destroying the tracked-file guarantee to fix noise.
//
// ⚠ `local/` is in this set for a DIFFERENT reason from scratch, and the difference must survive: it is
// private AND an informal ARCHIVE (`persist-working-state.md`) — it records what was true THEN, so a
// sweep through it destroys the reference rather than updating it. Nothing here changes what `local/`
// means; it simply stops being a name this file has to remember.
//
// FAIL-SAFE DIRECTION. If git is missing, the query errors, or this is not a checkout, the answer is
// NOT "skip nothing quietly" and NOT "skip everything" — it is ONE explicit list for all four scanners,
// plus a warning on stderr, so the operator is told which rule is in force. ⚠ It is not literally what
// each scanner skipped before: it is the UNION plus `out`, so a scanner whose own list was shorter (old
// cite-scan knew only `local|node_modules|bin|obj|.git`) skips marginally MORE on this path than it once
// did. Reachable only when git cannot answer at all, and the alternative — four divergent fallbacks —
// is the thing this module exists to end. (Scanning literally everything was the other candidate;
// it would drag `node_modules/` and every `obj/` into a prose scan, which is how a tool becomes one
// nobody runs.)
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// The script's own location, like every other tool here — `process.cwd()` would make the answer depend
// on where the caller was standing, which is the defect `doctor` now gates for in devtools/scripts/.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

// The pre-git deny-list, kept ONLY as the fallback for a tree git cannot answer for. Deliberately NOT
// stale-scan's old `startsWith('_')` rule, which matched anywhere in the tree: the fallback must not
// reintroduce the hole this module exists to close, and skipping less is always the safe direction.
const FALLBACK_DIR = /^(node_modules|bin|obj|dist|out|publish|local|\.superpowers)$/;
const FALLBACK_PATH = /^devtools\/_/;

let cached = null;      // { viaGit, entries: Set<string> } — one spawn per process, several walks per run.

/** Repo-relative, forward slashes, no trailing slash — the one spelling everything here compares in. */
function normalize(p) {
  return p.replace(/\\/g, '/').replace(/\/+$/, '');
}

function load() {
  if (cached) return cached;
  const res = spawnSync(
    'git', ['ls-files', '-z', '--others', '--ignored', '--exclude-standard', '--directory'],
    // 64 MB: a checkout with several node_modules trees still collapses to a few dozen entries, but a
    // truncated read would silently mean "not ignored", i.e. scan MORE — safe, and loud enough to notice.
    { cwd: repo, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (res.error || res.status !== 0) {
    const why = res.error ? res.error.message : (res.stderr || '').trim().split('\n')[0] || `exit ${res.status}`;
    console.error(`  !!  git-scope: could not ask git which paths are ignored (${why}).\n`
      + '      Falling back to the built-in name list (the union of what these scanners skipped before,\n'
      + '      plus `out`), so this scans MORE than the git answer would, never less.');
    cached = { viaGit: false, entries: new Set() };
  } else {
    cached = { viaGit: true, entries: new Set(res.stdout.split('\0').filter(Boolean).map(normalize)) };
  }
  return cached;
}

/** True when git answered; false when the fallback list is in force. For a scanner's own diagnostics. */
export function scopeViaGit() {
  return load().viaGit;
}

/**
 * Is this repo-relative path absent from every clone — i.e. untracked AND ignored?
 *
 * Answers for a path INSIDE a listed directory too (`devtools/_relay/x.md` under `devtools/_relay/`),
 * so a caller that did not prune its walk still gets the right answer.
 */
export function outOfScope(relPath) {
  const rel = normalize(relPath);
  if (rel === '') return false;
  const segments = rel.split('/');
  // `.git` is never reported by git as ignored — it is not "in" the working tree at all — so it stays
  // an explicit case. (A nested checkout's `.git` is a FILE, not a directory; `isNestedCheckout` in the
  // callers is a separate rule and still necessary, because a worktree is not gitignored.)
  if (segments[0] === '.git') return true;

  const { viaGit, entries } = load();
  if (!viaGit) {
    return segments.some((s) => FALLBACK_DIR.test(s)) || FALLBACK_PATH.test(rel);
  }
  for (let i = 1; i <= segments.length; i++) {
    if (entries.has(segments.slice(0, i).join('/'))) return true;
  }
  return false;
}

/**
 * Which of these CITED paths would be absent from a clone?
 *
 * A separate query from the walk, and it has to be: a citation names a path that need not exist here
 * any more — four of the six `devtools/_*` paths cited across this repo were already gone on the very
 * machine that made them — so a listing of what is on disk cannot answer it. `git check-ignore` matches
 * PATTERNS, so it answers for a path that never existed, and (without `--no-index`) it also answers NO
 * for a TRACKED path, which is the same tracked-files-are-always-in-scope guarantee as above.
 *
 * ⚠ ONE BAD PATH ABORTS THE WHOLE BATCH. Measured 2026-08-13: a token scraped from prose as
 * `bin/../../escape.txt` made check-ignore exit 128 after printing a partial answer — which would have
 * read as "everything after this is fine". Candidates are filtered to plain repo-relative shapes first,
 * and any exit code other than 0 (some ignored) or 1 (none ignored) returns null rather than a guess.
 *
 * 🔴 EVERY CANDIDATE IS ASKED TWICE — bare AND with a trailing slash — because git's answer DEPENDS ON
 * THE SLASH for a directory that is not on disk. Measured on this repo: `.gitignore` carries the
 * directory-only rule `.claude/worktrees/`, and with no such directory present, git calls
 * `.claude/worktrees/` ignored and `.claude/worktrees` NOT ignored. A caller strips a token's trailing
 * punctuation before asking (a sentence's full stop is part of the scrape), so it necessarily asks the
 * bare form — and would have been told "in scope" about the one directory whose whole point is that a
 * clone does not have it. Asking both cannot go the other way: appending a slash can only newly match a
 * DIRECTORY-only rule, and a rule of that shape matching a cited name means the citation was about that
 * ignored directory. The returned set is normalised bare, so a caller still matches on its own token.
 *
 * @returns {Set<string>|null} the ignored subset, or null when git could not answer — the caller must
 *   then fall back rather than treat an empty answer as "nothing is out of scope".
 */
export function citedOutOfScope(paths) {
  const safe = [...new Set(paths)]
    .map((p) => p.replace(/\\/g, '/'))
    .filter((p) => /^[A-Za-z0-9._@-]+(?:\/[A-Za-z0-9._@-]+)*\/?$/.test(p))
    // `..` walks OUT of the repo (git aborts on that, above); a bare `.` segment is prose punctuation
    // that happened to sit next to a slash, and answering for it means nothing either way.
    .filter((p) => !p.split('/').some((s) => s === '..' || s === '.'));
  if (safe.length === 0) return new Set();

  // Both spellings of each candidate — see the slash note above.
  const probes = [...new Set(safe.flatMap((p) => (p.endsWith('/') ? [p] : [p, `${p}/`])))];

  const res = spawnSync('git', ['check-ignore', '--stdin', '-z'],
    { cwd: repo, encoding: 'utf8', input: probes.join('\0'), maxBuffer: 64 * 1024 * 1024 });
  if (res.error || (res.status !== 0 && res.status !== 1)) return null;
  return new Set(res.stdout.split('\0').filter(Boolean).map((p) => p.replace(/\/+$/, '')));
}

// The root everything above is relative to, exported because a caller resolving the same paths must
// resolve them against the SAME root or the two answers silently disagree.
export { repo as repoRoot };
