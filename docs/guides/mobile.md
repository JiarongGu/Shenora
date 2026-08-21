# Running on a phone — the MAUI shell

> **A guide says HOW. The WHY lives in [DECISIONS.md](../DECISIONS.md) and is linked, never
> restated** — that is the rule D57 was written to keep (five design docs were retired precisely
> because a third copy of the reasoning goes stale while nobody notices).
> Migrating an existing app? Start at [ADOPTION.md](../ADOPTION.md).

> **Migrating an existing app? This is [ADOPTION.md](../ADOPTION.md)'s Stage 5**, and it only pays off if
> Stage 4 happened — what it reuses is the portable assembly Stage 4 creates. Starting fresh? Write your
> app logic in a `net10.0` project from the outset and everything below applies with no stages at all.

## A MAUI shell, if your app logic should also run on a phone

**This is Stage 4's payoff, and it only works if Stage 4 happened.** The MAUI shell hosts the same
portable assembly the desktop shell does; if your IPC modules still reference `Shenora.Windows` there is
nothing to reuse. `samples/Shenora.Sample.Maui` is the worked example, and it references the very
same `Shenora.Sample.Logic` the desktop sample does — that shared reference is the whole demonstration.

**Status, stated plainly:** BOTH shells are built and proven on real hardware. Android ran on a device
(request/response, batched notifications, the error boundary, the native file picker, the mission
scheduler); iOS runs on an iPhone 17 Pro and its simulator, including media playback, the save picker,
the audio-conversion tier and Live Activities.
⚠ **iOS needs the `ios` workload and a Mac BUILD HOST** — that constraint is real and is the only thing
between an iOS app and a device; the kit's own iOS library builds on Windows (see the tree in
`docs/ARCHITECTURE.md`).

### Setup

```csharp
// MauiProgram.CreateMauiApp, AFTER builder.Build()
var shenora = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
{
    ApplicationName = "YourApp",
    // Android's private data directory IS the app root; --app-root is desktop packaging vocabulary.
    Paths = new ShenoraPathsOptions { ExplicitRoot = FileSystem.AppDataDirectory },
});
// UseAndroid in Shenora.Android, UseIOS in Shenora.iOS — same signature, named for the platform (D65).
shenora.UseAndroid(Dispatcher.GetForCurrentThread()!, ex => Log(ex.ToString()));
shenora.Services.AddIpcModule<YourPortableFacade>();
shenora.Services.UseMessageDispatcher();
var app = shenora.Build();
```

Then, on the page that owns the `HybridWebView`:

```csharp
var bridge = new MobileIpcBridge(webView, new MobileIpcBridgeOptions
{
    Dispatcher = app.Services.GetRequiredService<IMessageDispatcher>(),
    EventBus = app.Services.GetRequiredService<IEventBus>(),
});
bridge.Attach();          // construct early (buffering starts), attach before the page loads
```

