// Which Mac, and — when it will not answer — WHICH of the six independent things is wrong.
import { argValue } from '../exec.js';
import type { DeployConfig } from '../config.js';
import { SshTarget, hasHost, type RemoteHost } from './ssh.js';
import { LocalTarget, type Target } from './target.js';

/** `--host user@mac.local` split into its parts. A bare host means "the same username as here". */
export function parseHostSpec(spec: string): RemoteHost | null {
  const trimmed = spec.trim();
  if (!trimmed) return null;
  const at = trimmed.lastIndexOf('@');
  if (at < 0) return { host: trimmed };
  const user = trimmed.slice(0, at).trim();
  const host = trimmed.slice(at + 1).trim();
  return host ? { host, ...(user ? { user } : {}) } : null;
}

/**
 * The Mac to drive, from — in order — `--host`, `SHENORA_IOS_HOST`, then the config's `remote` block.
 *
 * ⚠ **The environment variable is listed second on purpose.** A host is a fact about your network, and a
 * public repo should not carry one; the env var lets a shared `shenora.deploy.json` stay neutral while
 * each developer points at their own Mac.
 */
export function resolveHost(cfg: DeployConfig | null, args: readonly string[]): RemoteHost | null {
  const flag = argValue(args, '--host');
  if (flag) return parseHostSpec(flag);
  const env = process.env.SHENORA_IOS_HOST;
  if (env?.trim()) return parseHostSpec(env);
  return hasHost(cfg?.remote) ? cfg.remote : null;
}

/**
 * Where iOS work should run: the configured Mac, or this machine when it IS one.
 *
 * 🔴 **This replaced a bare `process.platform === 'darwin'` check, and the difference is the whole
 * feature.** That test asked "is the machine running this CLI a Mac?", which is only the right question
 * while there is nowhere else for the work to go. The kit's target adopter is on Windows with a Mac on
 * the LAN, so the honest question is "is there a Mac I can reach?" — and the answer can be yes on a
 * machine that could never answer the old one.
 *
 * @returns a target, or null when there is neither (already reported).
 */
export function resolveTarget(cfg: DeployConfig | null, args: readonly string[]): Target | null {
  const host = resolveHost(cfg, args);
  if (host) return new SshTarget(host);
  if (process.platform === 'darwin') return new LocalTarget();

  console.error('\nshenora: iOS work needs a Mac — Xcode, codesign, simctl and devicectl are Apple-only.');
  console.error('  This machine is not one, and no remote Mac is configured. Either:');
  console.error('    shenora ios doctor --host you@mac.local     one command, or');
  console.error('    set SHENORA_IOS_HOST=you@mac.local          for this shell, or');
  console.error(`    add "remote": { "host": "mac.local" } to ${cfg?.file ?? 'shenora.deploy.json'}`);
  console.error('  The Mac needs Remote Login on (System Settings → General → Sharing) and your key in');
  console.error('  its ~/.ssh/authorized_keys.');
  process.exitCode = 1;
  return null;
}

export type HostVerdict =
  | 'ok'
  | 'no-name'
  | 'unreachable'
  | 'refused'
  | 'denied'
  | 'throttled'
  | 'no-ssh-client'
  | 'unknown';

export interface HostDiagnosis {
  verdict: HostVerdict;
  /** One line naming what is wrong. */
  summary: string;
  /** What to do about it, already specific to the verdict. */
  fix: string[];
  /** ssh's own words, kept because they are usually better than a paraphrase. */
  detail: string;
}

/**
 * Ask the Mac to say `ok`, and classify a refusal into something actionable.
 *
 * 🔴 **The classification is the point, not the reachability test.** "Cannot connect" sends a developer
 * round a loop of six unrelated fixes — a sleeping Mac, Remote Login off, a key never authorised, an
 * `.local` name that does not resolve, a full auth-retry budget, no ssh client at all — and the evidence
 * that tells them apart is already in ssh's stderr. Reporting only pass/fail throws it away.
 *
 * ⚠ `.local` deserves its own sentence when the NAME is what failed: an mDNS name is answered by the
 * device itself, so "cannot resolve" means it is asleep or multicast is not crossing the network. It is
 * NOT evidence of a DNS problem on this box, which is the obvious wrong conclusion.
 */
