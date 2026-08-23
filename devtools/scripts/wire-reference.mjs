// wire-reference — GENERATE docs/reference/wire.md from the source constants, and fail when it drifts.
//
// 🔴 WHY THIS IS THE ONE REFERENCE THAT EARNS ITS KEEP. The bar set for a `docs/reference/` was "does it
// beat the XML docs an IDE already shows from the nupkg?" — and for types, methods and parameters it
// does not, which is why there is no generated API dump here. The WIRE is the exception: module names,
// route types, event types, error codes and capability names are STRINGS a page author types by hand,
// on the other side of a language boundary where no IDE can help. Today they are found by grepping C#.
//
// ⚠ GENERATED AND GATED, never hand-written. D57 retired five design docs because a third copy of
// anything goes stale while nobody notices — so this file is only defensible if it CANNOT. `verify` runs
// `--check`, which regenerates into memory and fails on any difference, naming the constant.
//
// Usage: node devtools/scripts/wire-reference.mjs [--check]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { outOfScope } from './git-scope.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const OUT = path.join(repo, 'docs', 'reference', 'wire.md');
const check = process.argv.includes('--check');

/**
 * The declaring types worth publishing, in the order a page author meets them, with the section title
 * and a line saying what the reader DOES with these strings.
 *
 * ⚠ An ALLOW-LIST rather than "every public const": most constants are internal vocabulary (a temp-file
 * suffix, a lane name) and publishing them would invite an adopter to depend on them. What belongs here
 * is what CROSSES THE WIRE.
 */
const SECTIONS = [
  ['IpcCategories', 'Envelope categories', 'The `category` field on every message.'],
  ['IpcHostBridge', 'Handshake', 'The PAGE sends this module + type once it is ready; the host answers with its ShellInfo.'],
  ['IpcErrorCodes', 'Error codes', 'The `code` on an `IpcError`. Yours are your own; these are the kit\'s.'],
  ['IpcRequestEvents', 'Request-tracking events', 'Emitted as a long request starts, progresses and ends.'],
  ['MediaPlayerEvents', 'Media player', 'One vocabulary, two directions — an EVENT drives the page\'s element, a REQUEST of the same name drives the host\'s player.'],
  ['MediaPlayerModule', 'Media player routes', 'Routes the page sends to the host.'],
  // ⚠ ADDED 2026-08-10, and they had been missing since the conversion route shipped — a page WAITS on
  // READY before setting its element's src and branches on FAILED's `reason`, which makes these the most
  // page-typed strings in the media tier. The gate could not notice: a SECTIONS entry with no type is an
  // error, but a TYPE with no section is silently absent, so the reference said "matches the source
  // constants" while covering less of it — the same fail-open shape this script was fixed for once before.
  ['MediaConversionEvents', 'Media conversion', 'A conversion outlives its request, so the page learns from these rather than from a response.'],
  ['MediaConversionErrorCodes', 'Media conversion failures', 'The `reason` on a conversion FAILED event. Anything else is an exception TYPE name.'],
  ['ShellCapability', 'Shell capabilities', 'What a host advertises in its handshake, and what a page branches on instead of sniffing the platform.'],
  // The module name, the two routes and the EVENT are all named by hand on the page side — and the
  // event is the one whose drift is silent: a subscription to the wrong type simply never fires, which
  // is indistinguishable from a user who never pressed back.
  ['BackNavigation', 'Back gesture', 'Android\'s system back, offered to the page before the platform finishes the activity under it.'],
  // Events only — the page never calls anything here, so the two type strings ARE the whole contract
  // and a drifted one is a subscription that silently never fires.
  ['AppLifecycle', 'App lifecycle', 'The app leaving and re-entering the foreground, with how long it was away — the one thing a throttled page cannot time itself.'],
  // ⚠ ADDED 2026-08-18, all long shipped and none of them published here — found by a docs audit, not
  // by this gate, which is the point of NOT_WIRE below. A page names every one of these by hand.
  ['ClipboardModule', 'Clipboard routes', 'The page\'s access to the native clipboard — the capability D53 added for what React cannot reach.'],
  ['FileDialogModule', 'File dialog routes', 'Native open/save pickers, including the mobile shells\' own.'],
  ['IpcRequestsModule', 'Request-tracking routes', 'What a page calls to list, cancel or clear the long requests the events above report.'],
  ['WindowCommandModule', 'Window command routes', 'Frameless chrome drives the real window through these.'],
  ['DropZoneModule', 'Drop zone routes', 'Registering the page regions a native file drop is matched against.'],
  ['DropZoneManager', 'Drop zone events', 'What the host pushes back as a drag crosses a registered zone.'],
  ['SessionEvents', 'Browser session events', 'What an auxiliary session publishes on the event bus — the 0.11.0 replacement for the deleted observation taps.'],
  ['InteractiveSessionErrorCodes', 'Interactive session failures', 'The `code` when an interactive session cannot answer.'],
  ['ClipboardContent', 'Clipboard media types', 'The keys of `ClipboardContent.Formats` the kit names itself; an app\'s own type is its own string.'],
  // ⚠ A URL SHAPE rather than a message field, and it belongs here for the same reason the routes above
  // do: the page BUILDS this string by hand. A segment stream over a registered remote source is fetched
  // from `{RoutePath}~remote/{handle}/{resource}`, so the prefix is as much a contract as any type name.
  ['SegmentStreamOptions', 'Segment stream route shapes', 'The reserved path segment a page uses to name a source by its registered handle.'],
];

