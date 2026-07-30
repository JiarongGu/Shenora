using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>A CSS viewport the co-browse page emulates (device metrics, DPI-independent).</summary>
public readonly record struct CoBrowseViewport(int Width, int Height, double DeviceScaleFactor);

/// <summary>Inputs for <see cref="CoBrowseSession"/>.</summary>
public sealed class CoBrowseSessionOptions
{
    /// <summary>A live UI-thread control (typically the main window) browser work marshals onto.</summary>
    public required Control Anchor { get; init; }

    /// <summary>
    /// Browser configuration — same scoping rule as <see cref="LoginWindowOptions.ProfileDirectory"/>:
    /// one profile per (provider, sub-account), NEVER one shared jar (the source's measured leak:
    /// definitions sharing a profile could read back each other's sessions). Set
    /// <see cref="SessionBrowserOptions.KeepAliveInBackground"/> — the page renders off-screen and
    /// must keep painting/animating for the screencast; wire ad/tracker stripping through
    /// <see cref="SessionBrowserOptions.RequestFilter"/> (the page is STREAMED — a clean window is
    /// bandwidth AND UX).
    /// </summary>
    public required SessionBrowserOptions Browser { get; init; }
    /// <summary>
    /// Diagnostics. Null = silent. The sessions package shipped with NO logging of any kind against
    /// ~30 swallowed catches, so a wedged co-browse session was undiagnosable in production (P5.5 H4.7). Note the
    /// browser-level events (init failure, suppressed popups, denied permissions, a dead renderer)
    /// report through <see cref="SessionBrowserOptions.Log"/> on <see cref="Browser"/>.
    /// </summary>
    public Microsoft.Extensions.Logging.ILogger? Log { get; init; }


    /// <summary>
    /// Consulted before every controller navigation (return false to refuse) — the same
    /// SSRF-shaped seam as the pool and the login window: co-browse URLs are data-driven, and
    /// this session both DISCLOSES the rendered page (streamed frames) and accepts input.
    /// </summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>Screencast JPEG quality (1–100).</summary>
    public int JpegQuality { get; init; } = 72;

    /// <summary>Max captured frame width — generous so a client-mirrored viewport
    /// (up to ~1200 CSS × dpr 2) is captured crisp.</summary>
    public int MaxFrameWidth { get; init; } = 2560;

    /// <summary>Max captured frame height (see <see cref="MaxFrameWidth"/>).</summary>
    public int MaxFrameHeight { get; init; } = 1800;

    /// <summary>
    /// FALLBACK viewport: a sane desktop CSS viewport decoupled from the host's DPI, used only
    /// until the client's own <c>viewport</c> input message arrives (which then MIRRORS the
    /// client's display box 1:1 — see <see cref="CoBrowseSession.DispatchInputAsync"/>).
    /// deviceScaleFactor 1.5 keeps it crisp regardless of host DPI.
    /// </summary>
    public CoBrowseViewport InitialViewport { get; init; } = new(1280, 860, 1.5);

    /// <summary>
    /// Frames buffered between the capture (UI thread) and the app's transport pump — latest-
    /// frame-wins: a slow client never backs up the compositor, the oldest frame is dropped.
    /// </summary>
    public int FrameBuffer { get; init; } = 2;
}

/// <summary>
/// CO-BROWSE an off-screen page, ported from the server-backed sibling: CDP
/// <c>Page.startScreencast</c> emits JPEG frames into <see cref="Frames"/>, and the client's
/// input JSON is dispatched back into the page via <see cref="DispatchInputAsync"/> — so a user
/// clears a countdown/verification IN-APP without a native window, human-solved by design. The
/// TRANSPORT is the app's (WebSocket, bridge, anything): pump <see cref="Frames"/> out (binary)
/// and feed input text back — the wire protocol is kept identical to the source so adoption is
/// mechanical. <see cref="Controller"/> is the SAME primitive set the login window drives (as a
/// BACKGROUND controller — its window-managing calls are inert), so a driver's navigate/script/
/// cookie-capture hooks run identically over the stream. Dispose with the flow (stops the
/// screencast, closes the hidden window).
///
/// ORDERING: <see cref="DispatchInputAsync"/> is a SINGLE-CONSUMER contract — the caller must
/// await each call before the next (the source's transport pump does exactly this). Input is
/// stateful (a held mouse button, the current viewport), so overlapping calls could reorder a
/// press/move/release or transpose typed keys.
/// </summary>
public sealed class CoBrowseSession : IAsyncDisposable
{
    private readonly Form _form;
    private readonly WebView2Control _web;
    private readonly Channel<byte[]> _frames;
    private int _disposed;
    // UI-thread-only state (mutated inside marshalled bodies; safe under the single-consumer
    // input contract): the current emulated viewport (so we never round-trip to read it) and
    // whether the left button is held (so drags emit buttons:1 on move, not 0).
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _buttonDown;

