// Knowledge-base doctor — keeps the two-tier rule system consistent and the always-loaded base lean
// as it grows (core `.claude/rules/` auto-loads; `.claude/knowledge/` is read on demand via the index).
//   knowledge check               - RULES_INDEX <-> files consistency (CI/pre-commit gate)
//   knowledge footprint           - always-loaded byte budget (core rules + index vs a cap)
//   knowledge new <name> [--core] - scaffold a rule from TEMPLATE.md + append an index row
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const rulesDir = path.join(repo, '.claude', 'rules');
const knowledgeDir = path.join(repo, '.claude', 'knowledge');
const indexPath = path.join(rulesDir, 'RULES_INDEX.md');
const templatePath = path.join(rulesDir, 'TEMPLATE.md');
const claudeMd = path.join(repo, 'CLAUDE.md');

const CORE_BUDGET = 16 * 1024; // core rules + index + template; keep the auto-loaded base small

const rel = (p) => path.relative(repo, p).replace(/\\/g, '/');
const NON_RULES = new Set(['RULES_INDEX.md', 'TEMPLATE.md']);
const mdFiles = (dir) =>
  fs.existsSync(dir) ? fs.readdirSync(dir).filter((f) => f.endsWith('.md') && !NON_RULES.has(f)) : [];
const size = (p) => (fs.existsSync(p) ? fs.statSync(p).size : 0);
const kb = (n) => (n / 1024).toFixed(1) + ' KB';
const indexLinks = () =>
  [...fs.readFileSync(indexPath, 'utf8').matchAll(/\[([^\]]+)\]\(([^)]+)\)/g)].map((m) => ({ name: m[1], target: m[2] }));

const [sub, ...rest] = process.argv.slice(2);

if (sub === 'check') {
  let problems = 0;
  const fail = (msg) => { problems++; console.error('  FAIL ' + msg); };
  const indexed = new Set();
  for (const { name, target } of indexLinks()) {
    const abs = path.resolve(rulesDir, target); // index rows are relative to .claude/rules/
    if (!fs.existsSync(abs)) fail(`index row [${name}] -> ${target} (missing file)`);
    else indexed.add(abs);
  }
  for (const dir of [rulesDir, knowledgeDir])
    for (const f of mdFiles(dir))
      if (!indexed.has(path.join(dir, f))) fail(`${rel(path.join(dir, f))} has no RULES_INDEX row`);
  if (problems === 0) console.log(`  ok  RULES_INDEX: rows resolve + every rule indexed`);
  process.exit(problems ? 1 : 0);
}

if (sub === 'footprint') {
  const coreBytes = size(indexPath) + size(templatePath)
    + mdFiles(rulesDir).reduce((n, f) => n + size(path.join(rulesDir, f)), 0);
  const knowledgeBytes = mdFiles(knowledgeDir).reduce((n, f) => n + size(path.join(knowledgeDir, f)), 0);
  console.log(`always-loaded: CLAUDE.md ${kb(size(claudeMd))} + core rules/index ${kb(coreBytes)} = ${kb(size(claudeMd) + coreBytes)}`);
  console.log(`on-demand:     .claude/knowledge/ ${kb(knowledgeBytes)} (${mdFiles(knowledgeDir).length} files, read only when a task matches)`);
  const over = coreBytes > CORE_BUDGET;
  console.log(`core budget:   ${kb(coreBytes)} / ${kb(CORE_BUDGET)} — ${over ? '⚠ OVER: move a rule to .claude/knowledge/' : 'ok'}`);
  process.exit(over ? 1 : 0);
}

if (sub === 'new') {
  const core = rest.includes('--core');
  const name = rest.find((a) => !a.startsWith('--'));
  if (!name || !/^[a-z0-9-]+$/.test(name)) { console.error('usage: knowledge new <kebab-name> [--core]'); process.exit(1); }
  const dest = path.join(core ? rulesDir : knowledgeDir, name + '.md');
  if (fs.existsSync(dest)) { console.error(`${rel(dest)} already exists`); process.exit(1); }
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.copyFileSync(templatePath, dest);

  // Append a row into the matching section's table (after its last `|` row, before the next `##`).
  const target = core ? `${name}.md` : `../knowledge/${name}.md`;
  const row = `| [${name}](${target}) | TODO: applies when | TODO: enforces |`;
  const section = core ? '## Core' : '## Knowledge';
  const lines = fs.readFileSync(indexPath, 'utf8').split('\n');
  let anchor = lines.findIndex((l) => l.startsWith(section));
  let lastRow = anchor;
  for (let k = anchor + 1; k < lines.length && !lines[k].startsWith('## '); k++)
    if (lines[k].startsWith('|')) lastRow = k;
  lines.splice(lastRow + 1, 0, row);
  fs.writeFileSync(indexPath, lines.join('\n'));

  console.log(`created ${rel(dest)} (${core ? 'core — auto-loaded' : 'knowledge — on-demand'}) + a RULES_INDEX row.`);
  console.log('next: fill the rule body + the row\'s "applies when"/"enforces", then `knowledge check`.');
  process.exit(0);
}

console.error('usage: knowledge <check | footprint | new <kebab-name> [--core]>');
process.exit(1);
