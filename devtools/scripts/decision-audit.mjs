// decision-audit — per-ENTRY truth check for docs/DECISIONS.md. A review tool, never a gate.
//
// 🔴 WHY IT IS NOT cite-scan. cite-scan answers "does this doc cite a dead identifier?" and reports
// `docs/DECISIONS.md:166`. That is the wrong unit: the thing a session trusts is an ENTRY, and an
// entry is what gets rewritten. This attributes every failing claim to its `D<n>`, so the output is a
// RANKED WORKLIST of which decisions to re-verify first — the repo's own "rank, don't read".
//
// It also checks three claim kinds cite-scan does not, and they are the three DECISIONS.md keeps
// getting wrong (2026-08-14):
//   * PACKAGE ids      — D2 named a set of four that has not existed since D37/D53/D55.
//   * NAMESPACES       — the header asserted four "still live NAMESPACES", none of which existed;
//                        that claim was load-bearing, because it was the stated reason those names
//                        were kept out of retired-names.txt, so a gate stayed off on an expired fact.
//   * CITED-FROM-SOURCE — whether any `D<n>` is an address code actually uses. An entry nothing cites
//                        is free to renumber or fold; one that ships in an XML doc on nuget.org is not.
//
// ⚠ IT ANSWERS "IS IT TRUE?" AND NOTHING ELSE. The second question — is the decision still REASONABLE
// for what the kit is now (D54's thesis, D53's identity, whether its PREMISE still exists) — is a
// judgement no script makes. A green row here means "the prose matches the tree", not "keep it".
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { outOfScope } from './git-scope.mjs';
import { DECISIONS_REL, decisionLines, decisionMarks, sectionAfter } from './decisions-parse.mjs';

// The script's own location, never process.cwd() — `doctor` fails any scanner that roots at the cwd,
// because a partial scan is indistinguishable from a clean one when the clean answer is silence.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

// ── the haystack: every source file a CLONE has ───────────────────────────────────────────────────
const exts = new Set(['.cs', '.ts', '.tsx', '.mjs', '.js', '.csproj', '.props', '.json',
  '.swift', '.targets', '.plist', '.h', '.cpp', '.cmake', '.yml', '.txt']);
let hay = '';
const namespaces = new Set();
const sourceFiles = [];
const walk = (dir) => {
  let entries = [];
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
  for (const e of entries) {
    const full = path.join(dir, e.name);
    if (outOfScope(path.relative(repo, full))) continue;
    if (e.isDirectory()) {
      if (fs.existsSync(path.join(full, '.git'))) continue;   // another checkout is not this one
      walk(full);
    } else if (exts.has(path.extname(e.name))) {
      sourceFiles.push(full);
      hay += `${path.basename(e.name, path.extname(e.name))}\n`;  // the NAME is source too
      let text = '';
      try { text = fs.readFileSync(full, 'utf8'); } catch { continue; }
      hay += text + '\n';
      for (const m of text.matchAll(/^\s*namespace\s+([A-Za-z0-9_.]+)/gm)) namespaces.add(m[1]);
    }
  }
};
for (const r of ['src', 'tests', 'samples', 'devtools']) walk(path.join(repo, r));

// ── what the tree actually ships ──────────────────────────────────────────────────────────────────
const csprojs = new Map();      // assembly/package id -> { packable, rel }
for (const f of sourceFiles.filter((p) => p.endsWith('.csproj'))) {
  const id = path.basename(f, '.csproj');
  const text = fs.readFileSync(f, 'utf8');
  csprojs.set(id, {
    packable: !/<IsPackable>\s*false\s*<\/IsPackable>/i.test(text),
    rel: path.relative(repo, f).split(path.sep).join('/'),
  });
}

