// `shenora copy` — stage the built web bundle into the app head, the step between `npm run build` and
// any native build.
//
// 🔴 A .NET app head embeds its web assets at BUILD time, so a stale bundle produces an app that runs
// perfectly and shows YESTERDAY'S UI — the most confusing failure in hybrid development, because nothing
// is broken.
import fs from 'node:fs';
import path from 'node:path';
import { fail, run } from './exec.js';
import { projectDir, requireFields, type DeployConfig } from './config.js';

/**
 * Recursive copy, written out rather than `fs.cpSync`.
 * ⚠ `fs.cpSync` hard-crashes Node 24 on at least one machine this kit is developed on (a fail-fast
 * 0xC0000409, no exception to catch).
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
 * 🔴 It exists so the DELETE below can tell a directory this tool made from one the adopter did — the only
 * check that catches a `webTarget` which is perfectly well-formed and simply points at the wrong place.
 */
const MARKER = '.shenora-bundle';

/**
 * The name of the version stamp dropped beside {@link MARKER}, as `{"version":"…"}`.
 *
 * 🔴 **It exists so a shell can know what version its OWN packaged client is** — the enabling cause of a
 * defect that is otherwise silent and permanent. An app that serves a fetched bundle in preference to its
 * packaged one has to compare the two, and the adopter who reported this had nothing comparable: the
 * packaged client's version was baked from one source while the app's own constant was another, so the
 * comparison could not be written at all. `ResourcePackJournal.Open` requires it.
 *
 * ⚠ **The number is COPIED, never invented.** It is the web app's own declared version, so a build that
 * changes the bytes without changing that version stamps the old one — which is the app's bug to fix and
 * not one this tool can paper over by hashing, because a hash does not ORDER and ordering is the question.
 *
 * 🔴 **NO LEADING DOT — unlike {@link MARKER}, this one has to survive into the shipped app.** A
 * dot-prefixed `MauiAsset` never reaches an Android app: `AndroidComputeResPaths` discards it between
 * `AndroidAsset` and the staged assets directory, silently, so the build stays green and the file is
 * simply absent from the APK. `MARKER` may keep its dot because it is only ever read HERE, at build time.
 */
const STAMP = 'shenora-pack.json';

/**
 * Where the bundle goes — or null, having explained why not.
 *
 * 🔴 <b>THIS COMMAND DELETES ITS DESTINATION</b>, and `webTarget` and `project` both come from a JSON file
 * the adopter edits. Unchecked, against a config root of `D:/adopter`:
 *
 *   webTarget: ""              -> deletes the app-head project directory, .csproj included
 *   webTarget: "Resources/Raw" -> deletes every MAUI raw asset: fonts, seed databases, licences
 *   webTarget: "../../.."      -> rmSync("D:\\")
 *   project:   "../x/A.csproj" -> escapes the config root entirely
 *
 * The first two are not attacks — they are an adopter reading "where the app head serves its bundle from"
 * and answering slightly wrong.
 */
function resolveBundleTarget(cfg: DeployConfig): string | null {
  const root = path.resolve(cfg.root);
  // `projectDir` (config.ts) owns the directory-or-file question, in one shared helper.
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

  // Replaced, not merged: a file deleted from the build must not survive in the app head, where it would
  // still be served and still be embedded in the next package.
  //
  // ⚠ But only a directory THIS COMMAND MADE — see `MARKER`. An existing destination without the
  // marker is refused rather than emptied, first run after an upgrade included.
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
  stampVersion(cfg, from, to);
}

/**
 * Write {@link STAMP} into the copied bundle, carrying the web app's own declared version.
 *
 * 🔴 **It SAYS what it did, every run, including when it found nothing.** A stamp that is silently absent
 * is the same shape as the defect it exists to prevent: the shell falls back to a hand-maintained constant,
 * which is exactly the drift that made the comparison unwritable in the first place.
 *
 * ⚠ **`webDir` is the BUILD OUTPUT, so the manifest is looked for beside its PARENT** (`web/dist` →
 * `web/package.json`) and then in the config root. A layout that keeps neither is reported rather than
 * guessed at any further — inventing a version here would be worse than having none.
 */
function stampVersion(cfg: DeployConfig, from: string, to: string): void {
  const candidates = [path.join(path.dirname(from), 'package.json'), path.join(cfg.root, 'package.json')];

  for (const manifest of candidates) {
    let version: unknown;
    try {
      if (!fs.existsSync(manifest)) continue;
      version = JSON.parse(fs.readFileSync(manifest, 'utf8')).version;
    } catch {
      // A malformed package.json is the app's problem and not this command's to report twice — the next
      // candidate may still answer, and the "no version" line below covers it if none does.
      continue;
    }

    if (typeof version !== 'string' || version.trim() === '') continue;

    fs.writeFileSync(path.join(to, STAMP), `${JSON.stringify({ version })}\n`);
    console.log(`shenora: stamped packaged version ${version} (from ${path.relative(cfg.root, manifest)})`);
    return;
  }

  console.log('shenora: no "version" in package.json beside the web build — the bundle carries no version '
    + 'stamp.\n  A shell that serves fetched bundles needs one to know which is newer; see '
    + 'ResourcePackJournal.');
}

/**
 * `sync` = copy + restore — its own verb, because restore is the slow step and most inner-loop runs do not
 * need it.
 */
export function cmdSync(cfg: DeployConfig): void {
  if (!requireFields(cfg, ['project'])) return;
  if (cfg.webDir) cmdCopy(cfg);
  else console.log('shenora: no webDir set — skipping the asset copy.');

  console.log('shenora: restoring…');
  // 🔴 `run`, NOT `sh` — `sh` spawns `/bin/sh`, so this command fails on Windows before `dotnet` is ever
  // reached. The only reason a shell was wanted is `| tail -20`, which `lastLines` does here.
  const r = run('dotnet', ['restore', path.join(cfg.root, cfg.project)], { cwd: cfg.root, quiet: true });
  const tail = lastLines(r.out, 20);
  if (tail) console.log(tail);
  if (r.status !== 0) fail('restore failed — see the output above.');
}

/**
 * The last `count` lines, as `| tail -n` would give. ⚠ An off-by-one here silently hides the error line a
 * failed restore ends with.
 */
export function lastLines(text: string, count: number): string {
  const lines = text.trimEnd().split(/\r?\n/);
  return lines.length <= count ? lines.join('\n') : lines.slice(-count).join('\n');
}
