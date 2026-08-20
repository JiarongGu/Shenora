# Remote work — the CLI's two transports

**The problem this subsystem exists for:** the adopter this kit targets is a .NET developer on **Windows**
shipping to iOS. Their Mac is on the LAN, not under their desk, and their phone cannot be connected *into*
at all. Both are "somewhere else", and until now `@shenora/cli` assumed everything was here — `ios.ts`
called `/bin/sh` and `fs.existsSync` in the same breath, which is only correct when the machine running the
CLI *is* the Mac.

Two kinds of elsewhere, and the direction of the connection is what separates them:

| | **Push** — a host | **Pull** — a device |
|---|---|---|
| Example | the LAN Mac | the iPhone running your app |
| Who connects | we connect out, over ssh | it connects in, by polling |
| Why | it has sshd; we have a key | **a webview cannot be dialled** — there is no port to open, no agent to install |
| Abstraction | `Target` (`remote/target.ts`) | the inspect service (`inspect/service.ts`) |

That asymmetry is the whole design. A phone's webview is not a server and never will be, so the only
channel that exists is one the page itself opens — which makes the device half a **queue the device drains**
rather than a connection anyone makes. Everything else follows from that: the cursor, the loopback split,
the re-announce on reconnect.

## `Target` — "run this, not necessarily here"

`remote/target.ts` defines one interface with two implementations. Every command in `ios.ts` goes through
it, so the same code drives a local Mac and a LAN one.

```
sh / probe          run a command there
exists / list / mtimeMs   ask about ITS filesystem, not ours
push / pull         move a file across
gui                 run something that needs a LOGIN SESSION (see below)
```

**`LocalTarget`** is today's behaviour: `/bin/sh`, `node:fs`, and `gui` is just `sh` because a developer
sitting at their Mac is already in an Aqua session.

**`SshTarget`** carries the four things a naive `ssh user@host <cmd>` gets wrong, each measured in this
family before it was written down:

- **A login shell.** `bash -lc`, not `bash -c`. Without the profile, a Homebrew or pkg-installed `dotnet`
  simply is not on PATH, and the failure reads as "the Mac has no .NET".
- **Single-quoted, never JSON-stringified.** ssh concatenates its argv and hands the result to the remote
  *login shell*, which expands `$VAR` before `bash -lc` ever runs. A double-quoted command has its
  variables expanded against the wrong (empty) environment and silently reads blank.
- **`set -o pipefail` on the REMOTE string.** `exec.ts` already applies this locally; a remote command is a
  second, separate string that needs it just as much. Without it a piped `install | tail` reports tail's
  status and a rejected install is announced as a success.
- **An 8 KB ceiling, BISECTED: 8185 bytes runs, 8195 does not.** A remote command past it is **silently
  truncated and can still exit 0** — the redirection falls off the end, so `base64 -d > file` prints the
  blob to stdout and reports success. `SshTarget` refuses over the limit and tells you to push a file
  instead. ⚠ The exact pair matters to anyone changing the batching: "roughly 8192" is not a figure you can
  compute a safe chunk size from, and the failure gives no signal to test against.

### `gui` — the one that is not an optimisation

🔴 **`codesign` cannot use a login-keychain key from an ssh session.** An ssh login is a different *audit
session*, so signing dies with `errSecInternalComponent` — proven in this family by signing a copy of
`/bin/echo`, which has nothing to do with any project and failed identically. This is the wall that stops
remote device builds, and there is no flag for it.

The way through is to ask the GUI session to run the command: `osascript` tells Terminal.app to run a
script, Terminal is already in the user's session, and the keychain opens. The cost is that **its output
cannot be streamed back** — the script is detached in another session — so completion is a marker file the
caller polls for.

⚠ **The script body must be a SUBSHELL `( … )`, never a brace group `{ … }`.** Every driver script sets
`-e`; inside a brace group that exits the whole remote shell on the first failure, so the line that writes
the completion marker never runs and a *failed* build is indistinguishable from a slow one until the poll
times out. Measured in the family: a build failed in four minutes and the watcher printed progress for
another sixteen.

Simulator builds sign ad-hoc and never meet any of this, which is why `--simulator` stays on plain ssh and
is much faster.

## The inspect service — server + client

`shenora inspect serve` starts a Node HTTP service on the dev machine. A device on the LAN opens its page,
and from then on the device polls for work and reports results. The operator drives it from another
terminal (`shenora inspect eval …`) or from the same page opened locally.

