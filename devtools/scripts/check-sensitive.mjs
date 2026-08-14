#!/usr/bin/env node
// Sensitive-info guard — blocks committing dev-machine paths, source brands, sibling-project names,
// or explicit age-restricted wording into this PUBLIC repo (the incident that earned the
// `sensitive-info` rule + a git filter-repo history rewrite). Runs from the pre-commit hook
// (devtools/hooks/pre-commit); also runnable by hand.
//
//   node devtools/scripts/check-sensitive.mjs          # scan STAGED changes (what pre-commit does)
//   node devtools/scripts/check-sensitive.mjs --tree    # scan every tracked file
//   node devtools/scripts/check-sensitive.mjs --history # ONE-OFF AUDIT of ALL history: every
//                                                      # reachable blob, every path it ever had,
//                                                      # and every commit message
//
// THE DIVISION OF LABOUR, and --history is deliberately NOT part of it:
//   * pre-commit hook (staged) — the ONGOING gate. Every commit, cheap, catches a leak before it
//     ever becomes history. This is the one that actually protects the repo.
//   * commit-msg hook (--message) — the same, for the message.
//   * `dev.mjs verify` (--tree) — the current checkout, on the "am I done?" gate.
//   * --history — an AUDIT you run at moments, not routinely: before making a repo public, and
//     after any filter-repo scrub to prove it worked. Its cost grows with the history (it reads
//     every reachable blob), so wiring it into `verify` or the hook would tax every commit forever
//     to re-check commits that were already checked when they were made. Don't.
//   node devtools/scripts/check-sensitive.mjs --message <file>   # scan a commit message (commit-msg hook)
//   …any mode + --allow-builtins-only                  # opt in to running WITHOUT the private patterns
//
// The tracked patterns here are STRUCTURAL only (generic path shapes) — safe to publish. The real
// sensitive tokens (brand/sibling names, the LAN subnet, R18 descriptors) live in the gitignored
// local/sensitive-patterns.txt, loaded at runtime. Exit 1 (blocks the commit) on any match, 0 clean.
//
// P5.5 H5 closed four holes found by review: (1) a MISSING local/sensitive-patterns.txt used to
// print a notice and continue with only the two built-ins — so on a fresh clone or in CI the
// private-name half of the guard silently did not run. It now FAILS CLOSED; pass
// --allow-builtins-only to opt in deliberately (the release workflow does, since local/ is
// gitignored and unavailable there — the pre-commit hook is the real gate). (2) file PATHS were
// never matched, only content, so a file NAMED after a banned token passed. (3) renamed/copied
// staged files were skipped (--diff-filter=ACM misses `git mv`'s R). (4) commit MESSAGES were not
// scanned at all, though they are history too — hence --message + devtools/hooks/commit-msg.

import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const tree = process.argv.includes('--tree');
const history = process.argv.includes('--history');
// Env var as well as the flag, so CI (where gitignored local/ cannot exist) can opt in once at the
// job level without threading a flag through `dev.mjs verify`.
const allowBuiltinsOnly = process.argv.includes('--allow-builtins-only')
  || process.env.SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY === '1';
