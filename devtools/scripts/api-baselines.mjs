// api-baselines — read the tracked API-surface baselines, at a git ref or in the worktree.
//
// 🔴 WHY THIS EXISTS, and it is the shape this repo keeps paying for. There are TWO baseline
// directories in TWO formats, and every tool that read them by hand read only one:
//
//   Api/Baselines/*.txt          `static class Shenora.AppCallback`   ← carries the type KIND
//   Api/MetadataBaselines/*.txt  `Shenora.Android.ActivityResultRelay` ← a bare FQN, no kind
//
// The second is deliberately kind-less (`MetadataSurface` has no `MetadataLoadContext`, so it cannot
// know), and it is the ONLY record of the whole `Shenora.Android` and `Shenora.iOS` surface — those
// packages have no file under `Baselines/` at all. A reader whose regex demands `class|interface|…`
// therefore returns ZERO types for both mobile packages and says nothing, which is how:
//   - `retired-audit` — the release gate for unrecorded REMOVALS — was blind to a public type leaving
//     either mobile package, the exact failure its own header cites as its reason for existing; and
//   - `namespace-moves` shipped a 154-row migration table with no Android or iOS row in it.
//
// ⚠ Both were fail-OPEN: a smaller answer is indistinguishable from a clean one. So this module exists
// less to save lines than to make "which baselines?" a question answered ONCE, correctly.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

/** Both baseline directories, repo-relative. */
export const BASELINE_DIRS = [
  'tests/Shenora.Tests/Api/Baselines',
  'tests/Shenora.Tests/Api/MetadataBaselines',
];

/**
 * A TYPE line in either format: column 0, and — in the kind-ful format — after the modifiers and the
 * kind keyword. Members are indented in both, which is what makes column 0 the reliable discriminator.
 */
const KINDFUL = /^(?:sealed |abstract |static |readonly |partial |ref )*(?:class|interface|enum|struct|delegate|record(?: struct| class)?) ([A-Za-z0-9_.`+<>]+)/;
const BARE_FQN = /^([A-Za-z0-9_.`+<>]+)$/;

/** The fully-qualified type name a baseline line declares, or null when the line is not a declaration. */
export function typeOnLine(line) {
  if (line.length === 0 || /^\s/.test(line)) return null;   // blank, or a member
  const kindful = KINDFUL.exec(line);
  if (kindful) return kindful[1];
  const bare = BARE_FQN.exec(line.trim());
  // A bare name is only a type when it is namespace-qualified — a stray word is not.
  return bare && bare[1].includes('.') ? bare[1] : null;
}

/** `.txt` baseline paths at a ref (null = the worktree). `.actual` is a drift DUMP and never included. */
export function baselineFilesAt(ref) {
  const files = [];
  for (const dir of BASELINE_DIRS) {
    if (ref === null) {
      const abs = path.join(repo, dir);
      if (!fs.existsSync(abs)) continue;
      for (const f of fs.readdirSync(abs)) if (f.endsWith('.txt')) files.push(`${dir}/${f}`);
      continue;
    }
    const listed = spawnSync('git', ['ls-tree', '-r', '--name-only', ref, '--', `${dir}/`],
      { cwd: repo, encoding: 'utf8' });
    for (const f of (listed.stdout ?? '').split(/\r?\n/)) if (f.endsWith('.txt')) files.push(f);
  }
  return files;
}

/** One baseline file's text at a ref (null = the worktree); '' when it cannot be read. */
export function baselineText(ref, file) {
  if (ref === null) {
    const abs = path.join(repo, file);
    return fs.existsSync(abs) ? fs.readFileSync(abs, 'utf8') : '';
  }
  const show = spawnSync('git', ['show', `${ref}:${file}`],
    { cwd: repo, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return show.status === 0 ? (show.stdout ?? '') : '';
}

/**
 * Every fully-qualified public type name recorded at a ref (null = the worktree), across BOTH
 * baseline directories and both formats.
 */
export function typesAt(ref) {
  const types = new Set();
  for (const file of baselineFilesAt(ref)) {
    for (const line of baselineText(ref, file).split(/\r?\n/)) {
      const type = typeOnLine(line);
      if (type) types.add(type);
    }
  }
  return types;
}

export { repo as repoRoot };
