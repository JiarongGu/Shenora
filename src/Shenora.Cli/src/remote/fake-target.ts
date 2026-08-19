// A scriptable stand-in for a build machine, so the DEVICE FLOW can be tested without one.
//
// 🔴 Why this exists. Everything from "run a command" downwards was proven against a real Mac once, by
// hand, in one session. That is not repeatable and it is not a gate: the next change to `ios.ts` gets no
// warning at all unless someone happens to have hardware plugged in. This fake makes every step up to
// the hardware boundary assertable here — WHICH commands ran, in WHICH order, through WHICH transport —
// so the only thing left unknown is whether the Mac obeys them.
//
// ⚠ It is deliberately NOT a mock framework. It records calls and answers from a script, because the
// questions worth asking are "did the signing build go through `gui` rather than `sh`" and "was the
// project named rather than the checkout root" — both of which are answered by looking at the recorded
// command strings.
import type { GuiRunOptions, Target, TargetRunOptions } from './target.js';
import type { RunResult } from '../exec.js';

export interface FakeCall {
  /** `sh`, `gui`, `probe`, `push`, `pull` — how the work was dispatched, which is often the assertion. */
  via: 'sh' | 'gui' | 'probe' | 'push' | 'pull';
  command: string;
  options?: TargetRunOptions | GuiRunOptions;
}

export interface FakeTargetScript {
  /** Answers for `sh`/`gui`, matched by substring against the command. First match wins. */
  responses?: Array<{ match: string | RegExp; status?: number; out?: string }>;
  /** Answers for `probe`, same matching. Anything unmatched answers ''. */
  probes?: Array<{ match: string | RegExp; out: string }>;
  /** Paths that exist. A prefix match, so a directory covers what is under it. */
  exists?: string[];
  /** Directory listings, keyed by exact path. */
  listings?: Record<string, string[]>;
  /** Newest mtimes, keyed by exact path. */
  mtimes?: Record<string, number>;
  isRemote?: boolean;
  label?: string;
}

/** Records everything asked of it and answers from {@link FakeTargetScript}. */
export class FakeTarget implements Target {
  readonly calls: FakeCall[] = [];
  readonly label: string;
  readonly isRemote: boolean;

  constructor(private readonly script: FakeTargetScript = {}) {
    this.isRemote = script.isRemote ?? true;
    this.label = script.label ?? 'you@mac.local';
  }

  /** Every command dispatched, in order — the assertion most of these tests make. */
  get commands(): string[] {
    return this.calls.map((c) => c.command);
  }

  /** Did anything run through this transport, and what? */
  via(kind: FakeCall['via']): string[] {
    return this.calls.filter((c) => c.via === kind).map((c) => c.command);
  }

  private answer(command: string): RunResult {
    for (const r of this.script.responses ?? []) {
      const hit = typeof r.match === 'string' ? command.includes(r.match) : r.match.test(command);
      if (hit) return { status: r.status ?? 0, out: r.out ?? '' };
    }
    return { status: 0, out: '' };
  }

  sh(command: string, options: TargetRunOptions = {}): RunResult {
    this.calls.push({ via: 'sh', command, options });
    return this.answer(command);
  }

  gui(script: string, options: GuiRunOptions): RunResult {
    this.calls.push({ via: 'gui', command: script, options });
    return this.answer(script);
  }

  probe(command: string): string {
    this.calls.push({ via: 'probe', command });
    for (const p of this.script.probes ?? []) {
      const hit = typeof p.match === 'string' ? command.includes(p.match) : p.match.test(command);
      if (hit) return p.out;
    }
    return '';
  }

  exists(path: string): boolean {
    return (this.script.exists ?? []).some((e) => path === e || path.startsWith(`${e}/`));
  }

  list(directory: string): string[] {
    return this.script.listings?.[directory] ?? [];
  }

  mtimeMs(path: string): number | null {
    return this.script.mtimes?.[path] ?? null;
  }

  newestMtimeMs(path: string): number | null {
    return this.script.mtimes?.[path] ?? null;
  }

  join(...parts: string[]): string {
    return parts.join('/').replace(/\/+/g, '/');
  }

  basename(p: string): string {
    return p.split('/').pop() ?? p;
  }

  dirname(p: string): string {
    return p.split('/').slice(0, -1).join('/');
  }

  push(localPath: string, targetPath: string): boolean {
    this.calls.push({ via: 'push', command: `${localPath} -> ${targetPath}` });
    return true;
  }

  pull(targetPath: string, localPath: string): boolean {
    this.calls.push({ via: 'pull', command: `${targetPath} -> ${localPath}` });
    return true;
  }

  close(): void {
    // Nothing held.
  }
}