const retired = fs.existsSync(path.join(repo, 'devtools', 'retired-names.txt'))
  ? fs.readFileSync(path.join(repo, 'devtools', 'retired-names.txt'), 'utf8').split(/\r?\n/)
    .map((l) => l.replace(/#.*$/, '').trim()).filter(Boolean)
  : [];

// Which D-numbers does the SOURCE cite? Those are permanent addresses (shipped XML docs on nuget.org).
const cited = new Map();
for (const f of sourceFiles) {
  if (f.endsWith('.txt') || f.endsWith('.json')) continue;   // retired-names + lockfiles are not citations
  let text = '';
  try { text = fs.readFileSync(f, 'utf8'); } catch { continue; }
  for (const m of text.matchAll(/\bD([1-9][0-9]?)\b/g)) {
    const id = `D${m[1]}`;
    cited.set(id, (cited.get(id) ?? 0) + 1);
  }
}

// ── parse DECISIONS.md into entries ───────────────────────────────────────────────────────────────
const docRel = DECISIONS_REL;
// The parse is SHARED (`decisions-parse.mjs`) — this file and `doc-shape` ran byte-identical copies of
// `sectionAfter`, each with the same end-at-EOF bug fixed separately, and this comment used to say so
// while keeping the copy. The module is that third copy not being written.
const lines = decisionLines();
const marks = decisionMarks(lines);

const entries = marks.map((m, j) => ({
  id: m.id,
  line: m.at + 1,
  body: lines.slice(m.at, j + 1 < marks.length ? marks[j + 1].at : sectionAfter(lines, m.at)),
}));

// 🔴 A PERMANENT FLOOR OF KNOWN-FALSE FINDINGS IS WORSE THAN NO CHECK, because it teaches its reader to
// skim. Three survived every real fix and are all the same two classes, so they are named here and the
// floor is zero — any finding from now on is worth reading.
//
// ⚠ A PLATFORM identifier is not this kit's to own. `SetWindowLong` (Win32, D24) and `CompressFormat`
// (Android, D43) are named BECAUSE they are the platform's — an entry explaining a Win32 trap has to say
// which call. Add to this list only an identifier that belongs to an OS or a third party; a kit type that
// has gone missing is exactly what this check is for.
const PLATFORM_IDENTIFIERS = new Set([
  'SetWindowLong',    // Win32 — D24's rejected attach-after-the-fact chrome
  'CompressFormat',   // Android Bitmap.CompressFormat — D43's WebP/API-30 trap
  // ⚠ The rule base names far more of these than DECISIONS.md does, and necessarily: a rule explaining
  // a platform trap has to say which platform call springs it. Every entry below is an OS, a BCL or a
  // third-party name — none is a Shenora type, which is what this check exists to find.
  'SetWindowSubclass',        // Win32
  'Chrome_WidgetWin_1',       // Win32 — WebView2's own window class
  'MediaMetadataRetriever',   // Android
  'GetFrameAtTime',           // Android — MediaMetadataRetriever's
  'GetPrimaryCpuAbi',         // Android — PackageManager's
  'WebViewAssetLoader',       // AndroidX
  'WKWebViewConfiguration',   // WebKit
  'SetUrlSchemeHandler',      // WebKit
  'IWKUrlSchemeHandler',      // WebKit
  'AppleEvent',               // macOS
  'CodesignEntitlements',     // MSBuild property, .NET for iOS
  'AsyncTaskMethodBuilder',   // BCL
  'TaskCanceledException',    // BCL
  'MuMuManager',              // a third-party Android emulator's CLI
]);

// ⚠ A name under DISCUSSION is not a name being claimed. D61 lists four rename candidates and says every
// one COLLIDED with an existing type — the entry's whole point is that they do not exist. Without these
// words the audit reports the rejected candidates as missing packages, which is true and useless.
const DISCUSSED = /\b(shadow(s|ed)?|collide[sd]?|candidate|rejected|proposed|would have been)\b/i;

// ── the claim checks, borrowed from cite-scan where they already work ─────────────────────────────
const looksLikeCode = (s) =>
  /^[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9_]+)?$/.test(s) && s.length >= 6 && /[a-z]/.test(s)
  && (/[A-Z].*[A-Z]/.test(s) || s.includes('_'));
const REPO_PATH = /^(src|tests|samples|devtools|\.claude|\.github)\//;
const AMEND = /\b(corrected|amended|amendment|superseded|obsolete|went stale|used to|no longer|was wrong|until 20\d\d-\d\d-\d\d|as of 20\d\d-\d\d-\d\d)\b/i;

// 🔴 A RETIRED NAME IN THIS FILE IS USUALLY CORRECT PAST TENSE, and conflating that with a live lie is
// how a noisy tool gets switched off. D65 is the entry that RETIRED `Shenora.Ipc`; naming it is the
// entry doing its job. So every hit is split by whether its own line reads as history — same regex
// doc-drift uses, but per LINE rather than over a 6-line window, because the window is what the
// amendment stacks defeat (that suppression is why this whole pass exists).
//   * a hit on a plain line  -> LIVE CLAIM, a real lie, fix it.
//   * a hit on a history line -> not a lie, but it sizes the rewrite: an entry that is mostly history
//     narration becomes a one-line tombstone.
const HISTORY = /\b(used to|use to|formerly|former|previously|rename[sd]?|removed|no longer|until|was|were|had|superseded|retired|deleted|cut|dropped|replaced|merged|history|historical|obsolete|legacy|once|originally|no such|folded|became|ceased|gone|did not survive)\b/i;

// `Shenora.IO.dll`, `Shenora.txt`, `Shenora.Android.csproj` are a FILE, not a package id — the dotted
// matcher cannot tell them apart and reported four phantom missing packages on the first run.
const FILE_EXT = /\.(dll|txt|md|json|nupkg|csproj|props|targets|xml|snupkg|yml|exe|pdb|js|mjs|ts|tsx|cs)$/i;

// 🔴 A LIVE NAMESPACE IS ONLY MISLABELLED WHEN THE LINE CALLS IT A PACKAGE — and this check used to
// fire on every correct mention of one, which is the same defect as a gate that cannot pass. Reported
// six entries as "stating something untrue" for saying exactly the right thing: *the
// `Shenora.Engine.Files` namespace*, *`Shell/` carries no `Shenora.Core.Ipc` dependency*, and the
// namespace TABLE itself. A finding a reader must dismiss six times is one they stop reading.
// So the label has to be ADJACENT, which is the shape of the actual error ("the `Shenora.Media`
// package"), rather than merely somewhere on the line — "(D48 made it a package, D55 folded it back)"
// sits on a line whose subject is correctly a namespace, and a word-anywhere test flags that too.
// ⚠ Deliberately still history-INSENSITIVE: a live namespace called a package is wrong in any tense.
const labelledPackage = (line, span) => {
  const quoted = '`' + span + '`';
  for (let at = line.indexOf(quoted); at >= 0; at = line.indexOf(quoted, at + 1)) {
    const after = line.slice(at + quoted.length);
    const before = line.slice(0, at);
    if (/^\s*(nuget\s+)?packages?\b/i.test(after)) return true;
    if (/\bpackages?\s+$/i.test(before)) return true;
  }
  return false;
};

// A NAME THE ENTRY IS REJECTING is not a name it claims exists — `Named `MediaPlayer`, NOT
// `WebMediaPlayer`` is the sentence doing its job, and reporting the absence of the rejected half calls
// the correct wording a lie.
// 🔴 SPAN-LEVEL, never line-level, and that is the whole care here. Adding `not` to the line-wide
// DISCUSSED test would suppress every finding on any line that happens to contain the word — a gate
// that stops catching things, which is the failure this file's own comments keep naming. So the
// negation has to sit immediately BEFORE this span, the way `labelledPackage` demands adjacency.
const negatedName = (line, span) => {
  const quoted = '`' + span + '`';
  for (let at = line.indexOf(quoted); at >= 0; at = line.indexOf(quoted, at + 1)) {
    // Trailing markup between the word and the name is ordinary here: `NOT **`X`**`, `not _`X`_`.
    // An ARTICLE between the negation and the name is ordinary English — *not a `ProfileId`* — and
    // omitting it cost a false finding on the one rule whose whole subject IS naming.
    if (/\b(not|never|rather\s+than|instead\s+of)\b\s*(an?|the)?[\s*_~]*$/i.test(line.slice(0, at))) return true;
  }
  return false;
};

// ⚠ A RETIRED NAME NEEDS AN IDENTIFIER FENCE ON BOTH SIDES, or it fires on every longer name that
// CONTAINS it: `MediaAccessOptions` — a live type — reported as the retired `MediaAccess`, and
// `IMediaStreamConversion` would report the retired `MediaStreamConversion`. `doc-drift` learned the
// same lesson when `Shenora.IO` was satisfied by `Shenora.iOS`.
// The trailing `.` case is deliberate: `Shenora.Media.Windows` must NOT report the shorter
// `Shenora.Media` (the longer retired name reports itself), while `Shenora.Media.` ending a sentence
// must still match — so a dot only fences when a namespace segment follows it.
const mentionsRetired = (line, name) => {
  let from = 0;
  for (;;) {
    const at = line.indexOf(name, from);
    if (at < 0) return false;
    const before = at === 0 ? '' : line[at - 1];
    const after = line.slice(at + name.length);
    const fencedBefore = !/[A-Za-z0-9_.]/.test(before);
    const fencedAfter = !/^[A-Za-z0-9_]/.test(after) && !/^\.[A-Za-z0-9_]/.test(after);
    if (fencedBefore && fencedAfter) return true;
    from = at + 1;
  }
};

// ── one unit's claims, whatever the unit IS ───────────────────────────────────────────────────────
// Extracted so the RULE BASE goes through the identical checks. The unit differs — a `D<n>` entry there,
// a whole file here — but "does this prose name something the tree does not have?" is the same question,
// and two copies of it is how one of them stops being fixed.
const scan = (e) => {
  // live = the claim is stated as CURRENT. past = the same hit on a history line: not a lie.
  const live = { pkgs: new Set(), ns: new Set(), ids: new Set(), paths: new Set(), names: new Set() };
  const past = new Set();

  e.body.forEach((line) => {
    // A line DISCUSSING a name (a rejected candidate, a collision) is not a line claiming it exists.
    const isHistory = HISTORY.test(line) || DISCUSSED.test(line);
    const note = (bucket, value) => (isHistory ? past.add(value) : live[bucket].add(value));

    for (const m of line.matchAll(/`([^`]+)`/g)) {
      const span = m[1];

      // A package id or a namespace: dotted, starts at the kit's root name.
      if (/^Shenora(\.[A-Za-z0-9_]+)+$/.test(span) && !FILE_EXT.test(span)) {
        const isPkg = csprojs.has(span);
        const isNs = namespaces.has(span) || [...namespaces].some((n) => n.startsWith(span + '.'));
        // ⚠ `Shenora.AppCallback` is a fully-qualified TYPE, not a claim about a namespace — the dotted
        // matcher cannot tell them apart, exactly as it could not tell a FILE apart above. A tail that
        // names a type the tree HAS is a correct reference; reporting it as a missing namespace is the
        // same known-false floor `FILE_EXT` was added to stop.
        const tail = span.slice(span.lastIndexOf('.') + 1);
        const isQualifiedType = !isPkg && !isNs
          && new RegExp(String.raw`\b(class|interface|record|struct|enum|delegate)\s+` + tail + String.raw`\b`).test(hay);
        if (isQualifiedType) continue;
        if (!isPkg && !isNs) note('pkgs', span);
        // Calling a live namespace a PACKAGE is the specific error that switched a gate off in D65's
        // neighbourhood, so it is reported even on a history line — the sentence is wrong either way.
        // ⚠ But only when the line actually calls it one; see `labelledPackage`.
        else if (!isPkg && isNs && labelledPackage(line, span)) live.ns.add(span);
      }

      const rejected = negatedName(line, span);
      for (const part of span.split(/[^A-Za-z0-9_]+/)) {
        if (looksLikeCode(part) && !PLATFORM_IDENTIFIERS.has(part) && !hay.includes(part)) {
          if (rejected) past.add(part); else note('ids', part);
        }
      }
      for (const token of span.split(/[\s,;()<>"']+/)) {
        const clean = token.replace(/[.,:;]+$/, '');
        if (!REPO_PATH.test(clean)) continue;
        if (clean.includes('*') || clean.includes('|') || clean.includes('...')) continue;
        // A PLACEHOLDER is not a path claim: `.claude/worktrees/agent-<id>` loses its `<id>` to the
        // token split above and arrives as a trailing `-`, which no file will ever match.
        if (clean.endsWith('-') || clean.endsWith('/') || clean.includes('<') || clean.includes('…')) continue;
        if (!fs.existsSync(path.join(repo, clean))) note('paths', clean);
      }
    }
    for (const name of retired) if (mentionsRetired(line, name)) note('names', name);
  });

  return {
    id: e.id,
    line: e.line,
    size: e.body.length,
    citedBy: cited.get(e.id) ?? 0,
    amendLines: e.body.filter((l) => AMEND.test(l)).length,
    historyLines: e.body.filter((l) => HISTORY.test(l)).length,
    pastHits: past.size,
    missPkgs: [...live.pkgs], missNs: [...live.ns], missIds: [...live.ids],
    missPaths: [...live.paths], deadNames: [...live.names],
    get fails() {
      return this.missIds.length + this.missPaths.length + this.missPkgs.length
        + this.missNs.length + this.deadNames.length;
    },
  };
};

const rows = entries.map(scan);

// ── the RULE BASE, through the identical checks ───────────────────────────────────────────────────
// 🔴 A DEAD NAME IN A RULE IS WORSE THAN ONE IN A DOC, and `ipc-contracts.md` says so in its own text:
// *"a rule is read as instructions — this line pointed at a folded package and a relayered file for
// days."* A decision entry is consulted when someone asks why; a rule is handed to every session whose
// task matches its area, and it is handed over as fact.
//
// The unit is the FILE, because that is what gets loaded and what gets rewritten. Everything else is
// shared with the entries above, deliberately: the history/negation/adjacency care took several rounds
// to get right and a second copy of these checks would not inherit any of it.
const ruleDirs = ['.claude/rules', '.claude/knowledge'];
const ruleUnits = [];
for (const dir of ruleDirs) {
  const full = path.join(repo, dir);
  if (!fs.existsSync(full)) continue;
  for (const name of fs.readdirSync(full).filter((n) => n.endsWith('.md')).sort()) {
    ruleUnits.push({
      id: `${dir}/${name}`,
      line: 1,
      body: fs.readFileSync(path.join(full, name), 'utf8').split(/\r?\n/),
    });
  }
}
const ruleRows = ruleUnits.map(scan);

// ── report ────────────────────────────────────────────────────────────────────────────────────────
const verbose = process.argv.includes('--verbose');
const only = process.argv.slice(2).filter((a) => /^D\d+$/i.test(a)).map((a) => a.toUpperCase());

console.log(`decision-audit: ${entries.length} entries in ${docRel}; haystack = ${sourceFiles.length} `
  + `source file(s), ${namespaces.size} namespace(s), ${csprojs.size} csproj(s), ${retired.length} retired name(s).`);
console.log('⚠ TRUTH ONLY. Whether a decision is still REASONABLE is a judgement this cannot make.\n');

const shown = rows.filter((r) => (only.length ? only.includes(r.id) : r.fails > 0 || verbose));
shown.sort((a, b) => b.fails - a.fails || b.size - a.size);

console.log('entry  line   size  cited  hist%  LIVE   what is stated as CURRENT and is not true');
console.log('─────  ─────  ────  ─────  ─────  ────   ─────────────────────────────────────────');
for (const r of shown) {
  const bits = [];
  if (r.missPkgs.length) bits.push(`pkg:${r.missPkgs.length}`);
  if (r.missNs.length) bits.push(`mislabelled:${r.missNs.length}`);
  if (r.missIds.length) bits.push(`ident:${r.missIds.length}`);
  if (r.missPaths.length) bits.push(`path:${r.missPaths.length}`);
  if (r.deadNames.length) bits.push(`retired:${r.deadNames.length}`);
  const histPct = Math.round((r.historyLines / r.size) * 100);
  console.log(
    `${r.id.padEnd(5)}  ${String(r.line).padStart(5)}  ${String(r.size).padStart(4)}  `
    + `${String(r.citedBy).padStart(5)}  ${String(histPct).padStart(4)}%  ${String(r.fails).padStart(4)}   `
    + bits.join(' ') + (r.pastHits ? `   (+${r.pastHits} correct past tense)` : ''),
  );
  const detail = (label, list) => { for (const v of list) console.log(`         ${label} ${v}`); };
  detail('✗ no such package OR namespace:', r.missPkgs);
  detail('⚠ called a PACKAGE, is only a namespace:', r.missNs);
  detail('✗ identifier not in source:', r.missIds);
  detail('✗ path does not exist:', r.missPaths);
  detail('✗ states a RETIRED name as current:', r.deadNames);
}

const clean = rows.filter((r) => r.fails === 0);
const big = rows.filter((r) => r.size > 15);
const orphan = rows.filter((r) => r.citedBy === 0);
const mostlyHistory = rows.filter((r) => r.size > 15 && r.historyLines / r.size > 0.4);
console.log(`\n${shown.length} entr(ies) state something untrue as CURRENT · ${clean.length} mechanically clean.`);
console.log(`${big.length} over the 15-line shape cap · ${mostlyHistory.length} are >40 % history narration `
  + `(tombstone candidates: ${mostlyHistory.map((r) => r.id).join(' ') || 'none'}).`);
console.log(`${orphan.length} cited nowhere in source — free to fold; the rest are permanent addresses.`);
console.log('\n🔴 Ranked worst-first, and a CLEAN ROW IS NOT A VERIFIED DECISION: this checks whether the');
console.log('   prose matches the tree, never whether the decision is still reasonable for what the kit is.');

// ── the rule base's own table ─────────────────────────────────────────────────────────────────────
const ruleShown = ruleRows.filter((r) => r.fails > 0).sort((a, b) => b.fails - a.fails);
console.log(`\n\nRULE BASE — ${ruleRows.length} file(s) under ${ruleDirs.join(' + ')}, the same checks.`);
console.log('⚠ A rule that teaches BY EXAMPLE names dead things on purpose — `generic-library.md` cites the');
console.log('  scenario-named types it exists to warn against. Ask whether the sentence CLAIMS or TEACHES.');
if (ruleShown.length === 0) {
  console.log(`✓ every rule's claims match the tree (${ruleRows.length} clean).`);
} else {
  console.log('rule                                      lines  LIVE');
  console.log('────────────────────────────────────────  ─────  ────');
  for (const r of ruleShown) {
    console.log(`${r.id.padEnd(40)}  ${String(r.size).padStart(5)}  ${String(r.fails).padStart(4)}`);
    const detail = (label, list) => { for (const v of list) console.log(`         ${label} ${v}`); };
    detail('✗ no such package OR namespace:', r.missPkgs);
    detail('⚠ called a PACKAGE, is only a namespace:', r.missNs);
    detail('✗ identifier not in source:', r.missIds);
    detail('✗ path does not exist:', r.missPaths);
    detail('✗ states a RETIRED name as current:', r.deadNames);
  }
  console.log(`\n${ruleShown.length} rule file(s) name something the tree does not have.`);
}
