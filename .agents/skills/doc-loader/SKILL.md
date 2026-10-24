---
name: doc-loader
description: Load the documents a task actually needs before touching code — the repository's own doc router plus every on-demand knowledge document whose "applies when" matches. Use at the START of any non-trivial task, because on-demand documents are not auto-loaded and an unread match is a missing contract.
---
<!-- daoris: core/core/skills/doc-loader/SKILL.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# doc-loader

The knowledge tier is deliberately **not** auto-loaded, so the context stays small. The cost of that
choice is this step: if you do not load what the task touches, you will miss an invariant that someone
already paid to learn.

## Steps

1. **The repository's own documents.** Find its documentation router — the table or index that maps a
   task to the one or two documents worth reading — and read only the entries that match. Bulk-loading
   defeats the purpose of a router.
2. **The generated index.** The always-loaded doctrine names the index of the on-demand tiers. Read it
   whole when it is a few dozen rows. When it is longer, search it more than once: the task's words,
   their synonyms, and the names of the folders and parts the task touches. Read every row the
   searches return. Where a search over this repository's knowledge is connected, ask it too, since it
   matches by meaning, which a word search cannot; where none is, the index searches are the whole
   step. Read every matched document. A search that finds nothing has not shown that nothing applies:
   the index is generated from what is on disk, so it is the exhaustive list, and any shortcut table
   elsewhere is a convenience, not the registry.
3. **Where things are.** If the brief names an index of where things are, start every search of the
   code there: open the row for what you need, then read the lines it names rather than the file.
   Search past it only for what it does not list.
4. **Private context.** If the task touches machine specifics, real paths, or another repository by
   name, read the untracked local notes rather than guessing.
5. **Report** in two to four lines: what you loaded, and the constraints it imposes here. If nothing
   matched, say so and proceed.

## Why

An unread match is indistinguishable from a rule that does not exist, right up until it is violated.
Reporting what you loaded makes that visible while it is still cheap to correct — and makes a *silent*
miss impossible to mistake for a considered decision.

Load only what the task touches. A step that routinely loads everything will be skipped, and then the
invariants go unread anyway.
