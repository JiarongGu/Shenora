---
name: pattern-finder
description: Find the existing exemplar to mirror for a task (a package's csproj, an options record, an envelope/dispatcher piece, a React hook, a test class, a devtools script) so new code matches the family's shape. Use before writing a new unit that has the same shape as something that already exists — in this repo, in a sibling source app, or in the family library template.
---

# pattern-finder

New code should read like the code around it — same registration, naming, error handling, and
cross-file wiring. Shenora is EXTRACTED, so the exemplar is often in a sibling repo: find it and
mirror it (with its hard-won comments) instead of inventing a shape.

## Steps

1. **Name the shape.** What are you adding? A packable csproj? an options record? an IPC
   envelope/middleware piece? a WinForms service? a React bridge hook? a unit test? a devtools
   command? a knowledge rule?
2. **Find exemplars** with Grep/Glob (not from memory), searching in this order:
   - **This repo** — once Shenora has one of the shape, mirror Shenora's own.
   - **The sibling sources** — `.claude/knowledge/extraction-sources.md` says which app proved
     which component; `local/EXTRACTION-MAP.md` (private) has the real paths. Port the proven
     file, keep its post-mortem comments, fix only the gaps listed there.
   - **The family library template (Lyntai)** — packaging, csproj/props shapes, release/devtools
     scripts, test patterns (API-surface baselines, contract-test bases). Real path in
     `local/CLAUDE.local.md`.
3. **Read one exemplar end-to-end**; extract its pattern (registration, naming, error handling,
   threading/marshalling discipline, the wiring chain across files).
4. **Report** the file(s) to mirror + the 3–5 conventions to follow, then implement in that shape.
