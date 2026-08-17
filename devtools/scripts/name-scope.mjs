// name-scope — a type whose name claims an AREA while serving one KIND, and a file named after a type
// that does not exist.
//
// 🔴 WHY THIS EXISTS, and it is one incident with two halves (2026-08-17). The owner opened
// `InteractiveSession.cs` and asked why its filename did not match its classes. It declared
// `SessionResult` and `SessionErrorCodes` — names promising every session kind, used by the interactive
// one alone, while a streaming session ends with its own type and a pooled one hands back a lease. The
// same read turned up `WinFormsHost.cs`, a file named after a class deleted before 0.10.0, and two
// sample files still carrying the `Facade` vocabulary D65 retired.
//
// Neither half is findable by the prose scanners: every name involved EXISTS, so `doc-drift`,
// `cite-scan` and `stale-scan` all see a live identifier and say nothing. The signal is structural.
//
// ⚠ REVIEW TOOL, NEVER A GATE — the same standing as `stale-scan` and `self-rename-scan`. The rules it
// applies are heuristics over naming, and this repo's own convention allows a CLUSTER file
// (`ShellContracts.cs`, `FileDialogContracts.cs`) that deliberately names no single type. Failing a
// build on a style judgement is the disproportion `phase-workflow.md` warns about.
import fs from 'node:fs';
import path from 'node:path';

const repo = path.resolve(path.join(import.meta.dirname, '..', '..'));
const roots = ['src', 'samples', 'tests'];

// ⚠ `record struct` / `record class` FIRST, or `record` matches and the KIND is captured as the NAME —
// which reported half the record-structs in the tree as a type literally called "struct".
const DECL = /^\s*(?:\[[^\]]*\]\s*)*(?:public|internal|protected|private)?\s*(?:sealed\s+|static\s+|abstract\s+|partial\s+|readonly\s+|ref\s+|file\s+)*(?:record\s+struct|record\s+class|class|record|struct|interface|enum)\s+([A-Za-z_]\w*)/;

/** The trailing word of a PascalCase name — `StreamingSession` → `Session`. Null when there is none. */
const areaOf = (name) => /[a-z]([A-Z][a-z]+)$/.exec(name)?.[1] ?? null;

function walk(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (['bin', 'obj', 'node_modules', 'dist'].includes(entry.name)) continue;
      walk(full, out);
    } else if (entry.name.endsWith('.cs')) out.push(full);
  }
  return out;
}

const files = roots
  .map((r) => path.join(repo, r))
  .filter((d) => fs.existsSync(d))
  .flatMap((d) => walk(d));

const sourceOf = new Map(files.map((f) => [f, fs.readFileSync(f, 'utf8')]));
const declaredAnywhere = new Set();
const perFile = [];

for (const file of files) {
  const base = path.basename(file, '.cs');
  if (base.endsWith('.Designer') || base === 'AssemblyInfo' || base === 'GlobalUsings') continue;

  const types = [];
  let depth = 0;
  for (const raw of sourceOf.get(file).split(/\r?\n/)) {
    const line = raw.replace(/\/\/.*$/, '');
    const match = DECL.exec(line);
    if (match && depth === 0) types.push(match[1]);   // top-level only: a nested type never names a file
    depth = Math.max(0, depth + (line.match(/\{/g)?.length ?? 0) - (line.match(/\}/g)?.length ?? 0));
  }
  types.forEach((t) => declaredAnywhere.add(t));
  if (types.length) perFile.push({ file, base, types });
}

