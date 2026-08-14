// `shenora copy` — stage the built web bundle into the app head, the step between `npm run build` and
// any native build.
//
// 🔴 WHY IT IS A COMMAND AND NOT A README LINE. A .NET app head embeds its web assets at BUILD time, so
// a stale bundle produces an app that runs perfectly and shows YESTERDAY'S UI — the single most
// confusing failure in hybrid development, because nothing is broken. This kit already learned it once
// on the desktop side, where `dev.mjs sample` had to start building the bundle first after a stale one
// shipped. Making it an explicit, named step is what stops "did I rebuild the web?" being a question.
import fs from 'node:fs';
import path from 'node:path';
import { fail, sh, q } from './exec.js';
import { requireFields, type DeployConfig } from './config.js';

/**
 * Recursive copy, written out rather than `fs.cpSync`.
 * ⚠ `fs.cpSync` hard-crashes Node 24 on at least one machine this kit is developed on (a fail-fast
 * 0xC0000409, no exception to catch). A CLI that dies with no message is worse than a slower loop.
 */
function copyTree(from: string, to: string): number {
  let files = 0;
  fs.mkdirSync(to, { recursive: true });
  for (const entry of fs.readdirSync(from, { withFileTypes: true })) {
    const src = path.join(from, entry.name);
    const dst = path.join(to, entry.name);
    if (entry.isDirectory()) files += copyTree(src, dst);
    else {
      fs.writeFileSync(dst, fs.readFileSync(src));
      files++;
    }
  }
  return files;
}

export function cmdCopy(cfg: DeployConfig): void {
  if (!requireFields(cfg, ['project', 'webDir'])) return;

  const from = path.join(cfg.root, cfg.webDir);
  if (!fs.existsSync(from)) {
    fail(`webDir does not exist: ${cfg.webDir}`, '  Build your web app first (e.g. `npm run build`).');
    return;
  }
  if (!fs.existsSync(path.join(from, 'index.html'))) {
    // Cheap, and it catches the common mis-set: pointing webDir at the SOURCE folder rather than the
    // build output. Copying that produces an app head full of .tsx files and a blank screen.
    fail(`no index.html in ${cfg.webDir} — is that the BUILD OUTPUT rather than the source?`);
    return;
  }

  const to = path.join(cfg.root, path.dirname(cfg.project), cfg.webTarget);

  // Replace rather than merge: a file deleted from the build must not survive in the app head, where it
  // would still be served and would still be embedded in the next package.
  if (fs.existsSync(to)) fs.rmSync(to, { recursive: true, force: true });

  const files = copyTree(from, to);
  console.log(`shenora: copied ${files} file(s) ${cfg.webDir} -> ${path.relative(cfg.root, to)}`);
}

/**
 * `sync` = copy + restore, the pair Capacitor's own `sync` names ("copy web assets AND update native
 * dependencies"). Kept as a separate verb rather than folded into `deploy` because restore is the slow
 * step and most inner-loop runs do not need it — a build after a package change does.
 */
export function cmdSync(cfg: DeployConfig): void {
  if (!requireFields(cfg, ['project'])) return;
  if (cfg.webDir) cmdCopy(cfg);
  else console.log('shenora: no webDir set — skipping the asset copy.');

  console.log('shenora: restoring…');
  const r = sh(`dotnet restore ${q(path.join(cfg.root, cfg.project))} 2>&1 | tail -20`, { cwd: cfg.root });
  if (r.status !== 0) fail('restore failed — see the output above.');
}
