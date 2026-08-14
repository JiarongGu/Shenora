// The adopter's inputs. Deliberately tiny: everything else is derived, or asked of the machine.
import fs from 'node:fs';
import path from 'node:path';

export const CONFIG_FILE = 'shenora.deploy.json';

/** What an adopter writes down. Anything absent here is either derived or discovered at run time. */
export interface DeployConfig {
  /** The .NET app head to build (e.g. a MAUI csproj), relative to the config file. */
  project: string;
  /** Target framework moniker for the iOS head. */
  tfm: string;
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
  "tfm": "net10.0-ios",
  "bundleId": "com.example.myapp",
  "team": "ABCDE12345",
  "webDir": "web/dist"
}
`;
