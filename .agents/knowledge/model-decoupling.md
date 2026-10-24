---
name: model-decoupling
applies_when: building any feature that uses a language model, an embedding model, or any AI service
enforces: specify the feature without naming a model; select the provider by deployment; report which tier ran
---
<!-- daoris: core/core/knowledge/model-decoupling.md @ 0.0.1 — canonical; edit via `daoris upstream` -->

# The model is a deployment choice, not part of the feature

**Use models freely — and never let one into the definition of a feature.** Specify what the feature
does, take the provider through a seam, and let the **deployment** decide which model serves it.

This is not "avoid models". A feature may depend on one entirely. What it must not depend on is *which*.

## Why

**The right model differs by deployment, today.** The same capability running beside a developer's
session and running as a shared service has genuinely different constraints — one already has a model
in the room and cannot afford a network round trip, the other answers for throughput and cost. A single
hard-wired choice is wrong in at least one of them.

**And the models turn over faster than your project will.** What is best now will not be best in a
year. A feature welded to one ages at the speed of the fastest-moving part of the stack rather than its
own — and the parts of a codebase that encode hard-won judgement should turn over far more slowly than
the inference layer beneath them.

**The corollary is where the real bug lives.** Decoupled also means a feature must not become
*unavailable* because one provider is absent. The failure is specific and easy to miss: a feature built
model-first returns nothing without an endpoint, its author reports the missing endpoint as *blocked*,
and the blockage gets treated as an external constraint rather than as the design decision it was. The
dependency was never required by the problem — only by the implementation.

## How to apply

- **Write the feature's definition with no model in it.** If you cannot say what it does without naming
  one, the model has become part of the specification.
- **Select the provider at the composition root**, by deployment — not inside the feature, and not from
  a default that quietly assumes a particular runtime is installed.
- **Ask what the feature can still do when the provider is absent.** Usually more than expected: exact
  matching, structural comparison, counting, ranking by overlap. Ship that, and let the model raise the
  ceiling rather than being the floor.
- **Report which tier ran, on every result.** A reader who cannot tell why a category is empty will
  assume a bug and be right to. Disclosing only on an empty result is not enough — a caller *with*
  results reads them as complete.
- **A failure in the model half must not fail the whole.** Wrap it, carry the reason, continue with
  what works — and never swallow cancellation, which belongs to the caller.
- **Where a capability genuinely needs a model**, make its absence an explicit, informative message
  rather than silence or an error.
- **Give the model only what it is genuinely better at.** It is not better at exact comparison, at
  counting, or at anything with a deterministic answer — using it there is slower, costlier and less
  correct.
