# Auxiliary browser sessions

> **A guide says HOW. The WHY lives in [DECISIONS.md](../DECISIONS.md) and is linked, never
> restated** — that is the rule D57 was written to keep.
> Migrating an existing app? Start at [ADOPTION.md](../ADOPTION.md).

## What this is, in one line

**Extra browsers your app drives, over the same WebView2 runtime the shell already ships** — off-screen
for scraping and rendering, on-screen for a human, or streamed as frames with synthetic input. Each runs
its own environment over its own profile, so none of them is your app's page and none of them can see
your app's cookies.

🔴 **The kit ships the MECHANICS and no scenario.** There is no login flow, no scraper, no co-browse
product in here — those are a product, not a mechanism (D21). The worked driver lives in the desktop
sample (`CookieLoginDriver`), and it is a plain consumer of the public seam: copy it, it is yours.

⚠ **Desktop only.** These rest on CDP and on WebView2 primitives neither mobile shell exposes
in-process. **Read D39 before writing a mobile port** — the trap is that one IS buildable behind the
same interface, and the decision records why the result would not be the same capability.

## Which one you want

| Type | It is | Reach for it when |
|---|---|---|
| `RenderSessionPool` → `RenderSession` | a bounded, leased pool of OFF-SCREEN sessions | you render or read many pages, in parallel, unattended |
| `InteractiveSession` | ONE real window over a persistent profile, driven by your code | a human must do something — sign in, solve a challenge — and your code needs what came out of it |
| `StreamingSession` | an off-screen session that emits FRAMES and accepts synthetic input | you are showing a remote page inside your own UI |

All three are constructed directly — **there is no `AddSessions()`**, because a session's lifetime is
yours rather than the container's. All three take a `SessionBrowserOptions` under `Browser`.

## The off-screen pool

```csharp
var pool = new RenderSessionPool(new RenderSessionPoolOptions
{
    Anchor  = form,                       // the UI thread everything marshals to
    Capacity = 3,                         // default
    Browser = new SessionBrowserOptions { ProfileDirectory = profileDir },
    // Your SSRF/allow-list policy. Null means "any http(s) URL", which is rarely what you want.
    NavigationGuard = async (uri, ct) => await MyPolicy.AllowsAsync(uri, ct),
});

await using var session = await pool.LeaseAsync(ct);
await session.NavigateAsync("https://example.test/thing", ct);
var html = await session.GetHtmlAsync(ct);
```

