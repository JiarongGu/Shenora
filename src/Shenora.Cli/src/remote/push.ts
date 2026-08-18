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
  /** Files this tool had sent before and has now taken away. */
  removed: number;
}

/**
 * What the last push sent, kept ON the target.
 *
 * ⚠ It lives beside the source rather than here because the question it answers is about THAT machine:
 * two developers pushing to one Mac, or the same one from two checkouts, each need the manifest that
 * matches what is actually on disk there.
 */
const MANIFEST = '.shenora-push-manifest';

/**
 * Paths already on the target that this push is responsible for.
 *
 * 🔴 **With no manifest yet, a git checkout's own INDEX is one** — and that case is not hypothetical, it
 * is the FIRST push into an existing checkout, which is how most people will start. Measured: pushing
 * today's tree over a checkout several weeks old left both halves of every renamed file, so
 * `IFileLockInspector` existed twice and the kit failed to compile with three errors on a tree that is
 * clean here. `git ls-files` names exactly what that older commit put there, which is exactly what may
 * need taking away.
 *
 * ⚠ Tracked files only, so a build output or anything untracked is never a deletion candidate.
 */
function previousManifest(target: Target, remoteDir: string): string[] {
  const raw = target.probe(`cat ${q(target.join(remoteDir, MANIFEST))} 2>/dev/null`);
  if (raw) return raw.split('\n').map((l) => l.trim()).filter(Boolean);

  const tracked = target.probe(`git -C ${q(remoteDir)} ls-files 2>/dev/null`);
  return tracked ? tracked.split('\n').map((l) => l.trim()).filter(Boolean) : [];
}

/**
 * Delete what we sent last time and would not send now.
 *
 * 🔴 The delete list is `previous MINUS current`, so it can only ever name a file this tool put there.
 * Computed HERE rather than by a remote `find`, which would have to decide what belongs to us and would
 * get it wrong on a directory holding anything else.
 */
function removeStale(target: Target, remoteDir: string, current: string[], stamp: string): number {
  const keep = new Set(current);
  const stale = previousManifest(target, remoteDir).filter((f) => !keep.has(f));
  if (stale.length === 0) return 0;

  // ⚠ Through a FILE and `xargs`, never an argument list: a rename sweep can stale hundreds of paths and
  // an `rm a b c …` command line would sail past ssh's 8 KB ceiling — where it is truncated and can still
  // report success, deleting some prefix of what was asked and saying it did the lot.
  const listing = path.join(os.tmpdir(), `shenora-stale-${stamp}.txt`);
  const remoteListing = `/tmp/shenora-stale-${stamp}.txt`;
  try {
    fs.writeFileSync(listing, `${stale.join('\n')}\n`, 'utf8');
    if (!target.push(listing, remoteListing)) return 0;
    // `-I{}` so a path containing a space is one argument; `rm -f` so an already-absent file is not an
    // error — the manifest describes what we sent, not what survived.
    target.sh(`cd ${q(remoteDir)} && xargs -I{} rm -f {} < ${q(remoteListing)}; rm -f ${q(remoteListing)}`,
      { quiet: true });
    return stale.length;
  } finally {
    fs.rmSync(listing, { force: true });
  }
}

/** Record what this push sent, so the next one knows what to take away. */
function writeManifest(target: Target, remoteDir: string, files: string[], stamp: string): void {
  const local = path.join(os.tmpdir(), `shenora-manifest-${stamp}.txt`);
  try {
    fs.writeFileSync(local, `${files.join('\n')}\n`, 'utf8');
    target.push(local, target.join(remoteDir, MANIFEST));
  } finally {
    fs.rmSync(local, { force: true });
  }
}

/**
 * Copy the working tree to the Mac.
 *
 * ⚠ **Uncommitted edits travel, deliberately.** The obvious implementation is `git push`, and it is the
 * wrong one for a dev loop: the Mac would build HEAD, so the fix you just made and have not committed
 * never arrives — the build reproduces the very error you were fixing, and "my fix did not work" is the
 * wrong but completely natural conclusion.
 *
 * 🔴 **It DELETES what it previously sent and would no longer send, and that is not tidiness.** The first
 * version only added and overwrote, on the reasoning that a tool should not `rm` over the network. It
 * broke the very first real build: the Mac's older checkout still held files this kit had since renamed,
 * so the push left both copies and `IFileLockInspector` existed twice — three compile errors in the KIT,
 * on a tree that builds cleanly here. **A stale source file is not clutter, it is a second definition**,
 * and the failure reads as "the framework is broken" rather than "your copy is stale".
 *
 * ⚠ It removes **only paths the manifest IT wrote last time names**, never anything else on that machine.
 * A file the Mac has that this never sent is untouched, so pointing `remote.dir` at a directory holding
 * other things cannot lose them.
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

    const removed = removeStale(target, remoteDir, files, stamp);
    writeManifest(target, remoteDir, files, stamp);

    console.log(`shenora: ${remoteDir} is up to date`
      + `${removed > 0 ? ` (${removed} stale file${removed === 1 ? '' : 's'} removed)` : ''}.`);
    return { files: files.length, bytes, removed };
  } finally {
    fs.rmSync(listFile, { force: true });
    fs.rmSync(archive, { force: true });
  }
}
