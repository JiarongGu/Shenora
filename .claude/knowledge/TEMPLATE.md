# <Title> — <one-line scope>

<One or two sentences: what invariant/gotcha this captures and where the foundation lives
(`src/server/.../X.cs`). Lead with the rule, not the backstory.>

## <The rule(s)>

- **<Hard rule in bold>** — <why it exists (the incident/measurement that earned it), then how to
  apply it. Prefer a concrete file/function reference over prose.>
- <Second point…>

## Gotchas / traps (optional)

- <The non-obvious failure mode and how to avoid it. If it was found live, say so — that's why it's a rule.>

<!--
Keep it short and specific — a rule earns its always-in-context cost only if it prevents a real
regression. Domain rules live in `.claude/knowledge/` (on-demand); only universal ones go in
`.claude/rules/` (--core). After creating: add a row to RULES_INDEX.md (the `knowledge new` tool
does this) and run `node devtools/dev.mjs knowledge check`.
-->
