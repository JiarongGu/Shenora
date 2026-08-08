# Probes and diagnostics — a probe is CODE, and a bad one accuses the kit

The sample's probes (`samples/Shenora.Sample.*`) are how behavioural claims get proven, so a defect in a
probe reads as a defect in the framework. Both rules below were earned by probes that reported a HEALTHY
subsystem as broken, and each cost a day.

## The rules

- 🔴 **A probe that reads OTHER PROCESSES' state must survive them — their failure is not your result.**
  The Windows playback probe walked every app's SMTC session and aborted on the first that threw
  (`0x80070015 ERROR_NOT_READY` — another app's session mid-transition), so the kit's own session was
  never reached and `WindowsPlaybackSession` was reported as never publishing. It had been publishing
  correctly the whole time: title, artist, album, `status=Playing`, ff/rw buttons, timeline. **Guard each
  iteration separately and keep going**; the enumeration is shared-machine state, not yours. Fixed
  2026-08-08 → `PLAYBACK SESSION: PASS`. Applies to any sweep over system-wide enumerations (media
  sessions, windows, devices, processes).

- 🔴 **A diagnostic that names an exception and nothing about it is WORSE than none — it reads as
  evidence.** The same failure was undiagnosable for a day because the output was `COMException: ` with an
  empty message: WinRT COMExceptions routinely carry none, so the probe printed a confident-looking line
  that contained zero information. **Print the HRESULT and the failing STEP** — doing so named the cause on
  the very first run afterwards. Before trusting a diagnostic, ask what it prints when the thing it watches
  is FINE, and what it prints when the failure is one it did not anticipate.

## Gotchas / traps

- ⚠ **"The probe says FAIL" is a hypothesis about the probe as much as about the kit.** Both incidents
  ended with the kit innocent — as did the renderer-crash hunt in `phase-workflow.md`, where the harness
  was the author. When a probe fails, the cheapest first move is to make it print MORE about its own
  execution, not to start eliminating suspects in the code under test.
- ⚠ A probe asserting an absence proves nothing until it has been seen to FAIL. See D63 in
  `docs/DECISIONS.md`: absent is indistinguishable from working, so a probe must supply a fake and assert
  it was USED.