    private CoBrowseSession(Form form, WebView2Control web, Channel<byte[]> frames, LoginWindowController controller, CoBrowseViewport initial)
    {
        _form = form;
        _web = web;
        _frames = frames;
        Controller = controller;
        (_viewportWidth, _viewportHeight) = ClampViewport(initial.Width, initial.Height);
    }

    /// <summary>JPEG frames, newest last — a bounded latest-wins buffer (drop-oldest). Pump these
    /// to the client as binary messages; the reader completes when the session is disposed.</summary>
    public ChannelReader<byte[]> Frames => _frames.Reader;

    /// <summary>
    /// The driver primitives over the streamed page — navigate (guarded), script, origin-scoped
    /// cookies, message/download/new-window/navigation taps. Deliberately the SAME controller the
    /// login window runs, so one driver serves both shapes; here it is a BACKGROUND controller,
    /// so its window-managing calls (Reveal/FitToBox, hold-close) are inert.
    /// </summary>
    public LoginWindowController Controller { get; }

    /// <summary>
    /// Create the off-screen browser and start the screencast, on the anchor's UI thread. No
    /// navigation happens here — the caller's driver navigates via <see cref="Controller"/>.
    /// </summary>
    public static Task<CoBrowseSession> StartAsync(CoBrowseSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.JpegQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), "JpegQuality must be 1-100.");
        if (options.MaxFrameWidth < 1 || options.MaxFrameHeight < 1) throw new ArgumentOutOfRangeException(nameof(options), "Max frame size must be positive.");
        if (options.FrameBuffer < 1) throw new ArgumentOutOfRangeException(nameof(options), "FrameBuffer must be at least 1.");

        // Latest-frame-wins: a slow client never backs up the compositor — only the newest
        // frames are kept.
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.FrameBuffer)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

        var tcs = new TaskCompletionSource<CoBrowseSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            options.Anchor.BeginInvoke(new Action(async () =>
            {
                Form? form = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested) { frames.Writer.TryComplete(); tcs.TrySetCanceled(cancellationToken); return; }
                    // A generous FIXED physical surface — big enough that any client-mirrored
                    // viewport fits without clipping. The real CSS viewport is driven purely by
                    // Emulation.setDeviceMetricsOverride (below + the "viewport" input case), which
                    // is DPI-independent, so this physical size must NOT track the box.
                    form = OffscreenWindow.Create("Co-browse session", new Size(1600, 1100));
                    var web = new WebView2Control { Dock = DockStyle.Fill };
                    form.Controls.Add(web);
                    // A dead renderer must COMPLETE the frame channel (P5.5 H4.4). Without this the
                    // screencast simply stops and the app's `await foreach` over Frames waits forever
                    // for a stream that can never resume — the consumer cannot tell a crashed session
                    // from a quiet one, which is exactly the missing-lifecycle-hook problem D21 names.
                    await SessionBrowser.InitializeAsync(web, options.Browser,
                        onProcessFailed: _ => frames.Writer.TryComplete()).ConfigureAwait(true);

                    var core = web.CoreWebView2;
                    var receiver = core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
                    receiver.DevToolsProtocolEventReceived += (_, e) =>
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                            var sid = doc.RootElement.GetProperty("sessionId").GetInt32();
                            var data = doc.RootElement.GetProperty("data").GetString();
                            if (!string.IsNullOrEmpty(data)) frames.Writer.TryWrite(Convert.FromBase64String(data));
                            _ = core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{sid}}}");
                        }
                        catch { /* one bad frame shouldn't sink the stream */ }
                    };
                    // Reuse the SAME controller the login window uses (a BACKGROUND one — no
                    // hold-close, no reveal), so a driver's capture hooks run identically over
                    // the stream without the off-screen host ever vetoing app shutdown.
                    var controller = new LoginWindowController(form, web, options.NavigationGuard, onLoading: null, foreground: false);
                    await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}").ConfigureAwait(true);
                    var vp = options.InitialViewport;
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                        BuildMetricsOverrideJson(vp.Width, vp.Height, vp.DeviceScaleFactor)).ConfigureAwait(true);
                    // Bandwidth: CDP screencast only emits a frame when the page VISUALLY CHANGES
                    // (a settled page → ~nothing), so it's naturally idle-cheap. everyNthFrame:1
                    // streams every changed frame (smoother typing/cursor/verification animation)
                    // rather than halving the rate; the event-driven nature keeps idle bandwidth
                    // ~0. If a busy page ever makes this too heavy, the next step is a real video
                    // codec (H.264/WebRTC) over JPEG frames.
                    await core.CallDevToolsProtocolMethodAsync("Page.startScreencast",
                        string.Create(CultureInfo.InvariantCulture,
                            $"{{\"format\":\"jpeg\",\"quality\":{options.JpegQuality},\"maxWidth\":{options.MaxFrameWidth},\"maxHeight\":{options.MaxFrameHeight},\"everyNthFrame\":1}}")).ConfigureAwait(true);

                    tcs.TrySetResult(new CoBrowseSession(form, web, frames, controller, options.InitialViewport));
                }
                catch (Exception ex)
                {
                    try { form?.Dispose(); } catch { }
                    frames.Writer.TryComplete();
                    tcs.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex)
        {
            // The anchor's handle isn't created / is gone — surface it through the task instead of
            // synchronously out of a Task-returning API, and don't leave the reader hanging.
            frames.Writer.TryComplete();
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    /// <summary>
    /// Dispatch ONE client input message (JSON text) into the page — the source protocol,
    /// verbatim for mechanical adoption. Coordinates arrive as FRACTIONS (0..1) of the frame and
    /// map to CSS px via the current emulated viewport (which the session set — no round-trip).
    /// Types: <c>viewport</c> {width,height,dpr?} (bi-directional 1:1 — the client sends its
    /// content box + pixel ratio and the page emulates EXACTLY that via device metrics ALONE,
    /// never a physical resize, which would desync the CSS layout), <c>mouse</c> {event,fx,fy}
    /// (a held button carries through moves so drags work), <c>wheel</c> {fx,fy,dy},
    /// <c>text</c> {text} (plain typing → insertText), <c>key</c> {key,alt?,ctrl?,meta?,shift?}
    /// (special keys/shortcuts as real key events). A malformed message is swallowed — one bad
    /// input can't kill the session. SINGLE-CONSUMER: await each call before the next (see the
    /// class doc).
    /// </summary>
    public Task DispatchInputAsync(string json)
    {
        if (_disposed != 0 || _form.IsDisposed || string.IsNullOrEmpty(json)) return Task.CompletedTask;
        return RunOnUiAsync(async () =>
        {
            var core = TryGetCore();
            if (core is null) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "viewport":
                {
                    var (w, h) = ClampViewport(root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble());
                    _viewportWidth = w;
                    _viewportHeight = h; // cache so mouse/wheel need no innerWidth round-trip
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                        BuildMetricsOverrideJson(w, h,
                            root.TryGetProperty("dpr", out var dp) ? dp.GetDouble() : null)).ConfigureAwait(true);
                    break;
                }
                case "mouse":
                {
                    var ev = root.GetProperty("event").GetString() ?? "";
                    if (ev == "pressed") _buttonDown = true;
                    else if (ev == "released") _buttonDown = false;
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        BuildMouseEventJson(ev, root.GetProperty("fx").GetDouble(), root.GetProperty("fy").GetDouble(),
                            _viewportWidth, _viewportHeight, _buttonDown)).ConfigureAwait(true);
                    break;
                }
                case "wheel":
                {
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        BuildWheelEventJson(root.GetProperty("fx").GetDouble(), root.GetProperty("fy").GetDouble(),
                            root.GetProperty("dy").GetDouble(), _viewportWidth, _viewportHeight)).ConfigureAwait(true);
                    break;
                }
                case "text":
                {
                    await core.CallDevToolsProtocolMethodAsync("Input.insertText",
                        JsonSerializer.Serialize(new { text = root.GetProperty("text").GetString() ?? "" })).ConfigureAwait(true);
                    break;
                }
                case "key":
                {
                    // A non-text key: a special key (arrows / editing / nav) OR a shortcut
                    // (Ctrl/Meta + key). Plain typed characters go through "text" (insertText);
                    // here we synthesize a real key event with the MODIFIER bitmask + the Windows
                    // virtual-key code + DOM code, which CDP needs for navigation keys and
                    // shortcuts (Ctrl+A/C/V/X/Z, arrows, Home/End, Delete) to actually take
                    // effect in the page.
                    static bool Flag(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                    foreach (var payload in BuildKeyEventJsons(root.GetProperty("key").GetString() ?? "",
                                 alt: Flag(root, "alt"), ctrl: Flag(root, "ctrl"), meta: Flag(root, "meta"), shift: Flag(root, "shift")))
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", payload).ConfigureAwait(true);
                    break;
                }
            }
        }); // RunOnUiAsync swallows a malformed-message throw — one bad input can't kill the session
    }

    // Interactive elements the user can act on — returned to the caller (as fractions of the
    // viewport) so the CLIENT can draw a hover highlight + pointer cursor + a pressed state; it
    // only has pixels and can't see the DOM itself. Poll it (the source used ~600 ms — catches a
    // nav, a countdown enabling a button, a challenge appearing) and ship changes over the app's
    // transport.
    private const string HotspotScript = @"(function(){try{