const messageIndex = process.argv.indexOf('--message');
const messageFile = messageIndex >= 0 ? process.argv[messageIndex + 1] : null;

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
  let loaded = 0;
  const bad = [];
  for (const raw of readFileSync(localFile, 'utf8').split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    try { patterns.push({ re: new RegExp(line, 'i'), why: 'private ban pattern' }); loaded++; }
    catch { bad.push(line); }
  }

  // A PATTERN THAT DOES NOT COMPILE PROTECTS NOTHING, and the author believes it does. This used to
  // log and carry on, which is the worse half of the same fail-open family as the empty file below:
  // partial, permanent, and invisible after the first scroll of output. The file is the author's own
  // (gitignored), so failing means "fix your line", which is the only useful outcome.
  if (bad.length > 0 && !allowBuiltinsOnly) {
    console.error('\n\x1b[31m✖ check-sensitive: local/sensitive-patterns.txt has ' +
      `${bad.length} line(s) that are not valid regexes.\x1b[0m`);
    for (const line of bad) console.error(`    ${line}`);
    console.error('  Each of those bans NOTHING while looking like it does. Fix or remove them.\n');
    process.exit(1);
  }

  // THE OTHER WAY TO GET A HALF-SCAN, and it was open: the fail-closed branch below only covers a
  // MISSING file, so a file that exists and yields no patterns — truncated by a crashed editor or a
  // mangled redirect (this repo has already had a file created that way), or created and not yet
  // filled in — degraded to the two builtins and reported CLEAN with no message at all. Same
  // reasoning, same verdict. Found by asking the general question the media incident raised: which
  // gates are satisfied by the PRESENCE of a file rather than its CONTENT?
  if (loaded === 0 && allowBuiltinsOnly) {
    // The SAME notice the missing-file branch prints. Without it, an existing-but-empty file under the
    // CI opt-in ran degraded and said only "clean" — the announcement was attached to one way of
    // getting here rather than to the fact of being here.
    console.error('check-sensitive: local/sensitive-patterns.txt has no patterns — running built-ins ONLY '
      + '(explicitly allowed).');
  }
  if (loaded === 0 && !allowBuiltinsOnly) {
    console.error('\n\x1b[31m✖ check-sensitive: local/sensitive-patterns.txt has NO patterns.\x1b[0m');
    console.error('  The file exists but every line is blank or a comment, so only the two structural');
    console.error('  path patterns ran — the brand/sibling-name half of the guard did NOT. A file that');
    console.error('  exists is not the property this guard needs; patterns in it are.');
    console.error('  Fix: restore its contents (see .claude/rules/sensitive-info.md),');
    console.error('  or pass --allow-builtins-only if a builtins-only scan is genuinely what you want.\n');
    process.exit(1);
  }
} else if (allowBuiltinsOnly) {
  console.error('check-sensitive: local/sensitive-patterns.txt missing — running built-ins ONLY (explicitly allowed).');
} else {
  // FAIL CLOSED. Silently degrading to two structural patterns is indistinguishable from "clean",
  // which is exactly how the private-name half of this guard never ran in CI.
  console.error('\n\x1b[31m✖ check-sensitive: local/sensitive-patterns.txt is MISSING.\x1b[0m');
  console.error('  Without it only the two structural path patterns run — the brand/sibling-name');
  console.error('  half of the guard does NOT. Refusing to report a half-scan as clean.');
  console.error('  Fix: restore local/sensitive-patterns.txt (see .claude/rules/sensitive-info.md),');
  console.error('  or pass --allow-builtins-only if a builtins-only scan is genuinely what you want.');
  // 🔴 NAME THE SITUATION, or the reader invents a worse fix. `local/` is gitignored, so it CANNOT
  // exist in a fresh clone, in CI, or in an agent worktree — and this message used to describe only a
  // corrupted primary checkout. A subagent hitting it on 2026-08-10 concluded the file was missing and
  // COPIED IT IN from the main checkout to get a green run: a reasonable inference from what it was
  // told, and the one outcome this repo's privacy rule most wants to prevent. Telling it which world
  // it is in costs three lines and removes the incentive.
  console.error('');
  console.error('  ⚠ In a WORKTREE, a fresh clone or CI? Then this is EXPECTED — `local/` is gitignored');
  console.error('    and cannot be there. Use --allow-builtins-only (or');
  console.error('    SHENORA_ALLOW_BUILTIN_PATTERNS_ONLY=1). Do NOT copy local/ across checkouts:');
  console.error('    duplicating private context is the leak this guard exists to prevent.\n');
  process.exit(1);
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

// Files to scan + a getter for their raw bytes (staged blob vs on-disk vs a commit message).
let files, bufOf;
if (history) {
  // HISTORY MODE — the one the rule has always demanded and no scanner could do. Both this rule and
  // the sibling's say "a leak already committed is a HISTORY problem, not a working-tree problem",
  // and then offered only --tree, which reads the CURRENT checkout. Editing the file away leaves the
  // value in every past commit, and on a public repo those are all fetchable. This scans every blob
  // reachable from every ref, every PATH those blobs ever had, and every commit MESSAGE.
  const objects = git(['rev-list', '--objects', '--all']).split('\n').filter(Boolean);
  const pathOf = new Map();
  for (const line of objects) {
    const sp = line.indexOf(' ');
    if (sp > 0) pathOf.set(line.slice(0, sp), line.slice(sp + 1));
  }

  // One `cat-file --batch-check` for the whole set: per-object spawns would be ~1500 processes.
  const shas = [...pathOf.keys()];
  const check = execFileSync('git', ['cat-file', '--batch-check'],
    { cwd: repo, input: shas.join('\n'), encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  const blobs = [];
  for (const line of check.split('\n')) {
    const [sha, type, size] = line.split(' ');
    // 4 MB cap: anything larger in this repo is an image or a package, and the NUL check below
    // would drop it anyway — this just avoids buffering it.
    if (type === 'blob' && Number(size) <= 4 * 1024 * 1024) blobs.push(sha);
  }

  // One `cat-file --batch` likewise. Output per object: "<sha> blob <size>\n" + <size> bytes + "\n".
  const contents = new Map();
  if (blobs.length) {
    const out = execFileSync('git', ['cat-file', '--batch'],
      { cwd: repo, input: blobs.join('\n'), maxBuffer: 512 * 1024 * 1024 });
    let at = 0;
    while (at < out.length) {
      const nl = out.indexOf(0x0A, at);
      if (nl < 0) break;
      const [sha, type, size] = out.subarray(at, nl).toString('utf8').split(' ');
      const len = Number(size);
      if (type !== 'blob' || !Number.isFinite(len)) break;
      contents.set(sha, out.subarray(nl + 1, nl + 1 + len));
      at = nl + 1 + len + 1;   // payload + the trailing newline git appends
    }
  }

  // Commit messages are history too — NUL-delimited so a message body can contain anything.
  const messages = git(['log', '--all', '--format=%H%x00%B%x00']).split('\0');
  const buffers = new Map();
  files = [];
  for (const sha of blobs) {
    const label = `${pathOf.get(sha) || '(no path)'}  @${sha.slice(0, 8)}`;
    files.push(label);
    buffers.set(label, contents.get(sha) ?? Buffer.alloc(0));
  }
  for (let i = 0; i + 1 < messages.length; i += 2) {
    const sha = messages[i].trim();
    if (!sha) continue;
    const label = `commit-message ${sha.slice(0, 8)}`;
    files.push(label);
    buffers.set(label, Buffer.from(messages[i + 1], 'utf8'));
  }
  bufOf = (f) => buffers.get(f) ?? Buffer.alloc(0);
} else if (messageFile) {
  files = [messageFile];
  bufOf = () => { try { return readFileSync(path.isAbsolute(messageFile) ? messageFile : path.join(repo, messageFile)); } catch { return Buffer.alloc(0); } };
} else if (tree) {
  files = git(['ls-files']).split('\n').filter(Boolean);
  bufOf = (f) => { try { return readFileSync(path.join(repo, f)); } catch { return Buffer.alloc(0); } };
} else {
  // ACMRC, not ACM: a `git mv` of a leaking file stages as R (rename) and a copy as C, both of
  // which the old filter skipped entirely.
  files = git(['diff', '--cached', '--name-only', '--diff-filter=ACMRC']).split('\n').filter(Boolean);
  bufOf = (f) => { try { return gitBuf(['show', `:${f}`]); } catch { return Buffer.alloc(0); } };
}

const hits = [];
for (const f of files) {
  // The PATH is scanned too — a file merely NAMED after a banned token used to pass, because only
  // content was ever matched. Skipped for --message (the "path" is a temp file git chose).
  if (!messageFile) {
    for (const { re, why } of patterns) {
      const m = f.match(re);
      if (m) hits.push({ f, line: 0, why: `${why} (in the FILE PATH)`, snippet: m[0] });
    }
  }

  const content = decode(bufOf(f));
  if (content.includes('\0')) continue; // genuinely binary (NULs survive a correct decode)
  const lines = content.split('\n');
  for (let i = 0; i < lines.length; i++) {
    // Ignore git's own commentary in a commit-message file — it is stripped before the commit and
    // legitimately contains branch/path chatter.
    if (messageFile && lines[i].startsWith('#')) continue;
    for (const { re, why } of patterns) {
      const m = lines[i].match(re);
      if (m) hits.push({ f, line: i + 1, why, snippet: m[0] });
    }
  }
}

if (hits.length === 0) {
  if (history) {
    console.log(`check-sensitive: HISTORY clean — scanned ${files.length} blobs + commit messages `
      + 'across every ref. No dev-machine paths, brands, sibling names, or R18 wording.');
  }
  else if (tree) console.log('check-sensitive: clean — no dev-machine paths, brands, sibling names, or R18 wording in tracked files.');
  else if (messageFile) console.log('check-sensitive: commit message clean.');
  process.exit(0);
}

console.error('\n\x1b[31m✖ check-sensitive: blocked — public-repo leak(s) detected:\x1b[0m');
for (const h of hits) console.error(`  ${h.f}:${h.line}  [${h.why}]  …${h.snippet}…`);
console.error('\nFix: use a repo-relative path / neutral placeholder, or move the value to local/.');
console.error('See .claude/rules/sensitive-info.md. (Override once with: git commit --no-verify)\n');
process.exit(1);
