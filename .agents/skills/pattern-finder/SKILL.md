---
name: pattern-finder
description: Find the existing exemplar to mirror before writing a new unit that has the same shape as something already in the codebase — a module, a handler, a test, a migration, a tooling script. Use before writing new code, so it reads like the code around it instead of introducing a second convention.
---
<!-- daoris: core/core/skills/pattern-finder/SKILL.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# pattern-finder

New code should read like the code around it: same registration, naming, error handling, and wiring.
The exemplar is nearly always already there.

## Steps

1. **Name the shape.** Say what you are adding in the codebase's own vocabulary — not "a class" but the
   kind of unit it is. If you cannot name the shape, it is probably a new one, and that is a design
   decision rather than a copy.
2. **Find exemplars by searching, not from memory.** Search this repository first: a convention it
   already follows outranks any external reference. Only if nothing here has the shape, look to whatever
   proven source the repository's own documents point at.
3. **Read one exemplar end to end.** Skimming yields the surface and misses the wiring — the
   registration, the error path, the lifecycle, the threading discipline, the chain across files.
   **Keep the comments that explain a non-obvious choice**; they are usually the record of something
   that went wrong once.
4. **Report** the file to mirror and the three to five conventions it establishes, then implement in
   that shape.

## Why

Two conventions for the same thing is worse than either one alone: every later reader has to determine
which applies, and every later change has to be made twice. Divergence enters a codebase one reasonable
local decision at a time, which is exactly why the check belongs *before* the code is written rather
than in review.

If the exemplar is wrong, fixing it and following it is the right move. Working around it silently
leaves the next person to rediscover the same problem.
