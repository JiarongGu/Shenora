#!/usr/bin/env node
// reserved-paths — refuse a path Windows cannot check out.
//
// WHY THIS IS A GATE AND NOT A RULE: it is created by accident, not by a decision. A `> nul` or
// `2>nul` redirect written in Git Bash does NOT reach a null device — that spelling is cmd's, so the
// shell creates a real file called `nul` in the repo. It then sits in `git status` as an ordinary
// untracked file and a `git add -A` stages it without comment.
//
// The cost is paid by everyone else, later: a Windows reserved name (or a segment ending in a dot or
// space) makes `git checkout` fail for every future clone on Windows, and the file is awkward to
// delete even deliberately — `Remove-Item -LiteralPath "\\?\…\nul"` reports success and removes
// nothing, and `Test-Path` answers false whether or not it is there (see windows-dev-gotchas).
//
// Scope is deliberately ONE thing. It checks names, never content.
//
// Usage: node devtools/scripts/reserved-paths.mjs

import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const git = (args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });

// The MS-DOS device names, still reserved by Win32 in every modern Windows. A name is reserved
// whatever its extension: `nul`, `NUL.cs` and `Nul.txt` all fail to open.
const DEVICES = new Set([
  'CON', 'PRN', 'AUX', 'NUL',
  ...Array.from({ length: 9 }, (_, i) => `COM${i + 1}`),
  ...Array.from({ length: 9 }, (_, i) => `LPT${i + 1}`),
]);

function problemWith(segment) {
  // The device check is on the stem, so `com1.txt` is reserved but `com.example.json` is not — the
  // quiet direction that matters, since `com`/`console`/`auxiliary` are ordinary words.
  const stem = segment.split('.')[0].toUpperCase();
  if (DEVICES.has(stem)) return `'${segment}' is a Windows reserved DEVICE name`;
  // Windows silently strips these, so the checked-out name never matches the committed one.
  if (/[ .]$/.test(segment)) return `'${segment}' ends with a space or dot, which Windows strips`;
  return null;
}

// Tracked files, plus untracked ones git would actually stage. Ignored files are none of our business.
const tracked = git(['ls-files', '-z']).split('\0').filter(Boolean);
const untracked = git(['ls-files', '-z', '--others', '--exclude-standard']).split('\0').filter(Boolean);

const hits = [];
for (const [file, staged] of [...tracked.map(f => [f, true]), ...untracked.map(f => [f, false])]) {
  for (const segment of file.split('/')) {
    const why = problemWith(segment);
    if (why) { hits.push({ file, why, staged }); break; }
  }
}

if (hits.length === 0) {
  console.log(`reserved-paths: ok — ${tracked.length} tracked + ${untracked.length} untracked path(s), no Windows-hostile name.`);
  process.exit(0);
}

console.error('\n\x1b[31m✖ reserved-paths: a path here breaks `git checkout` on Windows:\x1b[0m');
for (const h of hits) console.error(`  ${h.file}   [${h.why}]${h.staged ? '  — TRACKED, so it is already in history' : ''}`);
console.error('\nA stray `nul` is usually a `> nul` redirect written in Git Bash — use /dev/null.');
console.error('Delete it with:  [System.IO.File]::Delete("\\\\?\\<abs-path>")   and verify from bash (ls ./nul);');
console.error('Remove-Item and Test-Path BOTH lie about reserved names. See .claude/rules/windows-dev-gotchas.md\n');
process.exit(1);
