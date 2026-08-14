# The wire vocabulary

> **GENERATED — do not edit.** `node devtools/scripts/wire-reference.mjs`, gated by `dev.mjs verify`.
> Every value below is read from the source constants, so this page cannot drift from what ships.

These are the strings your PAGE types by hand. Everything else about the surface — types, methods,
parameters — your IDE already shows from the nupkg's XML docs, which is why there is no generated
API dump here. **WHY** any of it is shaped this way lives in [DECISIONS.md](../DECISIONS.md).

## Envelope categories

The `category` field on every message.

| Constant | Value | |
|---|---|---|
| `IpcCategories.Ipc` | `ipc` | A response to a client request (IpcResponse). |
| `IpcCategories.Notification` | `notification` | A host-pushed notification batch (IpcNotificationBatch). |

## Handshake

The host announces itself with this module + type when the page is ready.

| Constant | Value | |
|---|---|---|
| `IpcHostBridge.HandshakeModule` | `SHENORA` | Reserved wire route: the client's ready handshake module (mirrored by the client bridge, and pinned across the two languages by WireMirrorTests). |
| `IpcHostBridge.HandshakeType` | `READY` | Reserved wire route: the client's ready handshake type (mirrored by the client bridge). |

## Error codes

The `code` on an `IpcError`. Yours are your own; these are the kit's.

| Constant | Value | |
|---|---|---|
| `IpcErrorCodes.UnknownError` | `UNKNOWN_ERROR` | An unhandled (non-ShenoraException) exception reached the dispatch boundary. |
| `IpcErrorCodes.NoHandler` | `NO_HANDLER` | No MODULE claimed the request — nothing in the dispatch pipeline answers that name. |
| `IpcErrorCodes.NoRoute` | `NO_ROUTE` | The module answered but has no route of that TYPE. |
| `IpcErrorCodes.ScopeRequired` | `SCOPE_REQUIRED` | A scope-routed module was called without Scope. |
| `IpcErrorCodes.OperationCancelled` | `OPERATION_CANCELLED` | The operation was cancelled — a NORMAL outcome, not a fault. |
| `IpcErrorCodes.MissingPayloadValue` | `MISSING_PAYLOAD_VALUE` | A required payload value is absent or JSON null. |
| `IpcErrorCodes.InvalidPayloadValue` | `INVALID_PAYLOAD_VALUE` | A payload value could not convert to the requested type. |
| `IpcErrorCodes.CapabilityNotSupported` | `CAPABILITY_NOT_SUPPORTED` | The shell has NO EXPRESSION of what was asked for — not a fault, and not something a retry fixes. |

## Request-tracking events

Emitted as a long request starts, progresses and ends.

| Constant | Value | |
|---|---|---|
| `IpcRequestEvents.Updated` | `REQUEST_UPDATED` | A full IpcRequestStatus snapshot — every transition uses this ONE type, so folding is last-write-wins by id with no cross-type ordering hazard. |
| `IpcRequestEvents.Removed` | `REQUEST_REMOVED` | One or more request ids left the tracker with no corresponding Updated snapshot — history eviction and CLEAR_FINISHED. |

## Media player

One vocabulary, two directions — an EVENT drives the page's element, a REQUEST of the same name drives the host's player.

| Constant | Value | |
|---|---|---|
| `MediaPlayerEvents.Load` | `PLAYER_LOAD` | Point the element at a URL and prepare it, without playing: { uri, startAt } (seconds). |
| `MediaPlayerEvents.Play` | `PLAYER_PLAY` | Start or resume. |
| `MediaPlayerEvents.Pause` | `PLAYER_PAUSE` | Hold at the current position. |
| `MediaPlayerEvents.Seek` | `PLAYER_SEEK` | Move to an absolute position: { position } (seconds). |
| `MediaPlayerEvents.Rate` | `PLAYER_RATE` | Set the speed multiplier: { rate }. |
| `MediaPlayerEvents.Unload` | `PLAYER_UNLOAD` | Release the source — clear src and call load(), which is what frees the buffer. |

## Media player routes

Routes the page sends to the host.

| Constant | Value | |
|---|---|---|
| `MediaPlayerModule.ReportType` | `PLAYER_REPORT` | Route: the page describing what its element is doing. |
| `MediaPlayerModule.StatusType` | `PLAYER_STATUS` | Route: what is the host's player doing right now? No payload; answers like a drive command does. |

## Media conversion

A conversion outlives its request, so the page learns from these rather than from a response.

| Constant | Value | |
|---|---|---|
| `MediaConversionEvents.SourceProgress` | `SOURCE_PROGRESS` | Fraction complete: { source, progress }. |
| `MediaConversionEvents.Ready` | `READY` | The converted file is servable: { source }. |
| `MediaConversionEvents.Failed` | `FAILED` | Conversion failed: { source, reason }, plus dropped when reason is UnsupportedCodec. |

## Media conversion failures

The `reason` on a conversion FAILED event. Anything else is an exception TYPE name.

| Constant | Value | |
|---|---|---|
| `MediaConversionErrorCodes.UnsupportedCodec` | `UNSUPPORTED_CODEC` | The output would have lost a stream, so nothing was cached. |

## Shell capabilities

What a host advertises in its handshake, and what a page branches on instead of sniffing the platform.

| Constant | Value | |
|---|---|---|
| `ShellCapability.WindowChrome` | `windowChrome` | A frameless window whose chrome the page draws — minimize, maximize, drag, close. |
| `ShellCapability.DropZones` | `dropZones` | Native OS file drag-and-drop over page elements (`useDropZone`). |
| `ShellCapability.FilePicker` | `filePicker` | Picking a single file to read. |
| `ShellCapability.FolderPicker` | `folderPicker` | Picking a FOLDER — a desktop capability; see D35 before assuming it is portable. |
| `ShellCapability.SavePicker` | `savePicker` | Choosing a save destination. |
| `ShellCapability.SecondaryWindows` | `secondaryWindows` | Additional windows the app can open. |
| `ShellCapability.Tray` | `tray` | A tray icon. |
| `ShellCapability.LocalFiles` | `localFiles` | local content asks for this and falls back — to an external handler, or to hiding the control — rather than showing a player that can never load. |
