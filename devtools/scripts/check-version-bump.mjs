#!/usr/bin/env node
// check-version-bump — the version belongs to the RELEASE PIPELINE, not to a session.
//
// WHY THIS EXISTS (earned 2026-08-01, cost one whole version number).
// A session hand-edited `<VersionPrefix>` from 0.1.2 to 0.2.0 and hand-stamped the CHANGELOG's
// `## Unreleased` heading to `## 0.2.0` to match. Both look harmless. Both are the pipeline's job,
// and both broke it:
//
//   1. The release workflow RESOLVES the version — an empty `version` input means "bump from
//      whatever VersionPrefix currently says". The hand-bump silently moved that baseline, so the
//      run bumped 0.2.0 -> 0.3.0 and published 0.3.0. 0.2.0 went from unreleased to SKIPPED without
//      anyone deciding to skip it; the registries read 0.1.2 -> 0.3.0.
//   2. The workflow stamps `## Unreleased` with the resolved version. There was no `## Unreleased`
//      left to stamp, so 0.3.0 shipped with its changelog section titled "0.2.0" — the exact failure
//      docs/RELEASING.md says stamping was automated to prevent.
//
// Neither is caught by anything else, and that is the point of adding this. `doctor` checks version
// CONSISTENCY across props/npm/README/LICENSE — a hand-bump keeps all four consistent, so doctor
// stays green while the baseline drifts underneath the pipeline. Consistency was never the property
// at risk; AUTHORSHIP was.
//
// Runs on pre-commit (staged content only), because that is the one moment the fix is free.
// The pipeline sets SHENORA_RELEASE=1 for its own bump commit. A CI clone has no hooks installed
// (core.hooksPath is local config, never committed), so this cannot block a release either way —
// the env var is belt-and-braces and makes the intent explicit.
//
// Deliberate bypass, for the rare case a human really is hand-fixing a botched release:
//   SHENORA_RELEASE=1 git commit ...      (preferred — says why)
//   git commit --no-verify                (blunt)
//
// Usage: node devtools/scripts/check-version-bump.mjs
import { execFileSync } from 'node:child_process';

const RELEASE_ENV = 'SHENORA_RELEASE';
const PROPS = 'src/Directory.Build.props';
const CHANGELOG = 'CHANGELOG.md';

if (process.env[RELEASE_ENV] === '1') {
  console.log('check-version-bump: skipped (SHENORA_RELEASE=1 — release pipeline).');
  process.exit(0);
}

/** Staged diff for one path, or '' when the path is untouched. */
function stagedDiff(path) {
  try {
    return execFileSync('git', ['diff', '--cached', '-U0', '--', path], { encoding: 'utf8' });
  } catch {
    return '';
  }
}

const problems = [];

// ── 1. VersionPrefix must not move outside a release run ──────────────────────────────────────────
const propsDiff = stagedDiff(PROPS);
const versionLines = propsDiff
  .split(/\r?\n/)
  .filter((line) => /^[+-]/.test(line) && !/^[+-]{3}/.test(line) && /<VersionPrefix>/.test(line));

if (versionLines.length > 0) {
  const from = versionLines.find((l) => l.startsWith('-'))?.match(/<VersionPrefix>(.*?)<\//)?.[1];
  const to = versionLines.find((l) => l.startsWith('+'))?.match(/<VersionPrefix>(.*?)<\//)?.[1];
  problems.push(
    `${PROPS}: <VersionPrefix> changed${from && to ? ` (${from} -> ${to})` : ''}.\n` +
      '    The release workflow resolves the version; an empty `version` input bumps from whatever\n' +
      '    this says. Editing it here moves that baseline and SKIPS a version (0.2.0 was lost this\n' +
      '    way on 2026-08-01). Cut the release from the Actions tab instead — pass an explicit\n' +
      '    `version` if you want a specific number.');
}

// ── 2. The `## Unreleased` heading is the pipeline's to stamp ─────────────────────────────────────
const changelogDiff = stagedDiff(CHANGELOG);
const removedUnreleased = changelogDiff
  .split(/\r?\n/)
  .some((line) => /^-\s*##\s+Unreleased\s*$/i.test(line));

if (removedUnreleased) {
  problems.push(
    `${CHANGELOG}: the "## Unreleased" heading was removed or renamed.\n` +
      '    `dev.mjs changelog --fix --version X.Y.Z` does this during a release, and it was\n' +
      '    automated precisely because a human doing it by hand is how a version ships with the\n' +
      '    wrong section title. Add entries UNDER the heading; leave the heading alone.');
}

if (problems.length > 0) {
  console.error('\ncheck-version-bump: the version is the release pipeline\'s, not a session\'s.\n');
  for (const problem of problems) console.error(`  - ${problem}\n`);
  console.error(
    `  If you really are hand-fixing a release, say so: ${RELEASE_ENV}=1 git commit ...\n` +
      '  See docs/RELEASING.md.\n');
  process.exit(1);
}

console.log('check-version-bump: clean — version untouched by this commit.');