- **`await using`** — a lease returns to the pool on dispose. `RenderSession` is `IAsyncDisposable`
  ONLY, so it can never be a DI singleton (a container's synchronous `Dispose()` would throw).
- ⚠ **Dispose the POOL on the UI thread** — it is the one call here that does not marshal for you. A
  `FormClosed` handler, or before `Application.Run` returns; never a worker thread's container disposal.
- ⚠ **Returning a lease resets the DOM and JS, NOT the profile.** Cookies, `localStorage` and IndexedDB
  are shared across every lease of one pool, by design. **Separate trust domains need separate pools.**
- An operation that outruns `OpTimeout` (60 s) poisons that instance: it is discarded on return rather
  than re-pooled, because a session that stopped answering will not start.

## The interactive window

```csharp
var session = new InteractiveSession(new InteractiveSessionOptions
{
    Anchor  = form,
    Browser = new SessionBrowserOptions { ProfileDirectory = accountProfileDir },
    Title   = "Sign in",
});

var result = await session.RunAsync(async (controller, ct) =>
{
    await controller.NavigateAsync("https://example.test/login", ct);
    // …watch, wait, and return whatever you captured. Null means "nothing came of it".
    var cookies = await controller.GetCookiesAsync("https://example.test", ct);
    return Serialize(cookies);
});
```

- **One at a time.** A second concurrent `RunAsync` answers `SESSION_BUSY` rather than opening a second
  window, and `result.ThrowIfFailed()` turns any failure into a `ShenoraException` an IPC route can
  return as-is.
- **The user closing the window is a normal ending**, not a fault — the driver gets its final read
  before the close completes.
- 🔴 **Build the profile path with `InteractiveSession.ComposeProfileDirectory(root, segments)`.** A
  hand-built path is a security bug: Windows silently normalizes `"..."`, `".. ."` and `" . "` to the
  ROOT, and `"acct."`/`"acct "` to the same directory as `"acct"` — so one account's segment can land on
  another's cookie jar. Every obvious blocklist passes those; this method does not.
- 🔴 **`ClearProfile` RETURNS whether the tree is gone — check it when you are telling a user they
  signed out.** The common failure is a window that has not finished closing still holding the lock, and
  a silent false there recreates the exact incident the method exists to prevent.

## Streaming a page into your own UI

```csharp
var stream = await StreamingSession.StartAsync(new StreamingSessionOptions
{
    Anchor  = form,
    Browser = new SessionBrowserOptions { ProfileDirectory = profileDir },
    // 🔴 A dead renderer ends the session with nobody calling stop — dispose it HERE or the
    // off-screen window and its browser process outlive the app, holding the profile lock.
    OnEnded = ended => { if (ended.Reason is StreamingSessionEndedReason.RendererFailed) _ = stream!.DisposeAsync(); },
});

await foreach (var frame in stream.Frames.ReadAllAsync(ct)) Show(frame);   // latest-frame-wins
await stream.DispatchAsync(SessionInput.Click(x, y), ct);
```

⚠ **`DispatchAsync` is single-consumer: await each call before the next.** Overlapping calls reorder
press/move/release and transpose typed keys, which reads as a flaky page rather than as your bug.

## The five hooks — three of them prevent a WEDGE

An off-screen window has nobody to dismiss a modal. Left unhandled, WebView2 raises its OWN prompt and
the page stops **for good** — so the kit handles all three whether or not you supply a hook, and the
defaults are the safe answer to "nobody is watching".

| Hook | Default when unset | Supply one to |
|---|---|---|
| `OnScriptDialog` | **dismiss** | answer an `alert`/`confirm`/`prompt` |
| `OnAuthRequest` | **cancel** | supply basic-auth credentials |
| `OnCertificateRequest` | **cancel** | choose a client certificate (mutual TLS) |
| `OnWindowRequest` | **suppress** | allow a popup a legitimate flow needs |
| `OnPermissionRequest` | **deny** | grant clipboard, geolocation, … to your OWN page |

```csharp
new SessionBrowserOptions
{
    OnScriptDialog       = d => { d.Accept = true; d.ResultText = "42"; },
    OnAuthRequest        = c => { c.UserName = user; c.Password = secret; },
    OnCertificateRequest = r => r.SelectedIndex = 0,
}
```

- **A throwing hook degrades to the default rather than escaping** — these run inside a WebView2 event,
  where an escape is an unhandled UI-thread crash. ⚠ Note the safe direction differs: for the first
  three it keeps the page MOVING, for the last two it keeps REFUSING. A buggy policy must not become an
  open door.
- ⚠ **A handler that appears to do nothing is doing the load-bearing thing.** Two of these events have no
  `Handled` property — SUBSCRIBING is what suppresses WebView2's own dialog. Deleting one as dead code
  brings the wedge back.

## Watching what a session does — events, not taps

Every session publishes to the `IEventBus` you hand it, and **the scope is the session's `Id`**:

```csharp
bus.Subscribe(SessionEvents.Module, SessionEvents.NavigationCompleted, session.Id, e => { … });
```

Ten types: `NavigationStarting`, `NavigationCompleted`, `DomContentLoaded`, `SourceChanged`,
`TitleChanged`, `WebMessage`, `DownloadStarting`, `WindowCloseRequested`, `ProcessFailed`, and
`ResponseReceived`.

- **Scope it or you broadcast.** An unscoped emit reaches every subscriber, which is only correct when
  exactly one session exists.
- **`SourceChanged` is the SPA signal** — `history.pushState` raises no navigation event at all.
- **`ResponseReceived` is OFF until `ObserveResponse` selects a URL**, because it fires per subresource.
  It is also the honest primitive behind "tell me when a cookie changes": `CoreWebView2CookieManager`
  raises no events, so a response header is what you actually get. ⚠ It cannot see a cookie set by
  `document.cookie`.
- **`ProcessFailed` publishes EVERY kind**, including the routine self-healing ones (a GPU TDR, an
  unresponsive renderer). Only `RenderProcessExited`/`BrowserProcessExited` are terminal — treating them
  all as death discards a warm pool for a hiccup.

## Two guards, and only one of them holds

🔴 **`SessionBrowserOptions.RequestFilter` FAILS OPEN.** It is a sieve for breadth — a throwing filter
must not break page loading, so a broken one lets requests through (loudly: the first is reported).
**Never make it the only thing between a session and an internal host.**

What holds, both failing closed:
- **`NavigationGuard`**, consulted before every explicit `NavigateAsync`; and
- **the kit's own cross-origin cancellation**, which cancels an unvetted cross-authority main-frame hop
  — so a guard-approved URL answering `302 → 127.0.0.1:8080/admin` is not followed. It compares host
  AND port, and applies to the pool rather than to `InteractiveSession`, where an OAuth redirect
  legitimately crosses hosts.

⚠ Both are MAIN-FRAME only. An iframe is not covered by either.

## Serving your own bundle into a session

A session gets its own environment with **none** of the shell's serving set up — so navigating one to
your packaged origin renders WebView2's "can't reach this page". Pass the host's own two values
through; the same provider INSTANCE means the session's requests hit a cache the shell already warmed:

```csharp
Browser = new SessionBrowserOptions
{
    ProfileDirectory = …,
    VirtualHost      = hostOptions.VirtualHost,
    ResourceProvider = hostOptions.ResourceProvider,   // the SAME instance, not a second provider
}
```

Both or neither — either alone is refused at initialization, because on its own it serves nothing and
looks exactly like the bug it would be. **You only need any of this if you serve an EMBEDDED bundle**:
a server-backed app's pages are on a real loopback origin, which a session already reaches.

**D38 is the WHY**, including the two limits that decide whether this fits your case at all: a custom
or deferred SCHEME cannot work inside a session, and a bundle response's CORS header makes this safe
only on a session rendering your own pages. Read it before co-browsing anyone else's.

## If init times out

Almost always a leftover `msedgewebview2` process holding the profile lock. End the stray processes or
delete the folder; the message says so, and names the directory.
