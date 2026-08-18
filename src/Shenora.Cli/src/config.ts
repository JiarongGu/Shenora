// The adopter's inputs. Deliberately tiny: everything else is derived, or asked of the machine.
import fs from 'node:fs';
import path from 'node:path';

export const CONFIG_FILE = 'shenora.deploy.json';

/** What an adopter writes down. Anything absent here is either derived or discovered at run time. */
export interface DeployConfig {
  /** The .NET app head to build (e.g. a MAUI csproj), relative to the config file. */
  project: string;
  /**
   * Target framework moniker for the **iOS** head.
   *
   * ⚠ Unqualified for historical reasons, and that is exactly how it bites: beside `androidTfm` it
   * reads as "the tfm", so an Android-only project sets it to `net10.0-android`, and `ios deploy` then
   * builds the ANDROID target and dies on `NETSDK1147: install the android workload` — an error naming
   * a workload nobody wants on a Mac, so it reads as a broken machine. {@link iosTfm} is the clear
   * spelling; this stays as its fallback, and {@link platformTfm} refuses a mismatch by name.
   */
  tfm: string;
  /** Target framework moniker for the iOS head. Preferred over the unqualified {@link tfm}. */
  iosTfm?: string;
  /** Target framework moniker for the Android head. */
  androidTfm: string;
  /**
   * Logcat tag the app writes under, so `android log` shows the app's story instead of platform chatter.
   *
   * ⚠ It has a DEFAULT rather than being required, because the alternative is `--all` — and an adopter
   * whose first `android log` returns a screen of system noise concludes the tool is broken.
   */
  androidLogTag: string;
  /** The app's bundle identifier — must match the project's ApplicationId. */
  bundleId: string;
  /** Apple Developer Team ID. Only needed where automatic signing cannot cover it. */
  team: string;
  /** Debug unless you are measuring performance. */
  configuration: string;
  /** Built web assets to copy into the app head before building (see `shenora copy`). */
  webDir: string;
  /** Where the app head serves its bundle from, relative to the project. */
  webTarget: string;
  /** Absolute directory holding the config file. Filled in by {@link loadConfig}. */
  root: string;
  /** Absolute path of the config file itself. Filled in by {@link loadConfig}. */
  file: string;
}

const DEFAULTS: Omit<DeployConfig, 'root' | 'file'> = {
  project: '',
  tfm: 'net10.0-ios',
  androidTfm: 'net10.0-android',
  androidLogTag: 'DOTNET',
  bundleId: '',
  team: '',
  configuration: 'Debug',
  webDir: '',
  webTarget: 'Resources/Raw/wwwroot',
};

/**
 * Read `shenora.deploy.json` from `startDir` or any parent — a monorepo runs its CLI from anywhere.
 *
 * ⚠ There is deliberately NO machine-specific field: no Xcode path, no signing identity, no device id.
 * Those are facts about the machine and the phone plugged into it, so they are ASKED at run time. A
 * config that records them goes stale the first time someone else clones the repo, and the failure then
 * reads as "the tool is broken" rather than "that is not your device".
 */
