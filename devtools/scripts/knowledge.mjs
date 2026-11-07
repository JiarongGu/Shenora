// Knowledge-base doctor — keeps the on-demand knowledge, the skills and their generated index consistent,
// and the always-loaded brief from growing unnoticed. The always-loaded text is `AGENTS.md` (CLAUDE.md
// imports it); `.agents/knowledge/` and `.agents/skills/` are read on demand through `.agents/INDEX.md`,
// which `daoris sync` generates from the files' own frontmatter. This script never writes that index.
//   knowledge check               - INDEX <-> knowledge and skills, frontmatter, and skill-loader <-> skills (dev gate)
//   knowledge footprint           - the always-loaded byte budget (AGENTS.md vs a cap), reported and never fatal
//   knowledge new <name>          - scaffold a knowledge document with its frontmatter
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const knowledgeDir = path.join(repo, '.agents', 'knowledge');
// `.agents/skills/` is the source; `.claude/skills/` is its generated mirror, so only the source is read.
const skillsDir = path.join(repo, '.agents', 'skills');
const indexPath = path.join(repo, '.agents', 'INDEX.md');
// skill-loader's own table lists Shenora's skills, the ones the index marks `_(local)_`; the shared ones
// arrive from the canon and are listed in the index alone.
const skillIndexPath = path.join(skillsDir, 'skill-loader', 'SKILL.md');
const briefPath = path.join(repo, 'AGENTS.md');

// The always-loaded brief only: `.agents/INDEX.md` and the knowledge are read on demand, so counting them
// would report context nobody pays for on every session.
//
// 🔴 A budget exists to notice DRIFT, not to make anyone argue for a rule. It was raised 16 -> 17 -> 18 -> 32 KB
// while it counted a rules folder, and the lesson stands: a warning that is always lit is one nobody reads, and a
// rule that guards a defect class which produces NO SIGNAL (D63) is worth more than the kilobyte it costs.
// Trim first, raise second, and say why in the commit.
const CORE_BUDGET = 32 * 1024;

const rel = (p) => path.relative(repo, p).replace(/\\/g, '/');
const mdFiles = (dir) =>
  fs.existsSync(dir) ? fs.readdirSync(dir).filter((f) => f.endsWith('.md')).sort() : [];
// Measure CONTENT, not the checkout. `fs.statSync().size` counts CRLF as two bytes, so the same
// files measured 16.0 KB on a dev box (LF working tree) and 16.2 KB on the Windows CI runner
// (autocrlf checkout) — which failed a release at the footprint gate while every local run was
// green. The budget is a proxy for how much context these files cost a session; a line ending
// is not context. Normalising here makes the number identical everywhere.
const size = (p) => {
  if (!fs.existsSync(p)) return 0;
  return Buffer.byteLength(fs.readFileSync(p, 'utf8').replace(/\r\n/g, '\n'), 'utf8');
};
const kb = (n) => (n / 1024).toFixed(1) + ' KB';
const read = (p) => fs.readFileSync(p, 'utf8').replace(/\r\n/g, '\n');

// The generated index's rows: `| \`path\` _(local)_ | … |`. The path is the first backticked span of a row.
const indexRows = () =>
  read(indexPath).split('\n')
    .filter((line) => line.startsWith('| `'))
    .map((line) => ({ target: line.match(/^\| `([^`]+)`/)[1], local: /^\| `[^`]+` _\(local\)_/.test(line) }));

const skillDirs = () =>
  fs.existsSync(skillsDir)
    ? fs.readdirSync(skillsDir).filter((d) => fs.existsSync(path.join(skillsDir, d, 'SKILL.md'))).sort()
    : [];
// TABLE rows only (`| \`/name\` | … |`), never the whole file: skill-loader's Steps section names
// skills in prose too, and a skill mentioned only in passing is exactly the one that is NOT listed
// where the table is read. Matching prose would let it pass.
const listedSkills = () =>
  read(skillIndexPath).split('\n')
    .filter((line) => line.startsWith('|'))
    .map((line) => line.match(/`\/([a-z0-9-]+)`/))
    .filter(Boolean)
    .map((m) => m[1]);

// The frontmatter a knowledge document must carry (AGENTS.md: `name` / `applies_when` / `enforces`).
const frontmatter = (text) => {
  const m = text.match(/^---\n([\s\S]*?)\n---(?:\n|$)/);
  if (!m) return null;
  const fields = new Map();
  for (const line of m[1].split('\n')) {
    const kv = line.match(/^([a-z_]+):\s*(.*)$/);
    if (kv) fields.set(kv[1], kv[2].trim());
  }
  return fields;
};

const [sub, ...rest] = process.argv.slice(2);

