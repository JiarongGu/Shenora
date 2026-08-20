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

The PAGE sends this module + type once it is ready; the host answers with its ShellInfo.

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
| `ShellCapability.ClipboardFiles` | `clipboardFiles` | A page needs this because no web API expresses it — and it is the one clipboard capability that genuinely differs by shell, since a phone's pasteboard has no file list at all. |
| `ShellCapability.LocalFiles` | `localFiles` | local content asks for this and falls back — to an external handler, or to hiding the control — rather than showing a player that can never load. |

## Clipboard routes

The page's access to the native clipboard — the capability D53 added for what React cannot reach.

| Constant | Value | |
|---|---|---|
| `ClipboardModule.Module` | `SHENORA.CLIPBOARD` | The module name this facade answers on. |
| `ClipboardModule.ReadType` | `READ` | Route: everything the clipboard is offering, no gesture required. |
| `ClipboardModule.WriteType` | `WRITE` | Route: replace the clipboard with one item. |
| `ClipboardModule.ClearType` | `CLEAR` | Route: leave the clipboard holding nothing. |

## File dialog routes

Native open/save pickers, including the mobile shells' own.

| Constant | Value | |
|---|---|---|
| `FileDialogModule.Module` | `SHENORA.DIALOGS` | The module name this facade answers on. |
| `FileDialogModule.OpenFileType` | `OPEN_FILE` | Route: pick an existing file. |
| `FileDialogModule.OpenFolderType` | `OPEN_FOLDER` | Route: pick a folder. |
| `FileDialogModule.SaveFileType` | `SAVE_FILE` | Route: pick a save destination and get the PATH back. |
| `FileDialogModule.SaveTextType` | `SAVE_TEXT` | Route: pick a destination AND write text to it, in one call — the PORTABLE save, working on every shell because the HOST does the writing. |

## Request-tracking routes

What a page calls to list, cancel or clear the long requests the events above report.

| Constant | Value | |
|---|---|---|
| `IpcRequestsModule.ListType` | `LIST` | Route: snapshot of known requests — in flight first, then retained history. |
| `IpcRequestsModule.CancelType` | `CANCEL` | Route: abort a request in flight by id — XMLHttpRequest.abort(). |
| `IpcRequestsModule.ClearFinishedType` | `CLEAR_FINISHED` | Route: drop retained finished history. |

## Window command routes

Frameless chrome drives the real window through these.

