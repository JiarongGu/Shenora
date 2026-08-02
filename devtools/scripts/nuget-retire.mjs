#!/usr/bin/env node
// nuget-retire.mjs — unlist EVERY version of a package id that has been renamed away.
//
// Ported from the sibling Lyntai's `nuget-unlist.mjs`, with the cutoff semantics swapped: that one
// hides versions BELOW a line while the package lives on, this one retires whole IDS that no longer
// exist in the tree. Same underlying call, same reversibility, same auth story.
//
//   node devtools/dev.mjs nuget-retire                        # DRY RUN — prints what would happen
//   node devtools/dev.mjs nuget-retire --apply --api-key KEY  # actually unlist
//   node devtools/dev.mjs nuget-retire --only Shenora.WinForms
//
// `dotnet nuget delete` is misleadingly named: on nuget.org it UNLISTS (hides from search and the
// version dropdown) and never deletes. Restore-by-exact-version keeps working, so nobody's build
// breaks, and it is reversible from the package's Manage page. Versions are immutable either way — an
// unlisted version number can never be re-published.
//
// ⚠ UNLISTING IS ONLY HALF THE JOB, AND IT IS THE QUIETER HALF. Unlisting hides a package; it does
// not tell anyone where the code went. DEPRECATION is what surfaces "use Shenora.Windows instead" in
// a consumer's IDE and on the listing page — and it is NOT scriptable (no public API; it is web-UI
// only, per package, on the Manage page). This tool prints the exact text to paste. Do the
// deprecation FIRST: it is the part consumers actually see.
//
//   Auth: `--api-key <key>`, or NUGET_API_KEY in the environment if the flag is absent. Mint it at
//         nuget.org -> Account -> API Keys, scope "Unlist" (NOT push — this key cannot publish),
//         glob `Shenora.*`.
//
//         ⚠ A key passed as an argument lands in your SHELL HISTORY and in the process list. That is
//         the accepted trade for the flag being the convenient path (owner's call, 2026-08-02); the
//         env var stays supported for anyone who would rather it did not. Either way: use a
//         short-lived Unlist-scoped key and revoke it when the retirement is done — the key's SCOPE
//         is what actually bounds the damage, not where it was typed.
//
//         The key is redacted from this tool's own output. `execFile`'s error MESSAGE embeds the full
//         command line, and stderr is often empty on failure, so an unredacted failure path would
//         have printed the key to the console on the very run most likely to be pasted into a chat.
//
// Idempotent: queries nuget.org for what is currently LISTED and skips the rest, so a re-run after a
// partial failure only does the remainder.

import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const run = promisify(execFile);
const SOURCE = 'https://api.nuget.org/v3/index.json';

/**
 * Ids retired from the tree, and what replaced each. Published versions still exist on nuget.org —
 * that is the whole reason this file exists. See docs/DECISIONS.md D37.
 */
const RETIRED = [
  { id: 'Shenora.WinForms', replacement: 'Shenora.Windows' },
  { id: 'Shenora.WebView2', replacement: 'Shenora.Windows' },
  { id: 'Shenora.WebView2.Sessions', replacement: 'Shenora.Windows' },
];

const args = process.argv.slice(2);
const apply = args.includes('--apply');
const only = valueOf('--only');

function valueOf(flag) {
  const i = args.indexOf(flag);
  return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : null;
}

const cmp = (a, b) => {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) - (pb[i] || 0);
  return 0;
};

/** Currently LISTED versions, from the registration index (unlisted ones carry listed:false). */
async function listedVersions(id) {
  const url = `https://api.nuget.org/v3/registration5-gz-semver2/${id.toLowerCase()}/index.json`;
  const res = await fetch(url);
  if (res.status === 404) return null; // never published
  if (!res.ok) throw new Error(`${id}: registration fetch failed (HTTP ${res.status})`);
  const doc = await res.json();
  const out = [];
  for (const page of doc.items ?? [])
    for (const item of page.items ?? []) {
      const c = item.catalogEntry;
      if (c.listed !== false) out.push(c.version);
    }
  return out.sort(cmp);
}

// ---- The ordering guard, and the reason this is not just a loop.
//
// Retiring the old ids BEFORE the replacement is published leaves a window where neither is
// discoverable: the old package is hidden from search and the new one does not exist yet. Anyone
// arriving in that window finds nothing at all and concludes the project is gone. So refuse, rather
// than trusting whoever runs this to remember the order.
async function replacementIsPublished(id) {
  const versions = await listedVersions(id);
  return versions !== null && versions.length > 0;
}

