#!/usr/bin/env node
// Sensitive-info guard — blocks committing dev-machine paths, source brands, sibling-project names,
// or explicit age-restricted wording into this PUBLIC repo (the incident that earned the
// `sensitive-info` rule + a git filter-repo history rewrite). Runs from the pre-commit hook
// (devtools/hooks/pre-commit); also runnable by hand.
//
//   node devtools/scripts/check-sensitive.mjs          # scan STAGED changes (what pre-commit does)
//   node devtools/scripts/check-sensitive.mjs --tree    # scan every tracked file
//
// The tracked patterns here are STRUCTURAL only (generic path shapes) — safe to publish. The real
// sensitive tokens (brand/sibling names, the LAN subnet, R18 descriptors) live in the gitignored
// local/sensitive-patterns.txt, loaded at runtime. Absent that file, the built-ins still run and a
// notice is printed. Exit 1 (blocks the commit) on any match, 0 when clean.

import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const tree = process.argv.includes('--tree');

// Structural, non-secret patterns — a Windows home/dev-root absolute path is always a leak here
// (docs use repo-relative paths or neutral placeholders like D:\Audio\Works instead).
const builtins = [
  { re: /[A-Za-z]:\\Users\\[A-Za-z0-9._-]+/i, why: 'Windows user-home absolute path' },
  { re: /[A-Za-z]:\\Development\\/i, why: 'dev-machine project-root absolute path' },
];

// Private tokens (gitignored). Each non-comment line is a JS regex source.
const patterns = [...builtins];
const localFile = path.join(repo, 'local', 'sensitive-patterns.txt');
if (existsSync(localFile)) {
  for (const raw of readFileSync(localFile, 'utf8').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    try { patterns.push({ re: new RegExp(line, 'i'), why: 'private ban pattern' }); }
    catch { console.error(`check-sensitive: bad regex in local/sensitive-patterns.txt: ${line}`); }
  }
} else {
  console.error('check-sensitive: local/sensitive-patterns.txt missing — running built-ins only.');
}

const git = (args) => execFileSync('git', args, { cwd: repo, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const gitBuf = (args) => execFileSync('git', args, { cwd: repo, maxBuffer: 64 * 1024 * 1024 }); // raw bytes

// Decode text honoring a BOM. A UTF-16 file (PS5 `-Encoding utf8` writes a BOM; Windows tools often
// emit UTF-16LE) has a 0x00 byte in every ASCII char — read as utf8 it's both mojibake AND full of
// NULs, so the old naive read mis-skipped it as "binary" and a leak could ride through. Decode by BOM
// first, THEN let the NUL check catch genuine binaries (whose NULs survive a correct decode).
function decode(buf) {
  if (buf.length >= 2 && buf[0] === 0xFF && buf[1] === 0xFE) return buf.subarray(2).toString('utf16le');
  if (buf.length >= 2 && buf[0] === 0xFE && buf[1] === 0xFF) {   // UTF-16BE → swap to LE, then decode
    let body = buf.subarray(2);
    if (body.length % 2) body = body.subarray(0, body.length - 1); // drop a dangling odd byte
    const le = Buffer.from(body); le.swap16();
    return le.toString('utf16le');
  }
  if (buf.length >= 3 && buf[0] === 0xEF && buf[1] === 0xBB && buf[2] === 0xBF) return buf.subarray(3).toString('utf8');
  return buf.toString('utf8');
}

// Files to scan + a getter for their raw bytes (staged blob vs on-disk).
let files, bufOf;
if (tree) {
  files = git(['ls-files']).split('\n').filter(Boolean);
  bufOf = (f) => { try { return readFileSync(path.join(repo, f)); } catch { return Buffer.alloc(0); } };
} else {
  files = git(['diff', '--cached', '--name-only', '--diff-filter=ACM']).split('\n').filter(Boolean);
  bufOf = (f) => { try { return gitBuf(['show', `:${f}`]); } catch { return Buffer.alloc(0); } };
}

const hits = [];
for (const f of files) {
  const content = decode(bufOf(f));
  if (content.includes('\0')) continue; // genuinely binary (NULs survive a correct decode)
  const lines = content.split('\n');
  for (let i = 0; i < lines.length; i++) {
    for (const { re, why } of patterns) {
      const m = lines[i].match(re);
      if (m) hits.push({ f, line: i + 1, why, snippet: m[0] });
    }
  }
}

if (hits.length === 0) {
  if (tree) console.log('check-sensitive: clean — no dev-machine paths, brands, sibling names, or R18 wording in tracked files.');
  process.exit(0);
}

console.error('\n\x1b[31m✖ check-sensitive: blocked — public-repo leak(s) detected:\x1b[0m');
for (const h of hits) console.error(`  ${h.f}:${h.line}  [${h.why}]  …${h.snippet}…`);
console.error('\nFix: use a repo-relative path / neutral placeholder, or move the value to local/.');
console.error('See .claude/rules/sensitive-info.md. (Override once with: git commit --no-verify)\n');
process.exit(1);