export function loadConfig(startDir: string = process.cwd()): DeployConfig | null {
  let dir = path.resolve(startDir);
  for (;;) {
    const candidate = path.join(dir, CONFIG_FILE);
    if (fs.existsSync(candidate)) {
      let raw: Partial<DeployConfig>;
      try {
        raw = JSON.parse(fs.readFileSync(candidate, 'utf8')) as Partial<DeployConfig>;
      } catch (e) {
        // ⚠ Unhandled, this surfaced as a bare Node ESM stack trace with the parse error buried in it —
        // which reads as "the CLI is broken" rather than "your config has a typo". Found by writing a
        // malformed file on purpose while testing on a real Mac.
        console.error(`\nshenora: ${candidate} is not valid JSON.`);
        console.error(`  ${(e as Error).message}`);
        process.exitCode = 1;
        return null;
      }
      return { ...DEFAULTS, ...raw, root: dir, file: candidate };
    }
    const parent = path.dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

/** Does this path exist AND name a directory? False for anything unreadable — a missing project is the
 * caller's problem to report, not this helper's to guess about. */
function isDirectory(candidate: string): boolean {
  try {
    return fs.statSync(candidate).isDirectory();
  } catch {
    return false;
  }
}

/**
 * The app head's DIRECTORY, absolute — where its `bin/` and its bundle live.
 *
 * 🔴 **`project` MAY NAME A DIRECTORY**, because `dotnet build`/`publish`/`restore` all accept one and
 * this CLI hands `cfg.project` straight to them. On a directory, `path.dirname` silently yields its
 * PARENT — so `project: "src/MyApp"` resolves to `src/`, and every consumer then looks one level too
 * high. `shenora copy` staged the bundle into the wrong folder and reported success; the three build
 * commands looked for their artifact under `src/bin/…`, found nothing, and reported "the publish
 * reported success but no .apk appeared" — a confident statement about your build, describing a
 * directory that was never going to hold one.
 *
 * ⚠ One helper because it was fixed ONCE, in `copy.ts`, and three other call sites kept the bug. The
 * shape of that miss is the CLI's recurring one: a signal meaning "I looked in the wrong place"
 * presented as a fact about the world.
 */
export function projectDir(cfg: DeployConfig): string {
  const full = path.resolve(cfg.root, cfg.project);
  return isDirectory(full) ? full : path.dirname(full);
}

/**
 * The iOS TFM in force: `iosTfm` when set, else the unqualified `tfm`. Use this everywhere rather than
 * reading `cfg.tfm`, or setting the clear field would silently change nothing.
 */
export const iosTfmOf = (cfg: DeployConfig): string => cfg.iosTfm?.trim() || cfg.tfm;

/**
 * The TFM for the platform being built, refusing one that names a different platform.
 *
 * 🔴 A wrong answer here is only discovered by the SDK, minutes later, in the vocabulary of the wrong
 * platform: an `ios deploy` carrying `net10.0-android` dies on `NETSDK1147: the following workloads
 * must be installed: android`, which reads as a broken Mac rather than a config that could not express
 * two heads. Found by the first adopter taking a real app to a real iPhone.
 *
 * @returns the moniker, or null when it is missing or names the wrong platform (already reported).
 */
export function platformTfm(cfg: DeployConfig, platform: 'ios' | 'android'): string | null {
  const field = platform === 'ios' ? (cfg.iosTfm?.trim() ? 'iosTfm' : 'tfm') : 'androidTfm';
  const value = String(cfg[field] ?? '').trim();
  if (!value) {
    console.error(`\nshenora: ${CONFIG_FILE} is missing: ${field}`);
    console.error(`  ${cfg.file}`);
    process.exitCode = 1;
    return null;
  }
  if (!value.includes(`-${platform}`)) {
    console.error(`\nshenora: ${CONFIG_FILE}'s \`${field}\` is "${value}", which is not a ${platform} target.`);
    console.error(`  A MAUI app has one TFM per platform. Set \`${platform === 'ios' ? 'iosTfm' : 'androidTfm'}\``
      + ` to your ${platform} head (e.g. "net10.0-${platform}");`);
    console.error(`  \`tfm\` alone is read as the iOS one, which is why an Android value lands here.`);
    console.error(`  ${cfg.file}`);
    process.exitCode = 1;
    return null;
  }
  return value;
}

/** Fields a command needs, named ONE AT A TIME so the message is actionable rather than a schema dump. */
export function requireFields(cfg: DeployConfig, fields: (keyof DeployConfig)[]): boolean {
  const missing = fields.filter((f) => !String(cfg[f] ?? '').trim());
  if (missing.length === 0) return true;
  console.error(`\nshenora: ${CONFIG_FILE} is missing: ${missing.join(', ')}`);
  console.error(`  ${cfg.file}`);
  process.exitCode = 1;
  return false;
}

export const SAMPLE_CONFIG = `{
  "project": "src/MyApp/MyApp.csproj",
  "iosTfm": "net10.0-ios",
  "androidTfm": "net10.0-android",
  "bundleId": "com.example.myapp",
  "team": "ABCDE12345",
  "webDir": "web/dist"
}
`;
