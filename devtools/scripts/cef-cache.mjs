// The pinned CEF build, downloaded once and verified: the ONE owner of that for every CEF tool
// (cef-binding generates from its headers, cef-native builds against them and its import library).
//
// CEF's own index names each file and its SHA-1, and a download that does not match is deleted, never
// used. The cache is gitignored and rebuilt when absent.
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const project = path.join(repo, 'src', 'Shenora.Chromium');
export const pin = JSON.parse(fs.readFileSync(path.join(project, 'cef.json'), 'utf8'));
export const cache = path.join(repo, 'devtools', '_cache', 'cef', pin.cef);

/** Thrown, never `process.exit()`: an exit with a fetch in flight aborts Node and REPLACES the exit code. */
export class CefToolError extends Error {}
export function fail(message) {
  throw new CefToolError(message);
}

export function run(cmd, args, options = {}) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, ...options });
  if (r.error) fail(`${cmd} could not start: ${r.error.message}`);
  return r;
}

/** The build's commit, which is also where its OS-specific headers live in CEF's git. */
export function commit() {
  const c = /\+g([0-9a-f]+)\+/.exec(pin.cef)?.[1];
  if (!c) fail(`cef.json's "cef" (${pin.cef}) carries no +g<commit>+ — copy it from CEF's index verbatim.`);
  return c;
}

/**
 * A CEF platform's minimal distribution (`windows64` unless named — CEF's own platform names, as cef.json
 * pins them): `<returned>/cef/{include,Release,Resources}`. Each platform has a folder of its own in the cache.
 */
export async function distribution(platform = 'windows64') {
  if (!pin.distributions?.[platform]) fail(`cef.json pins no ${platform} distribution.`);
  const dir = path.join(cache, platform);
  const marker = path.join(dir, 'extracted.sha1');
  const root = path.join(dir, 'dist');
  if (fs.existsSync(marker)) return root;

  const index = await (await fetch('https://cef-builds.spotifycdn.com/index.json')).json();
  const build = index[platform]?.versions?.find((v) => v.cef_version === pin.cef);
  if (!build) fail(`CEF's index has no ${platform} build ${pin.cef}.`);
  const file = build.files.find((f) => f.type === 'minimal');
  fs.mkdirSync(dir, { recursive: true });
  const archive = path.join(dir, file.name);

  if (!fs.existsSync(archive)) {
    console.log(`cef: downloading ${file.name} (${(file.size / 1048576).toFixed(0)} MB)`);
    const response = await fetch(`https://cef-builds.spotifycdn.com/${encodeURIComponent(file.name)}`);
    if (!response.ok) fail(`download failed: HTTP ${response.status}`);
    fs.writeFileSync(archive + '.part', Buffer.from(await response.arrayBuffer()));
    fs.renameSync(archive + '.part', archive);
  }
  const sha1 = crypto.createHash('sha1').update(fs.readFileSync(archive)).digest('hex');
  if (sha1 !== file.sha1) {
    fs.rmSync(archive, { force: true });
    fail(`SHA-1 mismatch for ${file.name}: got ${sha1}, the index says ${file.sha1}. The file was deleted.`);
  }

  console.log('cef: extracting (the archive is bzip2, which is most of the time spent)');
  fs.rmSync(root, { recursive: true, force: true });
  fs.mkdirSync(root, { recursive: true });
  // RELATIVE paths from inside the platform's folder: Git's GNU tar reads `D:\…` as a remote host named "D"
  // ("Cannot connect to D: resolve failed"), and Windows' own bsdtar accepts the relative form too.
  const tar = run('tar', ['-xjf', path.basename(archive), '-C', path.basename(root)], { cwd: dir });
  if (tar.status !== 0) fail(`tar failed: ${tar.stderr}`);
  const top = fs.readdirSync(root).find((d) => d.startsWith('cef_binary_'));
  if (!top) fail('the archive held no cef_binary_* folder.');
  fs.renameSync(path.join(root, top), path.join(root, 'cef'));
  fs.writeFileSync(marker, sha1);
  return root;
}

/** Run an async tool body with this family's error handling: a named failure, and exitCode, never exit(). */
export async function main(name, body) {
  try {
    await body();
  } catch (error) {
    console.error(`${name}: ${error instanceof CefToolError ? error.message : error.stack}`);
    process.exitCode = 1;
  }
}
