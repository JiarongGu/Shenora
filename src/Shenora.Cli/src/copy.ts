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
import { fail, run } from './exec.js';
import { projectDir, requireFields, type DeployConfig } from './config.js';

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

/**
 * The name this command drops in every bundle directory it creates.
 *
 * 🔴 It exists so the DELETE below can tell a directory this tool made from one the adopter did. Every
 * other guard here is about the path; this one is about the CONTENTS, and it is the only thing that can
 * catch a `webTarget` that is perfectly well-formed and simply points at the wrong place.
 */
const MARKER = '.shenora-bundle';

/**
 * Where the bundle goes — or null, having explained why not.
 *
 * 🔴 <b>THIS COMMAND DELETES ITS DESTINATION</b>, so the destination has to be earned rather than
 * computed. `webTarget` and `project` both come from a JSON file the adopter edits, and neither was
 * checked; measured against a config root of `D:/adopter`, with the old code:
 *
 *   webTarget: ""              -> deletes the app-head project directory, .csproj included
 *   webTarget: "Resources/Raw" -> deletes every MAUI raw asset: fonts, seed databases, licences
 *   webTarget: "../../.."      -> rmSync("D:\\")
 *   project:   "../x/A.csproj" -> escapes the config root entirely
 *
 * The first two are not attacks; they are an adopter reading "where the app head serves its bundle
 * from" and answering slightly wrong. That is the whole reason this refuses instead of trusting.
 */
function resolveBundleTarget(cfg: DeployConfig): string | null {
  const root = path.resolve(cfg.root);
  // `projectDir` (config.ts) owns the directory-or-file question. It was solved HERE first and the
  // three build commands each kept the bug for a while, which is why it is one shared helper now.
  const appHead = projectDir(cfg);
  const to = path.resolve(appHead, cfg.webTarget);

  const inside = (parent: string, child: string) => {
    const rel = path.relative(parent, child);
    return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
  };

  if (!inside(root, appHead) && appHead !== root) {
    fail(`project points outside the config root: ${cfg.project}`,
      `  ${cfg.file} governs ${root}; a project above it is almost certainly a typo.`);
    return null;
  }
  if (!inside(appHead, to)) {
    // Catches the empty string and every `..` walk in one test: the bundle lives BELOW the app head,
    // so a target that is the project directory itself, or anywhere outside it, is not a bundle path.
    fail(`webTarget must name a directory inside the app head: ${JSON.stringify(cfg.webTarget)}`,
      '  It is relative to the project folder — e.g. "Resources/Raw/wwwroot". Refusing to delete '
      + `'${to}'.`);
    return null;
  }
  return to;
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

  const to = resolveBundleTarget(cfg);
  if (to === null) return;

  // Replace rather than merge: a file deleted from the build must not survive in the app head, where it
  // would still be served and would still be embedded in the next package.
  //
  // ⚠ But only a directory THIS COMMAND MADE. A well-formed `webTarget` aimed one level too high is
  // still a delete of the adopter's own files, and no path check can see that — so an existing
  // destination without the marker is refused rather than emptied. First run after an upgrade included:
  // saying so once is much cheaper than the alternative it prevents.
  if (fs.existsSync(to)) {
    if (!fs.existsSync(path.join(to, MARKER))) {
      fail(`${path.relative(cfg.root, to)} already exists and this command did not create it.`,
        '  Refusing to delete it. If it IS the bundle directory, remove it yourself and re-run; if it\n'
        + '  is not, fix `webTarget` — it is relative to the app head, e.g. "Resources/Raw/wwwroot".');
      return;
    }
    fs.rmSync(to, { recursive: true, force: true });
  }

  const files = copyTree(from, to);
  // Written AFTER the copy, so an interrupted run leaves no marker and the next one still refuses.
  fs.writeFileSync(path.join(to, MARKER),
    'Written by `shenora copy`. Its presence is what allows this directory to be replaced.\n');
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
  // 🔴 `run`, NOT `sh` — this whole command was unusable on Windows. `sh` spawns `/bin/sh`, which is not
  // there, so `shenora sync` failed before `dotnet` was ever reached; and the only reason a shell was
  // wanted here is `| tail -20`, which is trimming, not shelling. `run`'s own doc names this exact case:
  // "Where output has to be trimmed, capture brings it into the tool and the filtering happens here."
  //
  // ⚠ Windows is not an edge case for this CLI — the Android half exists BECAUSE most .NET Android work
  // happens there, and `sync` is a build-time command with nothing platform-specific about it. Its
  // failure also pointed at output that did not exist: "see the output above", above nothing at all.
  const r = run('dotnet', ['restore', path.join(cfg.root, cfg.project)], { cwd: cfg.root, quiet: true });
  const tail = lastLines(r.out, 20);
  if (tail) console.log(tail);
  if (r.status !== 0) fail('restore failed — see the output above.');
}

/**
 * The last `count` lines, as `| tail -n` gave us before the shell went away. Split out because it is the
 * behaviour that replaced a shell pipe, and a silent off-by-one here would quietly hide the error line a
 * failed restore ends with.
 */
export function lastLines(text: string, count: number): string {
  const lines = text.trimEnd().split(/\r?\n/);
  return lines.length <= count ? lines.join('\n') : lines.slice(-count).join('\n');
}