export function diagnoseHost(host: RemoteHost): HostDiagnosis {
  const target = new SshTarget(host);
  const r = target.sh('echo ok', { quiet: true, timeoutMs: 30_000 });
  const detail = r.out.trim();
  if (r.status === 0 && /\bok\b/.test(r.out)) {
    return { verdict: 'ok', summary: `${target.label} answered.`, fix: [], detail };
  }
  return classifySshFailure(detail, host, target.label);
}

/**
 * Turn ssh's own words into a verdict. Pure, so `host.test.ts` can hold every branch still without a Mac
 * — which matters because these are the branches nobody can reproduce on demand.
 */
export function classifySshFailure(detail: string, host: RemoteHost, label: string): HostDiagnosis {
  const target = { label };
  const mdns = /\.local$/i.test(host.host);
  const say = (verdict: HostVerdict, summary: string, fix: string[]): HostDiagnosis =>
    ({ verdict, summary, fix, detail });

  if (/was not found on PATH/.test(detail)) {
    return say('no-ssh-client', 'there is no ssh client on this machine.', [
      'Windows: Settings → System → Optional features → OpenSSH Client.',
    ]);
  }
  if (/could not resolve hostname|name or service not known|nodename nor servname/i.test(detail)) {
    return say('no-name', `"${host.host}" does not resolve.`, mdns
      ? [
        'An .local name is answered by the Mac ITSELF, so this is not a DNS problem on this box:',
        'the Mac is asleep or off, or multicast is not crossing your network (common on mesh',
        'wifi, a guest SSID, or across VLANs). Try its IP address instead — if that works, the',
        'name is the only thing broken.',
      ]
      : ['Check the spelling, or use its IP address.']);
  }
  if (/connection refused/i.test(detail)) {
    return say('refused', `${host.host} is reachable but is not accepting ssh.`, [
      'Turn on Remote Login: System Settings → General → Sharing → Remote Login.',
      'That is off by default on a new Mac, and it is the single most common cause of this.',
    ]);
  }
  if (/timed out|no route to host|network is unreachable/i.test(detail)) {
    return say('unreachable', `${host.host} did not answer.`, [
      'The Mac is asleep, off, or on a different network from this machine.',
      'A Mac asleep on wifi is unreachable; on ethernet it usually still answers.',
      'Do not diagnose this with ping — macOS often drops ICMP, so silence proves nothing.',
    ]);
  }
  if (/permission denied/i.test(detail)) {
    return say('denied', `${target.label} refused the key.`, [
      'Append your public key to the Mac\'s ~/.ssh/authorized_keys.',
      'From this machine, once, with a password:',
      `  ssh ${target.label} "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys" < ~/.ssh/id_ed25519.pub`,
      'BatchMode is on, so a password prompt never appears — it just reads as a refusal.',
    ]);
  }
  if (/connection closed by/i.test(detail)) {
    // ⚠ Almost never a network fault. sshd closes on MaxAuthTries, and every offered key spends one.
    return say('throttled', `${host.host} closed the connection before authenticating.`, [
      'Usually the auth-retry budget: ssh offers every key it has and sshd hangs up after a few.',
      `Name the one that works: --host ${target.label} with "key" set, or an IdentityFile entry in`,
      '~/.ssh/config for this host.',
      'Retrying in a loop makes this worse rather than better.',
    ]);
  }
  return say('unknown', `${target.label} could not be reached.`, [
    'The message below is ssh\'s own.',
  ]);
}

/** Print a diagnosis the way `doctor` prints everything else. */
export function reportDiagnosis(d: HostDiagnosis): void {
  console.error(`\nshenora: ${d.summary}`);
  for (const line of d.fix) console.error(`  ${line}`);
  if (d.detail) console.error(`\n  ssh said:\n${d.detail.split('\n').map((l) => `    ${l}`).join('\n')}`);
}
