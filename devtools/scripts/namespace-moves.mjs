// namespace-moves — where did a type GO between two releases, answered from the API baselines.
//
// 🔴 WHY THIS EXISTS (first adoption harvest, Yaorin 0.10.0 → 0.11.0). The release notes carry a
// PACKAGE-fold table — `Shenora.Core` → `Shenora` — and an adopter who applies it gets `using Shenora;`
// followed by one CS0246 per type, because the fold also re-namespaced within the package
// (`Shenora.Core.IEventBus` → `Shenora.Core.Events.IEventBus`). Each one is then a grep through the
// kit's source. Their words: a flat old-FQN → new-FQN list "would make this mechanical".
//
// ⚠ GENERATED, NEVER HAND-MAINTAINED — a hand-written move table is a second statement of every type's
// location, and D57 retired five design docs for exactly that. The baselines are already the release
// gate's own record of the surface, so this reads them and nothing else.
//
// THE MATCH IS BY SHORT NAME, which is what makes it useful and also what bounds it: a type that moved
// AND was renamed in one release cannot be matched here, and is reported as gone. That is honest — a
// rename is `retired-names.txt`'s job, and pretending otherwise would invent a mapping.
//
// Usage: node devtools/scripts/namespace-moves.mjs <from-ref> [to-ref]   (to-ref defaults to the worktree)
// 🔴 The baseline reading is SHARED (`api-baselines.mjs`) and this file's first version is why. It
// carried its own regex demanding a `class|interface|…` keyword, which the MetadataBaselines format
// does not have — so the mobile packages contributed ZERO types and the generated table silently had
// no Android or iOS row. A smaller answer is indistinguishable from a clean one.
import { typesAt } from './api-baselines.mjs';

const [from, to = null] = process.argv.slice(2);
if (!from) {
  console.error('usage: node devtools/scripts/namespace-moves.mjs <from-ref> [to-ref]');
  console.error('   e.g. node devtools/scripts/namespace-moves.mjs v0.10.0');
  process.exitCode = 1;
} else {
  const before = typesAt(from);
  const after = typesAt(to);
  if (before.size === 0) {
    console.error(`namespace-moves: no baseline types found at '${from}' — is it a valid ref?`);
    process.exitCode = 1;
  } else {
    const shortOf = (fqn) => fqn.slice(fqn.lastIndexOf('.') + 1);
    const afterByShort = new Map();
    for (const fqn of after) {
      const key = shortOf(fqn);
      if (!afterByShort.has(key)) afterByShort.set(key, []);
      afterByShort.get(key).push(fqn);
    }

    const moved = [];
    const gone = [];
    for (const fqn of [...before].sort()) {
      if (after.has(fqn)) continue;                       // still exactly where it was
      const candidates = afterByShort.get(shortOf(fqn)) ?? [];
      // Exactly one same-named survivor is a MOVE. Several is ambiguous, and saying so beats guessing.
      if (candidates.length === 1) moved.push([fqn, candidates[0]]);
      else if (candidates.length === 0) gone.push([fqn, null]);
      else moved.push([fqn, `${candidates.join('  OR  ')}   (ambiguous — several types share this name)`]);
    }

    const label = to ?? 'the working tree';
    console.log(`# Namespace moves — ${from} → ${label}\n`);
    console.log('Generated: `node devtools/dev.mjs namespace-moves ' + [from, to].filter(Boolean).join(' ')
      + '`. Matched by TYPE NAME, so a');
    console.log('type that moved AND was renamed shows as gone — that is `devtools/retired-names.txt`\'s half.\n');
    if (moved.length > 0) {
      console.log('| was | is |\n|---|---|');
      for (const [was, is] of moved) console.log(`| \`${was}\` | \`${is}\` |`);
      console.log('');
    } else {
      console.log('No type changed namespace.\n');
    }
    if (gone.length > 0) {
      console.log(`Gone from the public surface (renamed, made internal, or removed) — ${gone.length}:\n`);
      for (const [was] of gone) console.log(`- \`${was}\``);
      console.log('');
    }
    console.log(`${moved.length} moved, ${gone.length} gone, ${after.size} public type(s) at ${label}.`);
  }
}