/**
 * 🔴 EVERY OTHER TYPE DECLARING A `public const string`, EXPLICITLY JUDGED NOT TO CROSS THE WIRE.
 *
 * The gate below fails on a type that is in NEITHER list, which is the hole this closes: SECTIONS is an
 * ALLOW-LIST, so a type nobody added was silently absent while the gate still reported "matches the
 * source constants" — it did, over less of the wire each time. The header already recorded that
 * happening once (`MediaConversionEvents`, 2026-08-10) and it had happened again by 0.11.0 to NINE
 * types, the page-facing clipboard among them. A list of what is deliberately EXCLUDED cannot rot the
 * same way: a new type forces a decision instead of defaulting to invisible.
 */
const NOT_WIRE = new Map([
  ['AppRootArgument', 'a command-line flag the launcher passes, never a message'],
  ['Files', 'a temp-file suffix on disk'],
  ['PathClaims', 'a mission claim-scope name, host-side scheduling only'],
  ['MissionScheduler', 'the global lane name, host-side scheduling only'],
  ['SegmentRunRequest', 'file names inside a segment run\'s own directory — the page follows the manifest, it does not build these'],
  ['SafeAreaScript', 'a marker inside an injected script, read by that script alone'],
  ['ShenoraEnvironment', 'a dev-marker FILE name on disk'],
  ['MauiShellNames', 'the shell name used in capability-refusal messages, not a route'],
  ['WebViewScripts', 'the injected script SOURCES themselves'],
  // ⚠ A CROSS-LANGUAGE contract, but not a WIRE one: `shenora copy` writes this file at BUILD time and the
  // host reads it at BOOT. No page ever types it, so publishing it in the page's reference would be the
  // wrong kind of documentation — its pair is pinned by `ResourcePackStampTests` against copy.ts instead.
  ['ResourcePackJournal', 'the version-stamp FILE the CLI drops in a bundle, read host-side at boot'],
]);