// `process.exitCode` + return, never `process.exit()`. Calling exit() while a fetch is still in
// flight aborts Node with `Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)` from libuv — and
// the crash REPLACES the exit code, so the refusal below reported SUCCESS. Hit on the first run of
// this file: the guard printed its message and returned 0.
const fail = (...lines) => { lines.forEach((l) => console.error(l)); process.exitCode = 1; };

// Flag first, environment second — so the convenient path is explicit and the env var keeps working
// for anyone who prefers it.
const key = valueOf('--api-key') ?? process.env.NUGET_API_KEY;

/** Never let the key reach the console, whatever went wrong. See the auth note in the header. */
const redact = (text) => {
  const s = String(text ?? '');
  return key ? s.split(key).join('***REDACTED***') : s;
};

if (apply && !key) {
  fail('No API key. Pass --api-key <key>, or set NUGET_API_KEY in the environment.',
       'Mint an Unlist-scoped key at nuget.org -> Account -> API Keys, glob `Shenora.*`.');
}

const targets = process.exitCode ? [] :
  (only ? RETIRED.filter((r) => r.id.toLowerCase() === only.toLowerCase()) : RETIRED);
if (!process.exitCode && targets.length === 0) {
  fail(`--only ${only}: not a retired package. Known: ${RETIRED.map((r) => r.id).join(', ')}`);
}

if (!process.exitCode) {
  const replacements = [...new Set(targets.map((r) => r.replacement))];
  for (const replacement of replacements) {
    if (!(await replacementIsPublished(replacement))) {
      fail(`REFUSING: ${replacement} is not published yet.\n`,
           'Unlisting the old ids first would leave a window where NEITHER the old package (hidden)',
           'nor the new one (absent) can be found. Cut the release that publishes the replacement,',
           'then run this.');
      break;
    }
  }
  if (!process.exitCode) console.log(`replacement(s) published: ${replacements.join(', ')} — safe to retire the old ids\n`);
}

if (!process.exitCode) console.log(`${apply ? 'UNLISTING' : 'DRY RUN — nothing will change'}\n`);

let planned = 0, done = 0, failed = 0;
const needDeprecation = [];

for (const { id, replacement } of (process.exitCode ? [] : targets)) {
  let listed;
  try {
    listed = await listedVersions(id);
  } catch (err) {
    console.log(`${id}\n  ! ${err.message}\n`);
    failed++;
    continue;
  }
  if (listed === null) { console.log(`${id}\n  - not published, skipping\n`); continue; }
  if (listed.length === 0) { console.log(`${id}\n  - already fully unlisted\n`); continue; }

  planned += listed.length;
  needDeprecation.push({ id, replacement });
  console.log(`${id}  -> ${replacement}`);
  console.log(`  unlist ALL ${listed.length} listed version(s): ${listed.join(', ')}`);

  if (!apply) { console.log(''); continue; }

  for (const version of listed) {
    try {
      await run('dotnet', ['nuget', 'delete', id, version,
        '--source', SOURCE, '--api-key', key, '--non-interactive']);
      done++;
      process.stdout.write(`  ✓ ${version}\n`);
    } catch (err) {
      failed++;
      // REDACTED, and `err.message` is exactly why: execFile embeds the whole command line in it —
      // `--api-key <key>` included — and stderr is frequently empty when dotnet fails, so the
      // fallback is the branch that would have leaked.
      process.stdout.write(`  ✗ ${version} — ${redact(err.stderr || err.message).trim().split('\n')[0]}\n`);
    }
  }
  console.log('');
}

if (!process.exitCode) console.log(apply
  ? `Done. ${done} unlisted, ${failed} failed.`
  : `Planned: ${planned} version(s) would be unlisted. Re-run with --apply to do it.`);

if (needDeprecation.length) {
  console.log('\n' + '─'.repeat(78));
  console.log('DEPRECATION — the half that is NOT scriptable, and the half consumers actually see.');
  console.log('For each package: nuget.org -> the package -> Manage -> Deprecation.');
  console.log('Select ALL versions, tick "Other", set the alternate package, paste the message.\n');
  for (const { id, replacement } of needDeprecation) {
    console.log(`  ${id}`);
    console.log(`    alternate package : ${replacement}`);
    console.log(`    message           : Renamed. ${id} is now part of ${replacement}, which is one`);
    console.log(`                        package per platform. Every type keeps its name and signature —`);
    console.log(`                        replace the package reference and the namespace. See the`);
    console.log(`                        CHANGELOG for the mapping.`);
    console.log('');
  }
  console.log('─'.repeat(78));
}

if (failed) process.exitCode = 1;
