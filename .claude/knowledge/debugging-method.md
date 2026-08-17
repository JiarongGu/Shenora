# Debugging an unexplained failure — suspect the harness, and count trials

**Applies when** a failure is intermittent, only appears under instrumentation, or has survived several
eliminations. **Not** for an ordinary reproducible bug: there, read the stack and fix it — counting trials
on something that fails 10/10 buys nothing.

🔴 **A/B THE HARNESS ITSELF — and count TRIALS, because an intermittent failure makes every single-run
elimination a coin flip.** A renderer crash survived ~12 single-run eliminations over two sessions (GPU,
profile, page, scheme, IPC bridge, pool, .NET hosts); it fires ~50 % of the time, so half those verdicts
were noise and each clean-looking answer moved the search on. Five alternated trials per arm settled it in
minutes — **0/12 without a new process group, 6/12 with one**. The kit was never at fault.

- **Measure WHEN before eliminating WHAT.** Timestamping was the cheapest experiment available and was run
  LAST. Two earlier attempts produced no output (shell pipe buffering) and were abandoned — **abandoning a
  broken instrument IS the error**, because every experiment after it answers a question nobody asked.
- **The tells that the harness, not the code, is the author:** the failure appears only under
  instrumentation; it never reproduces when a human runs the app; no single cause survives elimination; and
  the diagnostics contradict each other (no Windows Error Reporting event despite an access violation, and
  an empty `FailureSourceModulePath` — no faulting module).
- ⚠ **Do not stop at the first coherent story.** "The `timeout` KILL orphans the app and its teardown writes
  the crash" fitted every fact and was WRONG — the crash lands ~8 s in, long before any kill. Reading the
  log IN ORDER killed it. **A story that explains the facts is a hypothesis, not a finding.**
- 🔴 **INSTRUMENT BEFORE THEORISING, and let the numbers pick the hypothesis.** A converted soundtrack
  buffering 0.07 s produced two confident wrong causes (a decoder returning nothing; an encoder that
  packetises differently on hardware) and one tally killed both: `emitted=3` packets × 1024 / 44100 =
  0.0697 s matched the observed number exactly, which pointed at the CLOCK rather than the codec.
- ⚠ **A measurement attributes to your change only if nothing else moved.** The device reading after that
  fix looked like proof and was not — every run in it started at segment 0, where the bug cannot manifest.
  Confounded is not wrong; it is UNATTRIBUTED, and must be labelled so rather than banked.