/** Every `public const string` with its summary's first sentence, keyed by declaring type. */
function readConstants() {
  const found = new Map();   // type -> [{ name, value, summary }]
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        // `outOfScope`, not a hand-written `bin`/`obj` list — git decides what a clone would not have.
        // A name list is what `git-scope.mjs` exists to retire: it is complete right up until a new
        // KIND of directory appears, and the last one was still added by name.
        if (outOfScope(path.relative(repo, full))) continue;
        walk(full);
      } else if (entry.name.endsWith('.cs')) {
        scan(fs.readFileSync(full, 'utf8'), found);
      }
    }
  };
  // 🔴 EVERY package that can declare a wire constant, not just the core one. `src/Shenora` alone left
  // the whole SHELL half of the wire unreadable — window commands, drop zones and session events are
  // declared in `Shenora.Windows` and are named by a page exactly as often as the core's own routes.
  // ⚠ The plain `Shenora` prefix would also match `Shenora.Tests`; these are named explicitly.
  for (const pkg of ['Shenora', 'Shenora.Windows', 'Shenora.Mobile', 'Shenora.Android', 'Shenora.iOS']) {
    const dir = path.join(repo, 'src', pkg);
    if (fs.existsSync(dir)) walk(dir);
  }
  return found;
}

function scan(source, found) {
  const lines = source.split(/\r?\n/);
  let type = null;
  for (let i = 0; i < lines.length; i++) {
    const declaration = /^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*(?:class|record|struct)\s+([A-Za-z0-9_]+)/.exec(lines[i]);
    if (declaration) type = declaration[1];

    const constant = /^\s*public const string ([A-Za-z0-9_]+)\s*=\s*"([^"]*)"/.exec(lines[i]);
    if (!constant || !type) continue;

    // The summary's first sentence, read UPWARD through the doc comment. Prose beats a bare name: the
    // reader needs to know when to send `PLAYER_LOAD`, not that it exists.
    // 🔴 NO LINE LIMIT, and the `break` below is the only terminator that is correct. This read upward at
    // most 11 lines, which SILENTLY PUBLISHED A MID-SENTENCE FRAGMENT for any constant documented in more:
    // the walk stopped inside the comment, so `summary` began mid-sentence and the first-sentence split
    // below returned that. `ShellCapability.LocalFiles` shipped as "local content asks for this and falls
    // back — …" in an adopter-facing reference. ⚠ `--check` CANNOT see it: it compares this output against
    // the committed file, so a wrong extraction is stable and agrees with itself for ever.
    let summary = '';
    for (let j = i - 1; j >= 0; j--) {
      const text = lines[j].trim();
      if (!text.startsWith('///')) break;
      // ⚠ A `<see cref="X"/>` becomes X rather than nothing. Deleting it left "a response to a client
      // request ()." — prose that reads as a typo and tells the reader less than the raw tag did.
      const body = text
        .replace(/^\/\/\/\s?/, '')
        // ⚠ The namespace prefix is optional AND must end at a separator. Written `[^"]*[.:]?` first,
        // where the greedy prefix ate the name and left its last LETTER: "a response to a client
        // request (e)". A capture that silently truncates is worse than no capture.
        .replace(/<see\s+cref="(?:[^"]*[.:])?([A-Za-z0-9_]+)"\s*\/>/g, '$1')
        .replace(/<[^>]+>/g, '')
        .trim();
      if (body) summary = summary ? `${body} ${summary}` : body;
    }
    summary = (summary.split(/(?<=\.)\s/)[0] ?? '').trim();

    if (!found.has(type)) found.set(type, []);
    found.get(type).push({ name: constant[1], value: constant[2], summary });
  }
}