var q='a[href],button,input[type=submit],input[type=button],input[type=image],[role=button],[onclick],label[for],select,summary';
var els=document.querySelectorAll(q),W=innerWidth,H=innerHeight,o=[];
for(var i=0;i<els.length&&o.length<80;i++){var e=els[i],r=e.getBoundingClientRect();
if(r.width<8||r.height<8||r.right<0||r.bottom<0||r.left>W||r.top>H)continue;
var s=getComputedStyle(e);if(s.visibility=='hidden'||s.display=='none'||s.pointerEvents=='none'||+s.opacity===0)continue;
o.push([+(r.left/W).toFixed(4),+(r.top/H).toFixed(4),+(r.width/W).toFixed(4),+(r.height/H).toFixed(4)]);}
return o;}catch(_){return [];}})()";

    /// <summary>
    /// One hotspot extraction: the JSON array of clickable-element rects as viewport FRACTIONS
    /// (<c>[[fx,fy,fw,fh],…]</c>) — the script's result comes back as JSON text, ready to embed
    /// in a transport message verbatim. Empty string when the page is unavailable.
    /// </summary>
    public Task<string> ReadHotspotsAsync()
    {
        if (_disposed != 0 || _form.IsDisposed) return Task.FromResult("");
        return RunOnUiAsync(async () =>
            TryGetCore() is { } core ? await core.ExecuteScriptAsync(HotspotScript).ConfigureAwait(true) ?? "" : "", "");
    }

    /// <summary>Stop the screencast, complete <see cref="Frames"/>, and close the hidden window.
    /// Idempotent. The frame reader is completed FIRST so the app's pump unblocks even if the
    /// message loop is already gone (the UI cleanup is then best-effort, never awaited — a dead
    /// loop must not hang dispose).</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _frames.Writer.TryComplete(); // FIRST: unblock any reader regardless of the UI loop's state
        Controller.Finish();          // (a background controller doesn't hold closes, but stay symmetric)
        RunOnUiFireAndForget(async () =>
        {
            try { if (TryGetCore() is { } core) await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}").ConfigureAwait(true); } catch { }
            try { _form.Close(); _form.Dispose(); } catch { }
        });
        return ValueTask.CompletedTask;
    }

    private Microsoft.Web.WebView2.Core.CoreWebView2? TryGetCore()
    {
        try
        {
            return _web.IsDisposed ? null : _web.CoreWebView2;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Marshal an async body onto the WinForms UI thread (WebView2 must be touched there) and
    /// await its result. Centralizes the BeginInvoke + TaskCompletionSource + try/catch each UI
    /// op otherwise repeats — and, crucially, catches an exception a raw
    /// <c>BeginInvoke(async …)</c> (an async void) would otherwise turn into an UNOBSERVABLE
    /// UI-thread crash. A throwing body, or a control whose handle is already gone, yields the
    /// fallback.
    /// </summary>
    private Task<T> RunOnUiAsync<T>(Func<Task<T>> body, T fallback)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _form.BeginInvoke(new Action(async () =>
            {
                try { tcs.TrySetResult(await body().ConfigureAwait(true)); }
                catch { tcs.TrySetResult(fallback); }
            }));
        }
        catch { tcs.TrySetResult(fallback); } // handle already destroyed (form closed mid-session)
        return tcs.Task;
    }

    /// <summary>Void variant — run a UI-thread action to completion, swallowing failures (a
    /// per-message input dispatch must never fault the session).</summary>
    private Task RunOnUiAsync(Func<Task> body) =>
        RunOnUiAsync<bool>(async () => { await body().ConfigureAwait(true); return true; }, false);

    /// <summary>Post UI cleanup WITHOUT awaiting — dispose must never hang on a stopped message
    /// loop (the async-void body is fully wrapped so nothing escapes).</summary>
    private void RunOnUiFireAndForget(Func<Task> body)
    {
        try { _form.BeginInvoke(new Action(async () => { try { await body().ConfigureAwait(true); } catch { } })); }
        catch { /* loop gone — the frame reader is already completed, so nothing waits */ }
    }

    // ---- pure protocol builders (internal: unit-tested without a browser) ------------------

    /// <summary>The source's viewport clamps (width 320–1560, height 240–1080), shared by the
    /// metrics JSON and the cached mapping viewport so they never disagree.</summary>
    internal static (int Width, int Height) ClampViewport(double width, double height) =>
        ((int)Math.Clamp(width, 320, 1560), (int)Math.Clamp(height, 240, 1080));

    /// <summary>Device-metrics JSON with the source's clamps (dpr 1–2, default 1.5) —
    /// invariant-culture dpr, because "1,50" on a comma-decimal locale is broken JSON (the source
    /// fixed this live).</summary>
    internal static string BuildMetricsOverrideJson(double width, double height, double? dpr)
    {
        var (w, h) = ClampViewport(width, height);
        var d = Math.Clamp(dpr ?? 1.5, 1, 2).ToString("0.00", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"width\":{w},\"height\":{h},\"deviceScaleFactor\":{d},\"mobile\":false,\"screenWidth\":{w},\"screenHeight\":{h}}}");
    }

    /// <summary>A mouse event. <paramref name="buttonHeld"/> carries a pressed button through
    /// moves (Chromium reads held state from <c>buttons</c>, so drags need buttons:1 on move).</summary>
    internal static string BuildMouseEventJson(string clientEvent, double fx, double fy, double vw, double vh, bool buttonHeld)
    {
        var (cdp, buttons) = clientEvent switch
        {
            "pressed" => ("mousePressed", 1),
            "released" => ("mouseReleased", 0),
            _ => ("mouseMoved", buttonHeld ? 1 : 0),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"type\":\"{cdp}\",\"x\":{fx * vw:F0},\"y\":{fy * vh:F0},\"button\":\"left\",\"buttons\":{buttons},\"clickCount\":1}}");
    }

    internal static string BuildWheelEventJson(double fx, double fy, double dy, double vw, double vh) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{{\"type\":\"mouseWheel\",\"x\":{fx * vw:F0},\"y\":{fy * vh:F0},\"deltaX\":0,\"deltaY\":{dy:F0}}}");

    /// <summary>The keyDown/keyUp pair for one non-text key (modifiers: alt=1, ctrl=2, meta=4, shift=8).</summary>
    internal static string[] BuildKeyEventJsons(string key, bool alt, bool ctrl, bool meta, bool shift)
    {
        var modifiers = (alt ? 1 : 0) | (ctrl ? 2 : 0) | (meta ? 4 : 0) | (shift ? 8 : 0);
        var (vk, code) = KeyInfo(key);
        var pair = new string[2];
        var i = 0;
        foreach (var kt in new[] { "keyDown", "keyUp" })
        {
            var payload = new Dictionary<string, object> { ["type"] = kt, ["key"] = key, ["modifiers"] = modifiers };
            if (vk != 0) { payload["windowsVirtualKeyCode"] = vk; payload["nativeVirtualKeyCode"] = vk; }
            if (code is not null) payload["code"] = code;
            pair[i++] = JsonSerializer.Serialize(payload);
        }
        return pair;
    }

    // Map a DOM key name → its Windows virtual-key code + DOM `code`, so CDP can synthesize
    // navigation keys and shortcuts. 0 = no VK (let CDP infer from `key` alone). Covers
    // editing/nav keys + letters/digits (for Ctrl-combos).
    internal static (int Vk, string? Code) KeyInfo(string key) => key switch
    {
        "Backspace" => (8, "Backspace"),
        "Tab" => (9, "Tab"),
        "Enter" => (13, "Enter"),
        "Escape" => (27, "Escape"),
        "Delete" => (46, "Delete"),
        "Home" => (36, "Home"),
        "End" => (35, "End"),
        "ArrowLeft" => (37, "ArrowLeft"),
        "ArrowUp" => (38, "ArrowUp"),
        "ArrowRight" => (39, "ArrowRight"),
        "ArrowDown" => (40, "ArrowDown"),
        " " => (32, "Space"),
        { Length: 1 } when key[0] is >= 'a' and <= 'z' => (char.ToUpperInvariant(key[0]), "Key" + char.ToUpperInvariant(key[0])),
        { Length: 1 } when key[0] is >= 'A' and <= 'Z' => (key[0], "Key" + key[0]),
        { Length: 1 } when key[0] is >= '0' and <= '9' => (key[0], "Digit" + key[0]),
        _ => (0, null),
    };
}
