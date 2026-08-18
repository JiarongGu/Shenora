// Getting the source ONTO the Mac. The step whose absence made every other remote command a lie about
// which code it built.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { captureRun, run, fail, q } from '../exec.js';
import type { Target } from './target.js';

/**
 * The files worth sending: everything git tracks, plus everything it would let you add.
 *
 * 🔴 **`git ls-files -co --exclude-standard`, not a directory walk.** Measured on this repo: 625 files
 * against 23,882 on disk. The difference is `bin/`, `obj/`, `node_modules/` and every other build output
 * — which must not travel, because copying a Windows `obj/` onto a Mac does not merely waste time, it
 * hands the Mac's build a stale intermediate stamped for another machine.
 *
 * ⚠ It also excludes anything gitignored, which is how `local/` — private by construction — stays here.
 *
 * @returns paths relative to the repo root, or null when this is not a git repo (already reported).
 */
export function filesToPush(root: string): string[] | null {
  const listed = captureRun('git', ['-C', root, 'ls-files', '-co', '--exclude-standard']);
  if (listed.status !== 0) {
    fail('`shenora ios push` needs a git repository — it uses git only to decide WHICH files to send.',
      '  Without one there is no way to tell your source from your build output.'
      + ' Set "remote": { "dir": … } and sync the Mac yourself.');
    return null;
  }
  return listed.out.split('\n').map((l) => l.trim()).filter(Boolean);
}

export interface PushResult {
  files: number;
  bytes: number;
}

/**
 * Copy the working tree to the Mac.
 *
 * ⚠ **Uncommitted edits travel, deliberately.** The obvious implementation is `git push`, and it is the
 * wrong one for a dev loop: the Mac would build HEAD, so the fix you just made and have not committed
 * never arrives — the build reproduces the very error you were fixing, and "my fix did not work" is the
 * wrong but completely natural conclusion.
 *
 * ⚠ **It ADDS and OVERWRITES; it does not delete.** A file removed here stays there. That is the honest
 * trade for not running `rm -rf` on a machine over the network from a tool, and it is stated rather than
 * hidden because it can matter: a renamed file leaves its old copy behind, and the old copy still
 * compiles.
 *
 * 🔴 **If the destination is a git checkout, its git metadata now DESCRIBES A TREE THAT IS NOT THERE.**
 * The files are current; `git log` still names whatever commit was checked out, and `git status` shows
 * every pushed file as modified. Two consequences worth knowing before they bite: reading `git log` on
 * the Mac to find out what you are building tells you the wrong thing, and a `git checkout -- .` there
 * silently reverts everything this sent. Prefer a directory that is NOT a checkout — set
 * `"remote": { "dir": … }` to a scratch path — if that matters to you.
 */
export function pushTree(target: Target, root: string, remoteDir: string): PushResult | null {
  const files = filesToPush(root);
  if (!files) return null;
  if (files.length === 0) {
    fail('there is nothing to send — git lists no files here.');
    return null;
  }

  const stamp = process.hrtime.bigint().toString(36);
  const listFile = path.join(os.tmpdir(), `shenora-push-${stamp}.txt`);
  const archive = path.join(os.tmpdir(), `shenora-push-${stamp}.tgz`);
  const remoteArchive = `/tmp/shenora-push-${stamp}.tgz`;

  try {
    // ⚠ A LIST FILE rather than an argument list. 625 paths is already past what a command line takes
    // comfortably, and the failure mode when it is too long is a truncated archive rather than an error.
    fs.writeFileSync(listFile, `${files.join('\n')}\n`, 'utf8');

    // `-T` reads the list; `-C` makes every path relative to the repo root so it unpacks the same shape.
    // Spawned directly, no shell — `tar` is on PATH on Windows 10+ and on macOS.
    //
    // 🔴 **The archive is named RELATIVELY, from a cwd of the temp directory.** GNU tar reads a colon in
    // an archive name as an rsh-style `host:path`, so an absolute Windows path made it try to open a
    // network connection to a host called `C` — `Cannot connect to C: resolve failed`, about a local
    // file. `--force-local` fixes it for GNU tar and does not exist on the bsdtar that ships with
    // Windows 10, so the portable answer is to leave the drive letter out of the argument entirely.
    const made = run('tar', ['-czf', path.basename(archive), '-C', root, '-T', path.basename(listFile)],
      { quiet: true, cwd: os.tmpdir() });
    if (made.status !== 0) {
      fail('could not pack the source tree.', made.out.trim() || undefined);
      return null;
    }

    const bytes = fs.statSync(archive).size;
    console.log(`shenora: sending ${files.length} files (${(bytes / 1_048_576).toFixed(1)} MB)`
      + ` to ${target.label}…`);

    if (!target.push(archive, remoteArchive)) {
      fail(`could not copy the archive to ${target.label}.`);
      return null;
    }

    // 🔴 `mkdir -p` FIRST: on a Mac that has never seen this project the directory does not exist, and
    // `tar -C` into a missing directory fails with a message about the archive rather than the path.
    const unpacked = target.sh(
      `mkdir -p ${q(remoteDir)} && tar -xzf ${q(remoteArchive)} -C ${q(remoteDir)}`,
      { quiet: true },
    );
    target.sh(`rm -f ${q(remoteArchive)}`, { quiet: true });
    if (unpacked.status !== 0) {
      fail(`could not unpack the source on ${target.label}.`, unpacked.out.trim() || undefined);
      return null;
    }

    console.log(`shenora: ${remoteDir} is up to date.`);
    return { files: files.length, bytes };
  } finally {
    fs.rmSync(listFile, { force: true });
    fs.rmSync(archive, { force: true });
  }
}