// The conventions this repo actually uses, so the residue is a real finding rather than a restatement
// of house style: `Foo.cs` may declare `IFoo` (+ impls), a plural file holds its family, and a file
// whose types ALL share its name as a prefix is a cluster named for its area.
const phantom = [];
const kindFiles = [];
for (const { file, base, types } of perFile) {
  const stem = base.replace(/\.(Unsupported|xaml)$/, '');          // TFM variants + XAML partials
  const singular = stem.replace(/(ie)?s$/, (m) => (m === 'ies' ? 'y' : ''));
  const claims = (t) =>
    t === stem || t === `I${stem}` || t === singular || t === `I${singular}`
    || t === `${singular}s` || t === `I${singular}s`;
  const namesake = types.findIndex(claims);

  if (namesake >= 0) {
    kindFiles.push({ file, namesake: types[namesake], others: types.filter((_, i) => i !== namesake) });
  } else if (!types.every((t) => t.startsWith(stem) || t.startsWith(singular))
             && !declaredAnywhere.has(stem)) {
    phantom.push({ file, stem, types });
  }
}

// How many KINDS compete for each area, which is the condition that makes a bare area name a false
// claim rather than a harmless one: `Session` had four, so `SessionResult` promised all of them and
// served one. An area with a single kind — one `UpdateStage`, one `ZipExtraction` — cannot mislead.
const kindsPerArea = new Map();
for (const { namesake } of kindFiles) {
  const area = areaOf(namesake);
  if (area && area !== namesake) kindsPerArea.set(area, (kindsPerArea.get(area) ?? new Set()).add(namesake));
}

const overbroad = [];
for (const { file, namesake: declaredNamesake, others } of kindFiles) {
  // ⚠ STRIP THE `I` FROM BOTH SIDES. `ComputedRemuxRoute.cs`'s namesake resolves to the INTERFACE
  // `IComputedRemuxRoute`, so comparing raw made the concrete `ComputedRemuxRoute` — and
  // `MediaContainerWriterExtensions` beside `IMediaContainerWriter` — read as area names claiming
  // everything. Both were false positives on this check's first run.
  const namesake = declaredNamesake.replace(/^I(?=[A-Z])/, '');
  const area = areaOf(namesake);
  if (!area || (kindsPerArea.get(area)?.size ?? 0) < 2) continue;
  for (const type of others) {
    const bare = type.replace(/^I(?=[A-Z])/, '');                   // IFooContext carries Foo
    if (bare.startsWith(namesake) || namesake.startsWith(bare)) continue;
    if (bare.startsWith(namesake.replace(area, ''))) continue;      // shares the kind's own prefix
    if (!bare.includes(area)) continue;                             // not an area name at all
    // ⚠ TESTS DO NOT COUNT as another user. `SessionResult` was named by `InteractiveSessionTests.cs`,
    // so counting every file made an earlier draft unable to catch the defect it was written from — a
    // zero that proved nothing. A test exercising one kind is not evidence the name serves several.
    const used = files.some((f) =>
      f !== file && !/Tests?\.cs$/.test(f) && new RegExp(`\\b${type}\\b`).test(sourceOf.get(f)));
    if (!used) overbroad.push({ file, type, namesake, area });
  }
}

const rel = (f) => path.relative(repo, f).replace(/\\/g, '/');

console.log(`name-scope: ${files.length} source file(s), ${declaredAnywhere.size} top-level type(s).\n`);

console.log(`PHANTOM FILENAME — named after a type that exists nowhere (${phantom.length}):`);
for (const p of phantom) console.log(`  ${rel(p.file)}\n      declares: ${p.types.join(', ')}`);
if (!phantom.length) console.log('  none.');

console.log(`\nOVER-BROAD NAME — an area name in a kind-specific file, used by nothing else (${overbroad.length}):`);
for (const o of overbroad) {
  const siblings = [...kindsPerArea.get(o.area)].join(', ');
  console.log(`  ${o.type}  in ${rel(o.file)}`);
  console.log(`      "${o.area}" has ${kindsPerArea.get(o.area).size} kinds (${siblings}), so this name`);
  console.log(`      promises all of them and serves ${o.namesake} alone.`);
}
if (!overbroad.length) console.log('  none.');

console.log('\nTRIAGE BY HAND — never a gate. A cluster file named for its AREA is correct and common here');
console.log('(ShellContracts.cs, FileDialogContracts.cs); what to look for is a name that CLAIMS more');
console.log('than it serves, and a filename left behind by a rename.');
