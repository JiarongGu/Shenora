# TASKS.md — pending backlog only

Pull the next item when between tasks. When an item is DONE: record it in `docs/ROADMAP.md`
`## Done` and REMOVE it from here — this file holds only what's still pending.
`> DIRECTION (user):` blockquotes capture the user's steering verbatim.

> DIRECTION (user, 2026-07-30): Shenora is the shared infrastructure library for ALL sibling
> projects — a "UI kit for non-web applications" in the headless sense: it holds the desktop
> shell that different applications boot their own logic on, and it must NOT depend on any UI
> component library. Purpose is to stop re-solving the same problems per project. In-scope
> common work explicitly includes: multi-form/multi-window, co-browsing (auxiliary browser
> sessions), drag-drop zones, the IPC package design, the event hub, frontend display
> optimizations, and the React hooks layer.
>
> DIRECTION (user, 2026-07-30, later): growth is harvest-driven — when something nice emerges
> while developing another application, it gets generalized and promoted into Shenora (common
> design/library/tool sharing). And the kit must be able to adopt MOBILE application logic too:
> Capacitor (and similar) shells speaking the same IPC envelope through a pluggable transport.

## TODO

### P1 — Skeleton tail

- [ ] **P1.1 — Local-feed consumption smoke.** `dev.mjs pack`, add `publish/packages` as a local
  NuGet source in a scratch consumer + `file:` install of the npm tarball; prove restore/install
  works and versions pin. Record the recipe in `docs/RELEASING.md` if anything differs.
- [ ] **P1.2 — Release workflow dry-run readiness.** Once a GitHub remote exists: run the Release
  workflow with `draft=true` against test feeds (or `--skip-duplicate` into a throwaway version)
  to validate OIDC config; document the nuget.org/npmjs.com trusted-publisher setup steps taken.

### Standing

- [ ] Keep `docs/ARCHITECTURE.md` + `docs/README.md` inventory in sync as pieces land.
- [ ] Add `.claude/knowledge/` rules as invariants are earned during extraction (UI-thread
  marshalling discipline, WebView2 gotchas, IPC batching numbers) — don't let them live only in
  code comments.