**It is a devtool you start, never a flag in a product binary.** It runs arbitrary JS in whatever page
polls it. Shipping that inside the app would put an eval endpoint in a release build, and a diagnostic
hosted inside the thing being diagnosed dies with it — the moment you most need it is when the app will not
boot.

### The trust split

The device half and the operator half live on the same port and are told apart **per request, by the peer's
address**, not by the bind address:

| Half | Routes | Open to the LAN? |
|---|---|---|
| Device | announce, poll for actions, post results | **yes** — the device being diagnosed is routinely the one that cannot authenticate |
| Operator | queue an action, read results, list devices, **run an ssh command** | **no** — loopback only |

The server binds `0.0.0.0` deliberately: the whole point is that a phone can reach it. The boundary is
`req.socket.remoteAddress` against the loopback set — a socket fact, never a header, so nothing a client
sends can move it. A privileged request from off-box gets **404**, not 403: an operator route should not
confirm it exists to anyone who cannot use it.

🔴 **The ssh route is on the operator half and that is load-bearing.** `POST /api/inspect/host` runs a command
on the configured Mac. Reachable from the LAN it would be a remote shell for anyone on the coffee-shop
wifi, so it is gated by the same loopback test as the rest of the operator half, and there is a test that
fails if that gate is ever removed.

### The cursor

Actions and results are append-only arrays with a monotonic `seq`. A poll asks for everything after the
`seq` it last saw; nothing is consumed. This buys two things a destructive queue cannot: a poll is
idempotent, so a dropped response costs a retry rather than an action, and two devices can be driven at
once without stealing each other's work.

⚠ **A first poll starts at the CURRENT head, not zero.** Otherwise a page opened an hour into a session
replays every action ever queued — on an eval queue that is actively harmful.

⚠ **The device re-announces on every reconnect EDGE, not once at load.** A page whose service restarted
under it keeps polling happily forever with the operator's copy of its report empty — which reads as "the
device reported nothing" when the truth is "it never got to report". Recording the disconnect is what
creates the edge to re-announce on, so the line that clears the connected flag is load-bearing rather than
bookkeeping.

## What is deliberately NOT here

- **No auth, no pairing.** Reachability is the trust boundary for the device half. This is a LAN devtool on
  your own network; a token would be typed into a phone keyboard once per session and would protect a
  channel whose privileged half is already loopback-only.
- **No NAT traversal or tunnel.** Same LAN, or it does not work — and it says so.
- **No streaming from `gui`.** Not a limitation to fix later: the detached session genuinely cannot pipe
  back. The marker file is the answer, not a placeholder for one.

## What is proven here, and what needs hardware

🔴 **The point of this split is that a change to the device loop cannot break silently.** Every defect
this subsystem has had lived in *which command went where, in what order* — not in whether the Mac obeyed
it. That half is assertable without a Mac, and `remote/flow.test.ts` asserts it against `FakeTarget`, a
scriptable stand-in that records how each command was dispatched.

| Proven by the suite, no hardware | Needs a Mac or a phone |
|---|---|
| a signing build goes through `gui`, a simulator build does not | whether `codesign` then succeeds |
| a local Mac never pays for the GUI hand-off | whether Terminal.app is reachable |
| `dotnet` is handed the PROJECT, never the checkout root | whether the project builds |
| freshness reads the whole bundle, and unreadable ≠ fresh | the real mtimes |
| a failed `gui` build PRINTS its log | the log's contents |
| extensions are checked before install, and a missing profile is named | whether the phone accepts it |
| `push` deletes what it previously sent, via a file and `xargs` | the bytes arriving |
| `provision` asks once per bundle id and verifies ON DISK | whether Apple issues the profile |

⚠ **A fake cannot tell you the last mile and must not pretend to.** These tests never claim a build
succeeds, a signature verifies, or an app launches. They claim the orchestration is right, which is where
every one of this subsystem's defects actually was — each found once, by hand, against real hardware, and
none of which would have been found twice.

## The codec probe the inspector does NOT carry

⚠ **A codec probe was deliberately left out of the inspector page.** Yaorin's version carried one and it paid
for itself immediately — run against headless Edge with `--disable-gpu` it reported `HEVC: ""` where the
same engine in a real WebView2 window answers `probably`, which is a real lesson: **a codec matrix is
CONTEXT-dependent and headless is not a proxy for the shipped surface.** But the list it probed encoded
that app's format decisions, and the kit is not a media library (D53). If it returns it should be a
DECLARED probe list the page is given, not a list the kit picks.