**`UseAndroid`/`UseIOS` registers no `IShenoraRunner`, deliberately.** MAUI owns the loop, so
`ShenoraApplication.Run` — contractually "blocks until shutdown" — has no honest implementation.
Drive the pair from the platform instead: `Start()` from `Window.Created`, `Stop()` from
`Window.Destroying`. Both are idempotent, so wiring them somewhere that fires more than once
(an activity's `OnCreate`/`OnResume`) is safe.

🔴 **But GUARD the stop, or a configuration change looks like a shutdown.** Android destroys and
recreates the window for a change the manifest does not declare — a locale or font-scale change,
whatever `ConfigurationChanges` the app's `MainActivity` did not list. An unconditional `Stop()`
then cancels every in-flight request: measured on a device, a save whose picker was open came back
`OPERATION_CANCELLED` with the user's chosen file created and left empty.

```csharp
window.Destroying += (_, _) =>
{
    // The kit's answer to "is this teardown real?" — false on iOS, where a scene teardown is one.
    if (Shenora.Mobile.MobileWindowLifecycle.IsRecreating) return;
    shenora.Stop();
};
```

⚠ **In-flight work SURVIVES a recreation, and its response does not.** The mobile bridge sets
`IpcHostBridgeOptions.CancelInFlightOnDispose = false`, so a request the page started runs to
completion while the page that asked dies with the window. Write mobile handlers so the side effect
is what matters — the user's file gets written; nobody is left to hear the answer.

**The client needs no MAUI-specific code.** `@shenora/react`'s `ShenoraBridge` detects the host, so
`invoke`/`post` work unchanged from the desktop shell. ⚠ **The page MUST load MAUI's bridge script**
(`<script src="_framework/hybridwebview.js"></script>` on .NET 10). Without it `window.HybridWebView`
does not exist, and the failure is silent in the worst way: the page renders, the send throws a
`TypeError` nobody sees, and the host waits forever for a handshake.

⚠ **This is not only about the PACKAGED `index.html`, which is what it reads like.** A document your own
pipeline serves — a client-update bundle fetched from a server, a dev proxy — never passes through the
build step that injects the tag, so it arrives untagged and the same silent failure returns by a route a
build step cannot fix. It is worse there: with no bridge there is no handshake, so anything gated on the
page confirming itself (exactly what a safe client update is) can never confirm and rolls back for ever.
`MobileWebViewInterceptor` logs a warning the first time it serves a document with no tag in it — **inject
the tag at serve time.**

🔴 **And the interceptor must exist before the webview navigates.** Its constructor is where
`WebResourceRequested` is subscribed, so constructing it in `Loaded`/`OnAppearing` — the natural place,
because that is where DI services are reachable — is already too late: the platform serves the document
and every asset from `Resources/Raw/wwwroot`, and only late requests like the favicons ever reach your
routes. Nothing throws and `Use(…)` returns a live registration either way. Construct it in the page
CONSTRUCTOR, before `Content = webView`; the interceptor says so in the log when it detects the shape.

### What transfers, and what does not

| | |
|---|---|
| **Transfers unchanged** | The whole IPC substrate — envelopes, `MessageDispatcher`, `ModuleBase`, `IModuleContext`, request tracking (`IIpcRequestTracker`), `IEventBus`, batched notifications. Every `Shenora` contract. The mission scheduler and the file-update queue. |
| **Different implementation, same contract** | `IUrlLauncher`, `IFileDialogs`, `IUiDispatcher` — MAUI Essentials behind the same interfaces. ⚠ `IClipboardService` is NOT one of them: it goes to each platform's own pasteboard (`UIPasteboard` / `ClipData`), because Essentials' clipboard is text-only and that is an Essentials limit rather than a platform one. |
| **Transfers, INCLUDING seekable media** | **Resource serving.** `HybridWebView` has a request-interception seam in .NET 10 (`WebResourceRequested`, `e.Uri`, `e.Headers`, `e.Handled`), and the simple case needs none of it — put the built frontend in `Resources/Raw/wwwroot` and the platform serves it. What the seam buys is DYNAMIC content: a generated image, an exported file, **and seekable media**. ⚠ **Seeking needs no `e.PlatformArgs`** (measured on both devices — **D44**): `SetResponse` has a SECOND overload taking a header DICTIONARY, on both mobile TFMs, and every header reaches the native response. **But the two shells need OPPOSITE BODIES** for the same request — Android applies the `Range` start itself so you must NOT slice; iOS passes the body through so you MUST. **You do not write that yourself any more** (D45): `MobileWebViewInterceptor` implements the same `IWebViewInterceptor` the desktop does, so `interceptor.UseFiles(…)` and the page's `mediaUrl(…)` are literally the same code on all three shells and the delivery rule is read off the platform. Read D44 only if you are writing a middleware that answers ranges by hand; getting it wrong plays every faststart file perfectly and fails every other one. |
| **Absent, not different** | Native drop zones, tray, secondary windows, window state, frameless chrome. These are desktop CONCEPTS. You will not find them registered, and the mobile packages do not reference the packages that hold them — so portable logic cannot accidentally depend on one. |
| **The OS media transport** | `IPlaybackSession` — the lock screen, the media flyout, headphone and car-stereo buttons. One contract, three implementations, verified against each OS's own registry. `Publish` / `Report` / `Clear` go app → OS and `CommandReceived` comes back. ⚠ Two things to know. `Report` is for JUMPS, not a timer: all three platforms extrapolate the displayed time from a position plus a rate, so pushing it every 250 ms spends battery telling the OS what it already knows — and a *delayed* report lands as a jump backwards, because the platform treats it as current. And a session makes you CONTROLLABLE, not VISIBLE: Android needs a MediaStyle notification, iOS an active `AVAudioSession`, and both mean picking icons, channels, categories and interruption behaviour — your decisions, not the kit's. |
| **Live Activities / Dynamic Island (iOS)** | `ILiveActivities` — see the recipe below. Android registers an implementation that answers `Unavailable` with a reason rather than throwing, so portable logic asks and branches. |

**Where a contract is only partly honourable, it refuses LOUDLY** rather than doing nothing: the
folder picker, and a clipboard PICTURE **on Android only**, throw `ShellCapability.NotSupported`
naming the platform and the alternative. ⚠ The clipboard's split is per-platform and deliberate —
iOS carries any format, because `UIPasteboard` takes an arbitrary UTI; Android refuses everything
but text and HTML, because a picture there travels as a `content://` URI needing a `ContentProvider`
the ADOPTING APP declares in its own manifest, which the kit cannot supply on its behalf. So a page
that offers "copy image" should gate on the shell rather than assume either answer — the refusal is a
`ShellCapability.NotSupported` naming the format, which is a caught exception rather than a question
you can ask in advance. `IUiInteraction`'s
block/unblock is the opposite case — a documented no-op, because mobile pickers are already modal, so
the capability is satisfied BY the platform rather than absent.

### ⚠ A server-backed app on a MAUI shell: the page's ORIGIN is not what you expect, and it costs a day

Filed by the first adopter (2026-08-04) after losing a day to it. Not a kit defect and it needs no kit API —
but nothing said it, and **both failures present as the same useless symptom: a bare
`TypeError: Failed to fetch`**, with the engine logging the real reason only as a `[warning security]` line
you cannot see without attaching devtools.

`HybridWebView` serves your bundle from a synthetic virtual host, so the page is on a **secure origin you did
not choose**:

| Shell | The page's origin |
|---|---|
| Android | `https://0.0.0.1` |
| iOS | `app://0.0.0.1` |

Both measured on real runs. **The iOS one especially is worth having from us** — the adopter could not measure
it at all (`ios-webkit-debug-proxy` would not install on their Mac), and it is not otherwise discoverable.

Two consequences, and they bite in sequence:

1. **Mixed content.** Every request from that secure origin to a plain-`http` backend is blocked outright.
   On Android the app can allow it, and that is where the decision belongs — it is a real security
   relaxation and the kit will not make it silently on your behalf:

   ```csharp
   Microsoft.Maui.Handlers.HybridWebViewHandler.Mapper.AppendToMapping("MixedContent", (handler, view) =>
   {
   #if ANDROID
       handler.PlatformView.Settings.MixedContentMode =
           Android.Webkit.MixedContentHandling.AlwaysAllow;
   #endif
   });
   ```

   Prefer `https` on the backend if you can; this is the escape hatch, not the recommendation.

2. **CORS, which only appears after you fix (1).** The request now leaves the device and the *response* is
   withheld instead, because your backend has never heard of that origin. **Allowlist the origins above**
   server-side. ⚠ A non-standard scheme may present as `Origin: null` rather than the literal string, so
   allowlist by what your server actually logs rather than by what any doc predicts — check the header once
   and trust that.

**The related tooling gap, if you hit it:** WebKit does not forward the page's `console.*` to the device log,
so a page-side error can be genuinely invisible. Both this repo and the adopter independently ended up
routing page → host over IPC and logging host-side (`PageDiagModule` in `samples/Shenora.Sample.Maui`). It is
a few lines; copy the pattern.

⚠ **And do not ask the PAGE where its bytes came from.** `transferSize` is **0 for every intercepted
response** — it is what the Resource Timing API reports when no bytes crossed a network — so the reflex
check `performance.getEntriesByType('navigation')[0].transferSize === 0` reads as *"served from cache"*
and is wrong with total confidence. It sent one diagnosis a long way wrong and cost a `ClearCache` hook
that was written, shipped, then measured unnecessary and removed. The reliable question is what the
PIPELINE served, and only the host can answer it — log it there.

### The Dynamic Island for a PLAYER — what the kit gives you, and the four things you still write

**Use `IPlaybackSession`, not a Live Activity.** Two different iOS mechanisms reach the Island and they are
**mutually exclusive** — an app publishing a Now Playing session takes the Island, and a Live Activity
started beside it has nowhere to render. For playback, Now Playing is also the one Apple intends: it is
Apple's own presentation, it reaches CarPlay, the Watch, AirPods and car head units as well as the Island,
and a custom card duplicating it is the sort of duplication App Review pushes back on. Verified end to end
on an iPhone 17 Pro (2026-08-07).

**What the kit does:** `IPlaybackSession` on all three shells — `Publish`/`Report`/`Clear` out,
`CommandReceived` back, one contract, no platform code in your app logic.

**What you still write, and all four are small:**

1. **Artwork.** 🔴 *This is the one that decides whether the Island shows anything at all.* Set
   `PlaybackInfo.Artwork` (PNG/JPEG bytes). With a title and duration but no image, iOS knows something is
   playing, falls back to your app icon, and the Island is a wide bar with nothing in it — which reads
   exactly like "the feature is broken". It is the field most likely to be skipped, because it is the only
   one that is not a string.
2. **`UIBackgroundModes: [audio]`** in your `Platforms/iOS/Info.plist`. The kit cannot add it — no MSBuild
   item merges a key into your manifest. ⚠ Editing it does not reach an INCREMENTAL build either;
   `_WriteAppManifest` merges a stale `obj/**/AppManifest.plist`, so delete that or clean.
3. **An active `AVAudioSession`**, once at startup:
   ```csharp
   var session = AVFoundation.AVAudioSession.SharedInstance();
   session.SetCategory(AVFoundation.AVAudioSessionCategory.Playback,
                       AVFoundation.AVAudioSessionMode.Default, default);
   session.SetActive(true);
   ```
   The kit stays out of this deliberately: the category, whether you mix with other audio, and what happens
   on an interruption are product decisions. ⚠ **2 and 3 are a PAIR and neither does anything alone** —
   without the key iOS suspends your process; without the session it does not believe you are playing. The
   symptom of missing either is identical: plays in the foreground, silent after a swipe.
4. **A video→audio handoff, if you play VIDEO.** iOS pauses a `<video>` when the app backgrounds (the video
   track cannot render); an `<audio>` already playing continues. So on `visibilitychange` to hidden, copy
   the playhead onto an `<audio>` with the same source, start it, and pause the video — reversing it on the
   way back. ⚠ iOS restricts *starting* new playback once backgrounded, so if the handoff loses that race,
   keep the `<audio>` running muted alongside and just unmute it. `samples/Shenora.Sample.Maui`'s
   `wwwroot/index.html` has both the handoff and a standalone `♪` button, and the button matters as a
   DIAGNOSTIC: with only a `<video>`, a correctly-configured shell and a broken one look identical.

### Live Activities / the Dynamic Island

> ✅ **Verified on real hardware, 2026-08-09** (iPhone 17 Pro): the widget renders, and updates reach it —
> the card was watched moving 33 % → 66 % while the host logged each `activity update applied … state=active`.
>
> ⚠ **If you ever need to check the extension's entry point, read `entryoff` against the section map** —
> it must resolve into `__stubs`, not to `_main`. **"The binary has a normal `LC_MAIN`" proves nothing**,
> because *every* Mach-O executable has one, including Apple's own `.appex`es; that non-evidence once had
> this feature written off as broken on device for two days (D69).
>
> 🔴 **The real limitation, which is the platform's and not the kit's: updates come from YOUR APP PROCESS.**
> `ILiveActivities.Update` calls ActivityKit in-process, so when the app is swiped away or terminated the
> activity stays on screen frozen at its last value while your update loop is gone. That is normal iOS
> behaviour, not a defect — updating an activity without a running app is what ActivityKit's PUSH updates
> are for. **`ILiveActivities.PushToken(handle)` hands you that token**; sending to it is your server's job,
> as it is for any push. If your activity advances only while the app runs, none of this applies.
>
> ⚠ **The token is not available the instant `Start` returns** — iOS mints it asynchronously, so an
> immediate call answers null and that reads as a missing feature rather than a pending one. ⚠ And the
> PUSH-updated path is still **unproven on hardware** by this repo (`DECISIONS.md` D69): the token is
> exposed, the round trip is not measured.

The OS requires the UI to be a SwiftUI view in a widget extension. **You do not write it** — the kit ships a
generic widget that READS a description you give it in C# (D69), so the whole adoption is one MSBuild
property. Everything else is the package's: the state contract, the ActivityKit shim, the extension's
plist, the build, and the codesigning.

**1.** Turn it on:

```xml
<PropertyGroup Condition="$(TargetFramework.Contains('ios'))">
  <ShenoraLiveActivity>true</ShenoraLiveActivity>
</PropertyGroup>
```

**2.** Declare `NSSupportsLiveActivities` in your app's `Platforms/iOS/Info.plist`. The kit cannot add this
for you — no MSBuild item merges a key into that file — and without it `Activity.request` fails for a reason
that is not obvious.

**3.** Use it from portable C#:

```csharp
var state = new LiveActivityState { Title = "Converting", Subtitle = "starting" };
var handle = activities.Start(state);              // null if it could not start
if (handle is not null)
    activities.Update(handle, state = state with { Progress = 0.6 });
activities.End(handle!);
```

**Ask `Unavailable` FIRST.** It returns null when activities can be started and otherwise a reason — the OS
being too old, the user having switched them off, or the shim not being linked. Android returns a reason
always, so portable logic branches instead of catching.

#### Styling it, without a design system

**Start with a ready-made component.** One call gives you a complete, proportioned activity on every
surface — the metrics that are fiddly to get right and invisible when wrong are already settled:

```csharp
using Shenora.Modules.Platform.Activities;

var appearance = new LiveActivityAppearance { Symbol = "arrow.down.circle.fill", Tint = "#FF9500" };
var presentation = Components.ProgressCard(appearance.Symbol);

var handle = activities.Start(state, appearance, presentation);
```

`ProgressCard` (work with a known end) · `StatusCard` (unknown end — no percentage, because a number
nobody can compute is worse than none) · `CounterCard` (a single value that matters). Each returns an
ordinary `Presentation`, so `with` overrides any one surface and nothing is hidden from you.

**Then lay it out yourself when you need to.** `Layout` is the container — a `div`, with flexbox's two
axes. The short names are why these types live in their own namespace: a tree of six nodes should not say
"LiveActivity" eight times.

```csharp
var presentation = new Presentation
{
    Expanded = new Layout
    {
        Axis = Axis.Horizontal,
        Justify = Justify.SpaceBetween,   // along the axis
        Align = Align.Center,             // across it
        Children =
        [
            new Icon("arrow.down.circle.fill"),
            new Cutout(),                 // the sensor housing
            new Text("{progress}", TextRole.Value),
        ],
    },
};
```

- 🔴 **`Cutout` is how you lay out the Dynamic Island as ONE panel.** iOS hands the expanded
  presentation to the widget as three SEPARATE views and nothing drawn in one can cross into another — so
  no layout can literally span the housing. **The kit splits your panel for you:** children before the
  cutout render in the Island's leading view, children after it in the trailing view, everything else in
  the strip below. Outside the Island there is no housing, so a cutout is just flexible blank space.
  ⚠ **Keep it near the top** — the splitter looks at `Expanded` and its direct children, no deeper. Nested
  further it is not found, and the fallback is quiet: the whole panel renders in the bottom strip and the
  cutout becomes blank space. That is deliberate (a malformed layout must never stop the activity drawing),
  but it looks like the split did nothing, so it is worth knowing rather than discovering.
- **Five surfaces, each independent:** `LockScreen`, `Expanded`, `CompactLeading`, `CompactTrailing`,
  `Minimal`. **A surface you leave unset keeps the kit's own arrangement** — restyling the pill does not
  mean restating the card, and you can adopt one surface at a time. (*Surface*, not *region*: iOS spends
  "region" on the three sub-views it slices `Expanded` into, and the cutout bullet above is about those.)
- **Elements:** `Text`, `Icon`, `ProgressBar`, `Layout`, `Cutout`, `Spacer` — all in
  `Shenora.Modules.Platform.Activities`. That is the whole vocabulary, deliberately.
  `Text` and `Icon` take their content POSITIONALLY: `new Icon("bolt.fill")`,
  `new Text("{title}", TextRole.Headline)`.
- **`Justify`** (`Start` / `Center` / `End` / `SpaceBetween`) and **`Align`** (`Leading` / `Center` /
  `Trailing` / `Fill`) are `justify-content` and `align-items` — the same two axes you already use on the
  web, because that is what this kit's adopters write.
- **`{title}`, `{subtitle}` and `{progress}` are bound at every RENDER**, which is why a layout described
  once at `Start` keeps showing values that change. `{progress}` is empty when progress is null — a
  percentage for work of unknown length is a lie that looks like a stalled job.
- **A text names a ROLE, not a font** (`Headline` / `Body` / `Caption` / `Value`). That is D13 holding: the
  kit maps a role to the platform's own type scale, and a `Style` property would be the first brick of the
  design system it must not become.
- **When you describe a surface you own its insets completely** (`Layout.Insets`); the kit adds
  none. A margin you could not remove would be the worst possible default.
- ⚠ **There is no grid, and that is a decision rather than a gap.** At
  Island size a container with justify/align expresses what a grid would, and a grid's one unique power —
  columns agreeing ACROSS rows — had no design asking for it. If you hit that, say so; it is addable.
- **Raw SwiftUI still wins.** Point `ShenoraLiveActivityViews` at your own `.swift` file and the kit's
  generic widget is not compiled at all — see `samples/Shenora.Sample.Maui/Platforms/iOS/IslandViews.swift`
  for a worked override. Everything above is a default, never a ceiling.

⚠ **Traps, all measured:**
- **A `null` `Progress` means INDETERMINATE**, not 0. Render a spinner; an empty bar claims "0% done".
- **Never change `LiveActivityState` or the layout records without changing the Swift mirror in the same
  commit.** Drift fails SILENTLY — a renamed field decodes to nil, a renamed element kind renders as
  nothing, and a mismatched enum member falls back to the interpreter's default, which draws a
  plausible-looking WRONG layout. `LiveActivityMirrorTests` guards the kit's copy in both directions, and
  a committed GOLDEN payload is decoded by the real Swift decoder (`dev.mjs mac layout-check`) so the
  agreement is exercised rather than described; if you fork the Swift, keep both sides and keep the enums
  going over the wire as NAMES.
- **The compact pill DOES render on a modern simulator** (measured on iOS 26.3 / iPhone 17 Pro) — background
  the app first, or there is no Island to see. The **expanded** card needs a long press and the lock-screen
  banner needs a lock, so those two still cost a device. An older simulator reports only a lock-screen scene
  target and shows a permanently blank pill; do not read that as a broken widget.
  `node devtools/dev.mjs mac activity` shows what the OS actually registered, started and launched.
- **An active activity with the widget never launched** is the signature of a module-name mismatch between
  the shim and the extension — every call reports success and nothing renders. The kit sets
  `-module-name` on both sides for exactly this reason; do not override `ShenoraLiveActivityModule` on one.

**SAVING is universal, but only through `SaveAsync(options, write)`** — implemented natively on both
mobile shells since 2026-08-03 (`ACTION_CREATE_DOCUMENT`, `UIDocumentPickerViewController`). Call that,
not `SaveFileAsync`, which still refuses here because "give me a PATH to save to" has no mobile
expression: the user grants access to one document, the app writes into it, and there is nothing to hand
back. Three consequences an adopter should design around:

- **`FileDialogResult.FilePath` is null on SUCCESS.** Check `Success`, never the path — a page that
  treats the missing path as failure will report every mobile save as failed.
- **The write callback may run even if the user cancels.** Android asks first; iOS must produce the
  content first, because its export picker hands over a file that already exists. Do not put anything
  irreversible in the callback.
- **You get atomicity for free on every shell.** Both mobile implementations produce into a cache temp
  and only then hand it over, so an interrupted save leaves the user's previous document untouched — the
  same guarantee the desktop gets from `Files.BeginReplace`. That is the whole reason the shape is a
  callback rather than a path.
- 🔴 **Android needs ONE line in your `MainActivity`, and without it a recreated activity loses the
  picker's answer.** Forward activity results to the kit's relay before calling base:

  ```csharp
  protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
  {
      Shenora.Android.ActivityResultRelay.Deliver(requestCode, (int)resultCode, data);
      base.OnActivityResult(requestCode, resultCode, data);
  }
  ```

  The kit cannot do this for you, and the reason is measured rather than stylistic: a MAUI activity
  does not round-trip AndroidX instance state, so the registry mechanism that would need no wiring
  cannot survive the host being recreated while the picker is open (a locale or font-scale change, or
  an aggressive manifest). The framework's own routing can — but only your activity sees it.

### ⚠ AUTOPLAY DIFFERS BETWEEN THE SHELLS, and the kit does not level it

**Android requires a user gesture before an unmuted `play()`; iOS does not.** One page, two behaviours:

```
iOS      video plays
Android  play() REJECTED: NotAllowedError — play() can only be initiated by a user gesture
```

The media is fully loaded when this happens (`readyState=4`, and the frame geometry is already known), so
it does not look like a loading problem and there is nothing in the log unless you are watching for it.

**Write for it rather than around it: start playback from a real user gesture** — a tap handler, not
`useEffect`, not a `loadedmetadata` handler. That works identically on both shells and on the desktop, and
it is what the web platform asks for anyway.

🔴 **A REAL TAP IS ENOUGH — measured, because this was mistaken for a shell defect for a day.** On Android
with WebView 133, the same button that a script cannot start plays the clip UNMUTED when a genuine touch
drives it (`adb shell input tap`) and when trusted CDP input does. What fails is a *synthetic* click: an
injected `element.click()` is `isTrusted:false` and grants no user activation at all
(`navigator.userActivation.isActive` reads `false` immediately before AND after it), so Chromium refuses
— correctly. **Two consequences worth carrying:**
- **Muted playback needs no gesture on either shell**, which is why a probe that mutes the element passes
  on Android while the page's own unmuted button does not. If you autoplay, mute.
- ⚠ **A test harness cannot answer this question by clicking.** Ours reports `UI-PLAY: INCONCLUSIVE`
  rather than a failure now, for the same reason `CodecProbe` reports one: *a query that could not be
  performed must never be indistinguishable from a negative result.*

🔴 **Do not "fix" it with `MediaPlaybackRequiresUserGesture = false`.** That is Android's documented
answer and it **breaks media loading** in MAUI's `HybridWebView`: applied on the platform view, every clip
then failed with `MEDIA_ELEMENT_ERROR: Format error` and `readyState=0`. Measured and A/B'd 2026-08-09 —
with the setting `MEDIA: FAIL`, without it `MEDIA: PASS`. The kit wrote that knob, measured it, and
deleted it rather than ship an option whose effect is "your video stops loading".
⚠ **And it is not needed**, which is the part that took a day to establish: a real user was never blocked,
so the setting was answering a question the harness had invented. Two scoping notes if anyone reopens it:
that A/B ran on an AOSP WebView 110 emulator, and codec behaviour is exactly what a 20-versions-old
Chromium is *not* evidence about; and `err=4 / size=0x0 / readyState=0` is the signature of **bytes that
never arrived**, not of a missing codec — a deliberately broken media URL reproduces it exactly. So the
open question there is about SERVING, not formats.

### One web bundle, every shell — advertise capabilities, don't sniff the platform

The table above is the host's view. The page needs the same answer, and **it cannot work it out for
itself**: what a shell offers depends on what the APP composed, not on the operating system — a
desktop host that never registers `TrayIcon` has no tray either. So the host states it, in the ready
handshake it already answers:

```csharp
// wherever you build the bridge options — WebView2 or MAUI, the option has the same name
Shell = new ShellInfo
{
    Name = "winforms",                              // diagnostics only; never branch on it
    Capabilities = [ShellCapability.WindowChrome, ShellCapability.DropZones,
                    ShellCapability.FilePicker, ShellCapability.Tray],
},
```

```tsx
const shell = await bridge.notifyReady();           // also cached on bridge.shell afterwards
return <>
  {shell?.capabilities.includes(ShellCapabilities.windowChrome) && <TitleBar />}
  {shell?.capabilities.includes(ShellCapabilities.dropZones) ? <DropTarget /> : <PickFileButton />}
</>;
```

Both sides use the same names (`ShellCapability` in C#, `ShellCapabilities` in TS), pinned to each
other by a test — and an app may advertise its own strings beyond them.

**Treat absent as "assume nothing", never as "assume desktop".** `Shell` is optional, so a plain
browser tab during frontend dev, and any host that does not set it, both arrive as `undefined` — and
both are correctly capability-less. Branching the other way makes the *browser* the one place your
title bar renders wrongly.

Advertise what you actually composed. A capability you claim but did not register turns a rendered
button into a `NotSupported` throw at the moment a user presses it.

Both samples do this for real and disagree honestly: `Shenora.Sample.Desktop` answers `winforms` with
all seven, `Shenora.Sample.Maui` answers `maui` with `[filePicker]`, and `Shenora.Sample.Web`'s
`App.tsx` reads the reply without knowing which one it is talking to.

**`FileDialogOptions` survived contact with mobile, which was an open question until now.**
`OpenFileAsync` needs no change: `FileDialogResult.FilePath` is specified as "a path or URI the HOST
can resolve", and Android's content URI is exactly that. The desktop-only options
(`CheckFileExists`, `OverwritePrompt`, `DefaultPath`, `RememberPathKey`, …) are ignored, and which
ones is listed on the implementation.

### iOS

Everything above applies unchanged — that is the finding, not a hedge. The shell compiles for
`net10.0-ios` with **no platform directive anywhere in the package**, the iOS head is three template
files (`AppDelegate`, `Program`, `Info.plist`), and the same page got the same `ShellInfo` back.
**Getting it onto a simulator or a phone is `@shenora/cli`** (D67) — the part of the loop you would
otherwise write yourself:

```bash
npm i -D @shenora/cli
npx shenora init              # write shenora.deploy.json (project + bundleId)
npx shenora ios doctor        # can this Mac build, sign and install? names what is missing
npx shenora ios deploy --simulator
npx shenora ios deploy        # a real iPhone: build → SIGN → verify extensions → install → launch
```

iOS needs a Mac, so the TFM is conditioned on the build host and a Windows `pack` is android-only. ⚠ Two
things are yours and no tool can do them: a **free/personal team profile expires after 7 days**
(re-deploy to refresh), and a first install needs the certificate TRUSTED on the phone (Settings →
General → VPN & Device Management). If your machine's Xcode and the installed workload disagree, pass the
override after `--` (`npx shenora ios deploy --simulator -- -p:ValidateXcodeVersion=false`).

*(This repo's own loop is `node devtools/dev.mjs mac` — see `devtools/README.md`. That is a maintainer
tool and is not shipped; `@shenora/cli` is the adopter-facing half of the same work.)*

Two things that only showed up here, and both are about your PAGE rather than the kit:

- **Write the page for the SUPERSET of shells.** Markup that looked right on an Android emulator for
  a whole session put its heading under the status bar and the Dynamic Island on the first iPhone
  run. Use `env(safe-area-inset-*)` with `viewport-fit=cover`; both collapse to nothing where there
  are no insets.
  - 🔴 **…and on Android that is NOT enough, so the kit ships the missing half.** Measured on Android 16:
    `env()` reports the display CUTOUT only — **never the system bars** (`bottom` came back 0 on a device
    whose navigation bar is genuinely 24 CSS px) — and reports **0 for the whole first page load**. No
    page-side code can work around either; a re-read on `resize`/`visualViewport` was written and does
    nothing, because nothing changes within that document to observe.
  - **`MobileSafeArea` publishes the platform's real insets as CSS variables**, at first paint, from the
    host. Opt-in, and every part of it is individually declinable:

    ```csharp
    _safeArea = new MobileSafeArea(webView, new SafeAreaOptions
    {
        Default = new SafeAreaInsets(24, 0, 24, 0), // published BEFORE the platform reports, so the
                                                    // first screen is right instead of laid out at 0
        Color   = "#14161a",                        // painted behind the inset strips
        Settle  = TimeSpan.FromMilliseconds(180),   // the correction eases instead of snapping
        Splash  = true,                             // covers the page until the real numbers land
    }, log);
    ```

    Your page then reads `var(--sa-top)` / `--sa-right` / `--sa-bottom` / `--sa-left` (rename the prefix
    with `VariablePrefix`), keeping `env()` as the fallback for anything that opens it outside the shell:

    ```css
    body { padding: max(12px, var(--sa-top, env(safe-area-inset-top))) /* …and the other three */ }
    ```
  - ⚠ **Two page-side rules the variables do not fix, both measured:** inset padding on a **scrolling**
    `<body>` scrolls away, so make body a non-scrolling flex column and scroll a child; and use
    `max(12px, inset)` rather than `calc(12px + inset)`, which stacks two paddings and reserved 61 CSS px
    where the platform asked for 49. `samples/Shenora.Sample.Maui/.../index.html` does both.
- **Strings leak the shell you developed on.** A shared bundle means "hello from android" eventually
  appears in an iPhone screenshot.

### Traps this repo already paid for

- **`Application.Current` is null inside `CreateMauiApp`** — `builder.Build()` makes the MauiApp, not
  the Application. Use `Dispatcher.GetForCurrentThread()`.
- **The envelope's `timestamp` is a `DateTimeOffset`.** A JS client sending `Date.now()` has its
  request dropped at the boundary — correctly logged host-side and correctly invisible to the page.
  Send `new Date().toISOString()`; `@shenora/react` already does.
- **Match the ABI when deploying to an emulator.** Most are x86_64 while a default build may produce
  arm64 only, and the install fails `INSTALL_FAILED_NO_MATCHING_ABIS`, which reads like a packaging
  fault rather than the wrong architecture.
- **The ready gate can be opened but never closed on this shell.** `HybridWebView` exposes no
  document-lifecycle event, so a page reload simply re-handshakes (`Open` is idempotent). Bounded,
  and worth knowing before you rely on buffering semantics the WebView2 bridge has.

---