if (sub === 'check') {
  let problems = 0;
  const fail = (msg) => { problems++; console.error('  FAIL ' + msg); };

  // The index is `daoris sync`'s to write; this asks only whether it is true of the files, so a document
  // added without a sync is named here, in this repository's own gate, and not only by `daoris check`.
  if (!fs.existsSync(indexPath)) {
    fail(`${rel(indexPath)} does not exist — run 'daoris sync'`);
  } else {
    const indexed = new Set();
    for (const { target } of indexRows()) {
      if (!fs.existsSync(path.join(repo, target))) fail(`index row \`${target}\` -> missing file`);
      else indexed.add(target);
    }
    for (const f of mdFiles(knowledgeDir)) {
      const target = rel(path.join(knowledgeDir, f));
      if (!indexed.has(target)) fail(`${target} has no row in ${rel(indexPath)} — run 'daoris sync'`);
    }
    for (const name of skillDirs()) {
      const target = rel(path.join(skillsDir, name, 'SKILL.md'));
      if (!indexed.has(target)) fail(`${target} has no row in ${rel(indexPath)} — run 'daoris sync'`);
    }
  }
  if (problems === 0) console.log(`  ok  INDEX: rows resolve + every knowledge document and skill indexed`);

  // A knowledge document is found by the index's `applies_when`, so one without it is never matched.
  const beforeFront = problems;
  for (const f of mdFiles(knowledgeDir)) {
    const fields = frontmatter(read(path.join(knowledgeDir, f)));
    const target = rel(path.join(knowledgeDir, f));
    if (!fields) { fail(`${target} has no frontmatter`); continue; }
    for (const key of ['name', 'applies_when', 'enforces']) {
      if (!fields.get(key)) fail(`${target} frontmatter has no \`${key}\``);
    }
    if (fields.get('name') && fields.get('name') !== f.replace(/\.md$/, ''))
      fail(`${target} frontmatter name is \`${fields.get('name')}\`, not \`${f.replace(/\.md$/, '')}\``);
  }
  if (problems === beforeFront) console.log(`  ok  knowledge: every document carries name / applies_when / enforces`);

  // The same consistency, for skills. `skill-loader`'s table is the one place a session looks to pick
  // Shenora's own skill, so a local SKILL.md missing from it never triggers — the failure is silent and
  // looks like the skill simply not existing. Shared skills are in the generated index alone.
  const beforeSkills = problems;
  const listed = new Set(listedSkills());
  const localSkills = new Set(
    fs.existsSync(indexPath)
      ? indexRows().filter((r) => r.local).map((r) => r.target.match(/^\.agents\/skills\/([^/]+)\/SKILL\.md$/)?.[1]).filter(Boolean)
      : []);
  for (const name of localSkills)
    if (!listed.has(name)) fail(`${rel(path.join(skillsDir, name, 'SKILL.md'))} is a local skill with no row in ${rel(skillIndexPath)} — it will never be picked`);
  for (const name of listed)
    if (!fs.existsSync(path.join(skillsDir, name, 'SKILL.md')))
      fail(`${rel(skillIndexPath)} lists /${name}, but .agents/skills/${name}/SKILL.md does not exist`);
  if (problems === beforeSkills) console.log(`  ok  skill-loader: rows resolve + every local skill listed`);

  process.exitCode = problems ? 1 : 0;
} else if (sub === 'footprint') {
  const briefBytes = size(briefPath);
  const knowledgeBytes = mdFiles(knowledgeDir).reduce((n, f) => n + size(path.join(knowledgeDir, f)), 0);
  console.log(`always-loaded: AGENTS.md ${kb(briefBytes)} (CLAUDE.md only imports it)`);
  console.log(`on-demand:     .agents/knowledge/ ${kb(knowledgeBytes)} (${mdFiles(knowledgeDir).length} files) + .agents/INDEX.md ${kb(size(indexPath))}, read only when a task matches`);
  const over = briefBytes > CORE_BUDGET;
  console.log(`core budget:   ${kb(briefBytes)} / ${kb(CORE_BUDGET)} — ${over ? '⚠ OVER: trim the brief or move a section to .agents/knowledge/' : 'ok'}`);

  // ADVISORY, NOT FATAL — corrected 2026-08-02 after this blocked a release by 0.2 KB.
  //
  // This is a style budget: a proxy for how much context the always-loaded files cost a session.
  // Being slightly over harms nothing today, and a release must be stopped by CORRECTNESS — build,
  // tests, the sensitive scan, version consistency — not by documentation size. It was made fatal
  // the same morning with the argument "an unenforced budget is not a budget", which mistook the
  // problem: the budget had drifted because nothing ever PRINTED it, and visibility was the fix.
  // `verify` still runs this, so the number is in every gate log where drift gets noticed.
} else if (sub === 'new') {
  const name = rest.find((a) => !a.startsWith('--'));
  if (rest.includes('--core')) {
    console.error('--core is gone: the always-loaded text is the canon-generated region of AGENTS.md, which a repository does not extend; a rule it must hold goes to the canon.');
    process.exitCode = 1;
  } else if (!name || !/^[a-z0-9-]+$/.test(name)) {
    console.error('usage: knowledge new <kebab-name>');
    process.exitCode = 1;
  } else {
    const dest = path.join(knowledgeDir, name + '.md');
    if (fs.existsSync(dest)) { console.error(`${rel(dest)} already exists`); process.exitCode = 1; }
    else {
      fs.mkdirSync(knowledgeDir, { recursive: true });
      fs.writeFileSync(dest, [
        '---',
        `name: ${name}`,
        'applies_when: TODO: the task that should load this document',
        'enforces: TODO: the invariant, in one clause',
        '---',
        '',
        `# ${name}`,
        '',
        'TODO: the invariant, why it holds, and what a session does about it.',
        '',
      ].join('\n'));
      console.log(`created ${rel(dest)} (on-demand).`);
      console.log('next: fill the body and the frontmatter, run `daoris sync` for the index row, then `knowledge check`.');
    }
  }
} else {
  console.error('usage: knowledge <check | footprint | new <kebab-name>>');
  process.exitCode = 1;
}
