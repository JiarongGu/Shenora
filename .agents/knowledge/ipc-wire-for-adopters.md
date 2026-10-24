---
name: ipc-wire-for-adopters
applies_when: a neighbouring app's page or host talks to Shenora over IPC — what a request, response, error and event look like, which names are promised, how errors and batching behave, and which transport carries them on each shell
enforces: the envelope shapes and the SHENORA.* names are the promised contract, with docs/reference/wire.md as the generated list; no raw exception text crosses the wire; events always arrive batched
---

# The IPC wire, as an adopter sees it

**`docs/reference/wire.md` is the authority for every name a page types by hand** — module names, route and
event types, error codes, capabilities. It is generated from the source constants by
`devtools/scripts/wire-reference.mjs` and `verify` fails when it drifts, so read it rather than any copy.
This document says what the shapes are, which parts are promised, and where each lives. The invariants
for changing the stack from inside this repository are `ipc-contracts`.

## Why

A page and a host written in two languages meet only through strings, and no IDE checks a string across
that boundary. A neighbour that copies a shape from memory, or relies on something incidental, breaks
silently when the kit moves.

## The shapes (JSON, camelCase, nulls omitted, enums as camelCase strings — `IpcJson`)

| Message | Shape | C# | TS (`src/Shenora.React/src/types.ts`) |
|---|---|---|---|
| request, page → host | `{ id, module, type, scope?, payload?, timestamp }` | `src/Shenora/Core/Ipc/IpcRequest.cs`, read by `IpcWire.ReadRequest` (case-insensitive names, unknown names ignored) | `IpcRequest` |
| response, host → page | `{ category: "ipc", id, success, data?, error? }` | `IpcResponse.cs` | `IpcResponse` |
| error | `{ code, message?, parameters? }`; `code` doubles as an i18n key | `IpcError.cs` | `IpcError` |
| events, host → page | `{ category: "notification", id, payload: [ { module, type, payload?, scope? } … ], timestamp }` | `IpcNotificationBatch.cs`, `IpcNotification.cs` | `IpcNotificationBatch` |
| handshake | request `SHENORA` / `READY`, answered with `ShellInfo { name, capabilities[] }` | `IpcHostBridge.HandshakeModule`/`HandshakeType`, `ShellInfo.cs`, `Core/Shell/ShellCapability.cs` | `HANDSHAKE_MODULE`, `ShellInfo` |

The C# and TS halves are pinned to each other by `tests/Shenora.Tests/Ipc/WireMirrorTests.cs`.

## What is promised

- **The shapes above and every name in `docs/reference/wire.md`** — a change is a public change, recorded
  under `### Breaking` in `CHANGELOG.md`.
- **Routes are a module and a type, both SCREAMING_SNAKE; the `SHENORA.` module prefix is the kit's.** An
  app's own modules take any other name; two modules claiming one name throw at composition.
- **No raw exception text crosses the wire** (`IpcErrorMapping.ToError`): a `ShenoraException` passes its
  code, parameters and message; a cancellation becomes `OPERATION_CANCELLED`; anything else becomes
  `UNKNOWN_ERROR` carrying only `parameters.exceptionType`. The host's dispatch never throws
  (`IpcHostBridge.HandleIncomingAsync`).
- **Error codes** (`IpcErrorCodes`): `UNKNOWN_ERROR`, `NO_HANDLER`, `NO_ROUTE`, `SCOPE_REQUIRED`,
  `OPERATION_CANCELLED`, `MISSING_PAYLOAD_VALUE`, `INVALID_PAYLOAD_VALUE`, `CAPABILITY_NOT_SUPPORTED`; the
  page adds `TIMEOUT` and `NO_TRANSPORT`, which never come from the host (`ClientOnlyIpcErrorCodes`).
- **Events always arrive batched**, and none is delivered before the page's `READY`: until then they are
  queued (`NotificationPump`, `MaxQueued` 10,000, oldest dropped first).

## What is incidental — do not build on it

- **The 50 ms batching interval** — `NotificationPumpOptions.FlushInterval` and each bridge's
  `NotificationInterval` are options an app may change.
- **The page's 30 s default request timeout** — `ShenoraBridge.defaultTimeoutMs` in
  `src/Shenora.React/src/bridge.ts`, configurable through `configureBridge`.
- **Which transport carries the bytes.** `createHostTransport()` (`src/Shenora.React/src/transport.ts`)
  takes the first present: WebView2 `chrome.webview` postMessage (`src/Shenora.Windows/WebView/WebViewIpcBridge.cs`),
  MAUI `HybridWebView` raw messages (`src/Shenora.Mobile/Ipc/MobileIpcBridge.cs`), or a same-origin POST to
  `/__shenora/ipc` on Chromium (`ChromiumTransport.DefaultIpcPath`). A page that uses `@shenora/react` never
  sees the difference.
- **The page's origin.** Pages are served at `https://{VirtualHost}/`; `app.local` is the Chromium default
  (`ChromiumHostOptions`), while WebView2's `WebViewHostOptions.VirtualHost` has none and is the app's.

## Not confirmed

- **There is no protocol version field** in any envelope (searched `src/Shenora/Core/Ipc/` and the TS
  sources): a page and a host are expected to ship the same kit version. Whether a mismatch is detected
  anywhere else was not found in the code.
- **The MAUI page's origin** is MAUI's own and the kit does not set it; the Android and iOS values are
  measured in `docs/design/mobile-shells.md`, not asserted by code here.