function render(found) {
  const out = [
    '# The wire vocabulary',
    '',
    '> **GENERATED — do not edit.** `node devtools/scripts/wire-reference.mjs`, gated by `dev.mjs verify`.',
    '> Every value below is read from the source constants, so this page cannot drift from what ships.',
    '',
    'These are the strings your PAGE types by hand. Everything else about the surface — types, methods,',
    'parameters — your IDE already shows from the nupkg\'s XML docs, which is why there is no generated',
    'API dump here. **WHY** any of it is shaped this way lives in [DECISIONS.md](../DECISIONS.md).',
    '',
  ];

  // 🔴 AN ALLOW-LIST ENTRY THAT MATCHES NOTHING IS AN ERROR, NOT AN EMPTY SECTION. This used to
  // `continue`, which made the one failure this file exists to prevent completely silent: rename
  // `MediaPlayerEvents`, regenerate, and a whole vocabulary drops out of the published reference while the
  // gate reports "matches the source constants" — because it does, it just matches less of it. SECTIONS is
  // the SPEC of what must be published; a spec line with no source is a rename nobody swept, which is
  // precisely how D65 got past two prose gates. Failing here costs one line in this file when a type is
  // genuinely retired, and buys the guarantee the header claims.
  const missing = SECTIONS.filter(([type]) => !found.get(type)?.length).map(([type]) => type);
  if (missing.length > 0) {
    console.error(`\x1b[31m✖ wire-reference: no public const strings found for ${missing.join(', ')}.\x1b[39m`);
    console.error('  Either the type was renamed (update SECTIONS in this script) or its constants went.');
    console.error('  Publishing the reference without them would silently shrink the documented wire.');
    process.exitCode = 1;
  }

  // …AND THE OTHER DIRECTION, which is the one that actually bit: a type carrying wire constants that
  // NOBODY listed. Silent absence is indistinguishable from "there was nothing to publish", so a type
  // in neither list stops the generator until someone says which it is.
  const listed = new Set(SECTIONS.map(([type]) => type));
  const unjudged = [...found.keys()].filter((type) => !listed.has(type) && !NOT_WIRE.has(type));
  if (unjudged.length > 0) {
    console.error(`\x1b[31m✖ wire-reference: ${unjudged.join(', ')} declare(s) public const strings and appear(s) in neither list.\x1b[39m`);
    console.error('  If a page or client names these, add a SECTIONS row so they are PUBLISHED.');
    console.error('  If they never cross the wire, add them to NOT_WIRE with the reason.');
    console.error('  Doing neither is how the reference silently shrank before — see NOT_WIRE\'s note.');
    process.exitCode = 1;
  }

  for (const [type, title, blurb] of SECTIONS) {
    const rows = found.get(type);
    if (!rows?.length) continue;
    out.push(`## ${title}`, '', blurb, '', '| Constant | Value | |', '|---|---|---|');
    for (const r of rows) out.push(`| \`${type}.${r.name}\` | \`${r.value}\` | ${r.summary} |`);
    out.push('');
  }
  return out.join('\n');
}

const rendered = render(readConstants());

if (!check) {
  // ⚠ REFUSE TO WRITE a reference that is missing a section, rather than writing it and reporting a
  // non-zero exit nobody reads. `render` has already said which type is missing. Catching the damage in
  // `--check` afterwards is second best; not producing it is the actual protection, and the person running
  // the generator is the one who just did the rename and can fix SECTIONS in the same minute.
  if (process.exitCode) {
    console.error('  Refusing to write docs/reference/wire.md — fix SECTIONS first.');
  } else {
    fs.mkdirSync(path.dirname(OUT), { recursive: true });
    fs.writeFileSync(OUT, rendered);
    console.log(`  ok  wire-reference: wrote docs/reference/wire.md`);
  }
} else {
  const current = fs.existsSync(OUT) ? fs.readFileSync(OUT, 'utf8') : '';
  // ⚠ Compared with line endings NORMALISED: this repo checks out CRLF on Windows and the generator
  // writes LF, so a raw comparison fails on every machine for a reason that has nothing to do with the
  // wire. A gate that cries wolf on a checkout setting is a gate people learn to ignore.
  const norm = (s) => s.replace(/\r\n/g, '\n');
  if (norm(current) === norm(rendered)) {
    console.log('  ok  wire-reference: docs/reference/wire.md matches the source constants');
  } else {
    console.error('[31m✖ wire-reference: docs/reference/wire.md is STALE.[39m');
    console.error('  A wire constant changed and the generated reference did not. Regenerate:');
    console.error('    node devtools/scripts/wire-reference.mjs');
    process.exitCode = 1;
  }
}