| Constant | Value | |
|---|---|---|
| `WindowCommandModule.Module` | `SHENORA.WINDOW` | The reserved module name (mirrored by the client's WindowCommands). |
| `WindowCommandModule.MinimizeType` | `MINIMIZE` | Route: minimize the window. |
| `WindowCommandModule.ToggleMaximizeType` | `TOGGLE_MAXIMIZE` | Route: maximize if restored, restore if maximized. |
| `WindowCommandModule.CloseType` | `CLOSE` | Route: close the window (the app's FormClosing logic still runs). |
| `WindowCommandModule.IsMaximizedType` | `IS_MAXIMIZED` | Route: is it maximized? Answers { maximized } — authoritative for the chrome's glyph, since a manual work-area maximize never shows in WindowState. |
| `WindowCommandModule.StartDragType` | `START_DRAG` | Route: begin an OS window-move loop (the page's header on mousedown). |
| `WindowCommandModule.StartResizeType` | `START_RESIZE` | Route: begin an OS resize loop: { edge } — top, topLeft or topRight. |
| `WindowCommandModule.SetThemeType` | `SET_THEME` | Route: { dark }. |
| `WindowCommandModule.SetCaptionButtonsType` | `SET_CAPTION_BUTTONS` | Route: { buttons }, the caption-button hit rectangles. |

## Drop zone routes

Registering the page regions a native file drop is matched against.

| Constant | Value | |
|---|---|---|
| `DropZoneModule.RegisterType` | `REGISTER` | Route: declare a zone at { zoneId, x, y, width, height } (page coordinates). |
| `DropZoneModule.UpdateType` | `UPDATE` | Route: move a zone to new bounds; same payload as RegisterType. |
| `DropZoneModule.UnregisterType` | `UNREGISTER` | Route: forget a zone: { zoneId }. |
| `DropZoneModule.ShowType` | `SHOW` | Route: raise the drop overlay over a zone: { zoneId }. |

## Drop zone events

What the host pushes back as a drag crosses a registered zone.

| Constant | Value | |
|---|---|---|
| `DropZoneManager.Module` | `SHENORA.DROPZONE` | The reserved module name (mirrored by the client's useDropZone). |
| `DropZoneManager.DragEnterEvent` | `DRAG_ENTER` | Event: the pointer entered a zone while dragging: { zoneId }. |
| `DropZoneManager.DragLeaveEvent` | `DRAG_LEAVE` | Event: the pointer left a zone, or the drag ended elsewhere: { zoneId }. |
| `DropZoneManager.FileDropEvent` | `FILE_DROP` | Event: files were dropped: { zoneId, files, position }. |

## Browser session events

What an auxiliary session publishes on the event bus — the 0.11.0 replacement for the deleted observation taps.

| Constant | Value | |
|---|---|---|
| `SessionEvents.Module` | `SHENORA.SESSION` | The module every session event is published under. |
| `SessionEvents.ResponseReceived` | `RESPONSE_RECEIVED` | A network response arrived — payload SessionResponse. |
| `SessionEvents.NavigationStarting` | `NAVIGATION_STARTING` | A top-level navigation began — payload SessionSource. |
| `SessionEvents.NavigationCompleted` | `NAVIGATION_COMPLETED` | A top-level navigation finished, successfully or not — payload SessionNavigationResult. |
| `SessionEvents.DomContentLoaded` | `DOM_CONTENT_LOADED` | The document exists and is parsed — payload SessionSource. |
| `SessionEvents.SourceChanged` | `SOURCE_CHANGED` | The address changed WITHOUT a navigation — payload SessionSource. |
| `SessionEvents.TitleChanged` | `TITLE_CHANGED` | The document title changed — payload SessionSource. |
| `SessionEvents.WebMessage` | `WEB_MESSAGE` | The page posted a message via chrome.webview.postMessage — payload SessionWebMessage. |
| `SessionEvents.DownloadStarting` | `DOWNLOAD_STARTING` | The page began a download — payload DownloadHit. |
| `SessionEvents.WindowCloseRequested` | `WINDOW_CLOSE_REQUESTED` | The page called window.close() — no payload. |
| `SessionEvents.ProcessFailed` | `PROCESS_FAILED` | A browser process died — payload SessionProcessReport. |

## Interactive session failures

The `code` when an interactive session cannot answer.

| Constant | Value | |
|---|---|---|
| `InteractiveSessionErrorCodes.Busy` | `SESSION_BUSY` | Another session is already open — interactive sessions serialize. |
| `InteractiveSessionErrorCodes.Cancelled` | `SESSION_CANCELLED` | The caller's token tripped, or the user closed before the driver captured. |
| `InteractiveSessionErrorCodes.Incomplete` | `SESSION_INCOMPLETE` | The driver finished without capturing anything (e.g. |
| `InteractiveSessionErrorCodes.Error` | `SESSION_ERROR` | The driver (or the window) threw — details stay in the host log. |
| `InteractiveSessionErrorCodes.Unavailable` | `SESSION_UNAVAILABLE` | The UI-thread anchor is gone (headless / teardown). |

## Clipboard media types

The keys of `ClipboardContent.Formats` the kit names itself; an app's own type is its own string.

| Constant | Value | |
|---|---|---|
| `ClipboardContent.PngImage` | `image/png` | PNG bytes — the interchange image format every platform and browser reads. |
| `ClipboardContent.Html` | `text/html` | UTF-8 HTML, for a paste that keeps its formatting. |

## Segment stream route shapes

The reserved path segment a page uses to name a source by its registered handle.

| Constant | Value | |
|---|---|---|
| `SegmentStreamOptions.RemotePrefix` | `~remote/` | The path segment that means "an issued handle follows": {RoutePath}~remote/{handle}/{resource}. |
