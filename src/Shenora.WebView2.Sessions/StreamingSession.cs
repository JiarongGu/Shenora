using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.WebView2.Sessions;

/// <summary>A CSS viewport the co-browse page emulates (device metrics, DPI-independent).</summary>
public readonly record struct SessionViewport(int Width, int Height, double DeviceScaleFactor);

/// <summary>
/// One captured frame: the JPEG bytes plus the CSS viewport they depict.
/// <para>
/// The geometry is the point (P5.5 H9.3 / D21). Frames used to arrive as bare <c>byte[]</c>, so an app
/// received pixels with no idea what viewport they represented — and since input coordinates are
/// FRACTIONS of the viewport, it could not map a click back reliably without inventing its own
/// side-channel. That is precisely the "you end up needing the app's own protocol anyway" trap D21
/// names. The values come from the screencast frame's own metadata, so they describe THAT frame
/// rather than whatever the viewport happens to be by the time the app reads it.
/// </para>
/// </summary>
/// <param name="Jpeg">The encoded frame.</param>
/// <param name="Width">CSS width of the viewport this frame depicts.</param>
/// <param name="Height">CSS height of the viewport this frame depicts.</param>
public readonly record struct SessionFrame(byte[] Jpeg, int Width, int Height);

/// <summary>Why a <see cref="StreamingSession"/> ended.</summary>
public enum SessionEndReason
{
    /// <summary>The app disposed the session — the ordinary path.</summary>
    Disposed,

    /// <summary>
    /// The page's RENDERER died (crash, OOM, kill). The stream cannot resume; dispose and start again.
    /// Distinguishing this from <see cref="Disposed"/> is the whole reason the hook carries a reason:
    /// both complete the frame channel, so a reader alone cannot tell a crash from a clean shutdown.
    /// </summary>
    RendererFailed,
}

/// <summary>Why a session ended, handed to <see cref="StreamingSessionOptions.OnEnded"/>.</summary>
/// <param name="Reason">Ordinary dispose, or a renderer failure.</param>
/// <param name="Detail">Diagnostic text when the platform supplied any; null otherwise.</param>
public sealed record SessionEnded(SessionEndReason Reason, string? Detail = null);

/// <summary>Inputs for <see cref="StreamingSession"/>.</summary>
public sealed class StreamingSessionOptions
{
    /// <summary>A live UI-thread control (typically the main window) browser work marshals onto.</summary>
    public required Control Anchor { get; init; }

    /// <summary>
    /// Browser configuration — same scoping rule as <see cref="InteractiveSessionOptions.ProfileDirectory"/>:
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
    /// SSRF-shaped seam as the pool and the interactive session: co-browse URLs are data-driven, and
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
    /// client's display box 1:1 — see <see cref="SessionViewportInput"/>).
    /// deviceScaleFactor 1.5 keeps it crisp regardless of host DPI.
    /// </summary>
    public SessionViewport InitialViewport { get; init; } = new(1280, 860, 1.5);

    /// <summary>
    /// Frames buffered between the capture (UI thread) and the app's transport pump — latest-
    /// frame-wins: a slow client never backs up the compositor, the oldest frame is dropped.
    /// </summary>
    public int FrameBuffer { get; init; } = 2;

    /// <summary>
    /// Called exactly ONCE when the session ends, with why (P5.5 H9.3 / D21 — the lifecycle hook the
    /// feature was missing). Both a clean dispose and a dead renderer complete
    /// <see cref="StreamingSession.Frames"/>, so a reader alone cannot tell them apart; this can. Use it
    /// to tear down the transport, tell the user, or decide whether restarting is worth it.
    /// <para>
    /// App code, so it is invoked GUARDED — a throw here cannot take down the session or the UI
    /// thread. It may run on the UI thread or on a WebView2 event callback, so keep it short and
    /// marshal anything heavy yourself.
    /// </para>
    /// </summary>
    public Action<SessionEnded>? OnEnded { get; init; }
}

/// <summary>
/// An off-screen browser session that STREAMS what it renders and ACCEPTS synthetic input — the two
/// browser capabilities, exposed as primitives. Everything else is the app's.
///
/// <para>
/// THE LIFECYCLE IS THE CONTRACT. An app plugs into these four points and builds its own product on
/// them; the kit decides none of it:
/// </para>
/// <list type="number">
/// <item><b>Started</b> — <see cref="StartAsync"/> completes. The browser is live on its isolated
/// profile and nothing has navigated yet; the app's driver decides where to go.</item>
/// <item><b>Navigating / navigated</b> — <see cref="Controller"/>'s taps
/// (<c>OnNavigation</c>, <c>OnMessage</c>, <c>OnDownload</c>, <c>OnNewWindow</c>), plus the
/// <see cref="StreamingSessionOptions.NavigationGuard"/> that can refuse a hop outright.</item>
/// <item><b>Frames</b> — <see cref="Frames"/>, a bounded latest-wins channel of
/// <see cref="SessionFrame"/>, each carrying the viewport it depicts.</item>
/// <item><b>Ended or faulted</b> — <see cref="StreamingSessionOptions.OnEnded"/>, exactly once, with
/// a <see cref="SessionEndReason"/> so a crash is distinguishable from a clean shutdown.</item>
/// </list>
///
/// <para>
/// WHAT THE KIT OWNS is the earned browser mechanics, none of which is about any particular feature:
/// the off-screen window and profile isolation, the CDP screencast and its ack protocol, latest-wins
/// frame dropping so a slow reader never backs up the compositor, 1:1 viewport mirroring through
/// device metrics ALONE (never a physical resize, which desyncs CSS layout), and input replay at
/// resolution-independent fraction coordinates.
/// </para>
/// <para>
/// WHAT THE APP OWNS is the product: the TRANSPORT (WebSocket, the IPC bridge, anything — pump
/// <see cref="Frames"/> out, feed <see cref="SessionInput"/> back), the viewer UI, hover/hotspot
/// affordances, recording, permissions, and what any of it is FOR. Screen-sharing a checkout,
/// letting a user clear a verification challenge in-app, remote support, visual regression capture,
/// a headless preview pane — those are compositions, not features of this type.
/// </para>
/// <para>
/// This was called <c>CoBrowseSession</c> until P5.5 H9.8, which is the mistake worth remembering:
/// the mechanics were always generic, but naming the type after ONE product it enables made the kit
/// look like it shipped that product, and invited the next contributor to add more of it. The sibling
/// sessions are named the same way — <see cref="RenderSession"/> reads a rendered page,
/// <see cref="InteractiveSession"/> lets a human drive one. Name the mechanism (D22).
/// </para>
/// <para>
/// <see cref="Controller"/> is the SAME primitive set an interactive session drives (as a BACKGROUND
/// controller — its window-managing calls are inert), so a driver's navigate/script/cookie-capture
/// hooks run identically over the stream, and it is the seam for anything page-specific an app wants
/// (element geometry, readiness probes) that the kit does not decide for it. Dispose with the flow
/// (stops the screencast, closes the hidden window).
/// </para>
/// <para>
/// ORDERING: <see cref="DispatchAsync"/> is a SINGLE-CONSUMER contract — the caller must
/// await each call before the next (a transport pump does exactly this). Input is
/// stateful (a held mouse button, the current viewport), so overlapping calls could reorder a
/// press/move/release or transpose typed keys.
/// </para>
/// </summary>
public sealed class StreamingSession : IAsyncDisposable
{
    private readonly Form _form;
    private readonly Shenora.Core.IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly WebView2Control _web;
    private readonly Channel<SessionFrame> _frames;

    // The screencast subscription, ROOTED for the session's lifetime (P5.5 H2). It used to live only
    // in a local inside StartAsync: nothing referenced the receiver once that method returned, so the
    // frame stream depended on the WebView2 SDK caching the receiver internally — unspecified
    // behaviour, and a stream that stops after an arbitrary GC reports NO error at all (the app just
    // sees a page that quietly went still). Held here, and detached in DisposeAsync.
    private readonly CoreWebView2DevToolsProtocolEventReceiver _frameReceiver;
    private readonly EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> _onFrame;
    private int _disposed;
    // UI-thread-only state (mutated inside marshalled bodies; safe under the single-consumer
    // input contract): the current emulated viewport (so we never round-trip to read it) and
    // whether the left button is held (so drags emit buttons:1 on move, not 0).
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _buttonDown;

    // The options + the shared once-only latch, so DisposeAsync can raise OnEnded through the SAME
    // gate the ProcessFailed callback uses (see SignalEnded for why the race is real).
    private readonly StreamingSessionOptions _options;
    private readonly StrongBox<int> _endedLatch;

    private StreamingSession(Form form, WebView2Control web, Channel<SessionFrame> frames, SessionController controller,
        StreamingSessionOptions options, StrongBox<int> endedLatch,
        CoreWebView2DevToolsProtocolEventReceiver frameReceiver,
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> onFrame)
    {
        _form = form;
        _ui = new Shenora.WinForms.WinFormsUiDispatcher(form);
        _web = web;
        _frames = frames;
        Controller = controller;
        _options = options;
        _endedLatch = endedLatch;
        _frameReceiver = frameReceiver;
        _onFrame = onFrame;
        (_viewportWidth, _viewportHeight) = ClampViewport(options.InitialViewport.Width, options.InitialViewport.Height);
    }

    /// <summary>
    /// Captured frames, newest last — a bounded latest-wins buffer (drop-oldest), so a slow client
    /// never backs up the compositor. Pump these to the client; each carries the CSS viewport it
    /// depicts (see <see cref="SessionFrame"/>).
    /// <para>
    /// The reader COMPLETES when the session ends — but completion alone does not say why, so pair it
    /// with <see cref="StreamingSessionOptions.OnEnded"/> to tell a clean dispose from a dead renderer.
    /// </para>
    /// </summary>
    public ChannelReader<SessionFrame> Frames => _frames.Reader;

    /// <summary>
    /// The driver primitives over the streamed page — navigate (guarded), script, origin-scoped
    /// cookies, message/download/new-window/navigation taps. Deliberately the SAME controller the
    /// interactive session runs, so one driver serves both shapes; here it is a BACKGROUND controller,
    /// so its window-managing calls (Reveal/FitToBox, hold-close) are inert.
    /// </summary>
    public SessionController Controller { get; }

    /// <summary>
    /// Create the off-screen browser and start the screencast, on the anchor's UI thread. No
    /// navigation happens here — the caller's driver navigates via <see cref="Controller"/>.
    /// </summary>
    public static Task<StreamingSession> StartAsync(StreamingSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.JpegQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), "JpegQuality must be 1-100.");
        if (options.MaxFrameWidth < 1 || options.MaxFrameHeight < 1) throw new ArgumentOutOfRangeException(nameof(options), "Max frame size must be positive.");
        if (options.FrameBuffer < 1) throw new ArgumentOutOfRangeException(nameof(options), "FrameBuffer must be at least 1.");

        // Latest-frame-wins: a slow client never backs up the compositor — only the newest
        // frames are kept.
        var frames = Channel.CreateBounded<SessionFrame>(new BoundedChannelOptions(options.FrameBuffer)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

        // Shared by both end paths (renderer death and dispose) so OnEnded fires exactly once.
        var ended = new StrongBox<int>(0);
        // The viewport the page is told to emulate — the label a frame gets when its own metadata
        // is absent or unusable.
        var (fallbackWidth, fallbackHeight) = ClampViewport(options.InitialViewport.Width, options.InitialViewport.Height);

        var tcs = new TaskCompletionSource<StreamingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    // A dead renderer must COMPLETE the frame channel (P5.5 H4.4), or the app's
                    // `await foreach` over Frames waits forever for a stream that can never resume.
                    // H9.3 adds the other half D21 asked for: completing the channel tells a reader
                    // "no more frames" but NOT WHY, so a crash is indistinguishable from a clean
                    // shutdown — the ended hook carries the reason.
                    await SessionBrowser.InitializeAsync(web, options.Browser,
                        onProcessFailed: e =>
                        {
                            frames.Writer.TryComplete();
                            SignalEnded(options, ended, new SessionEnded(SessionEndReason.RendererFailed,
                                $"{e.ProcessFailedKind}"));
                        },
                        // Gates the await only (P5.5 H9.6) — a start cancelled during the multi-second
                        // init now escapes there instead of waiting out the whole InitTimeout to reach
                        // the re-check below.
                        cancellationToken: cancellationToken).ConfigureAwait(true);

                    // Re-check AFTER the multi-second init (P5.5 H2). The pre-check above was the only
                    // one, so a start cancelled during those seconds still published nothing to the
                    // caller while leaving behind a live off-screen window, a browser process holding
                    // the profile lock, and — once the screencast started — frames being written into a
                    // channel no reader would ever be handed.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { form.Dispose(); } catch { }
                        frames.Writer.TryComplete();
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    var core = web.CoreWebView2;
                    var receiver = core.GetDevToolsProtocolEventReceiver("Page.screencastFrame");
                    void OnFrame(object? _, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                            var sid = doc.RootElement.GetProperty("sessionId").GetInt32();
                            var data = doc.RootElement.GetProperty("data").GetString();
                            if (!string.IsNullOrEmpty(data))
                            {
                                // Geometry from the frame's OWN metadata rather than the session's
                                // current viewport (H9.3): a resize in flight would otherwise label
                                // this frame with the NEW viewport it does not depict, which is
                                // exactly when a mis-mapped click hurts.
                                var (w, h) = ReadFrameViewport(doc.RootElement, fallbackWidth, fallbackHeight);
                                frames.Writer.TryWrite(new SessionFrame(Convert.FromBase64String(data), w, h));
                            }
                            _ = core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{sid}}}");
                        }
                        catch { /* one bad frame shouldn't sink the stream */ }
                    }
                    receiver.DevToolsProtocolEventReceived += OnFrame;
                    // Reuse the SAME controller an interactive session uses (a BACKGROUND one — no
                    // hold-close, no reveal), so a driver's capture hooks run identically over
                    // the stream without the off-screen host ever vetoing app shutdown.
                    var controller = new SessionController(form, web, options.NavigationGuard, onLoading: null, foreground: false);
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

                    // Last gate before publishing: the CDP round-trips above are cheap but not free,
                    // and past this line the caller owns teardown — so anything cancelled up to here
                    // must be torn down by US, not left running.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { receiver.DevToolsProtocolEventReceived -= OnFrame; } catch { }
                        try { form.Dispose(); } catch { }
                        frames.Writer.TryComplete();
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    tcs.TrySetResult(new StreamingSession(form, web, frames, controller, options, ended,
                        receiver, OnFrame));
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
    /// Replay ONE client input into the page. Coordinates arrive as FRACTIONS (0..1) of the viewport
    /// and map to CSS px via the emulated viewport the session itself set, so there is no round-trip
    /// to the page.
    /// <para>
    /// This replaced <c>DispatchInputAsync(string json)</c> (P5.5 H9.1 / D21), which took the
    /// originating app's wire protocol as an opaque JSON string — see <see cref="SessionInput"/> for
    /// why that was the wrong contract and for <see cref="SessionInput.TryParseLegacyJson"/>, the
    /// mechanical migration path. The mechanics below are unchanged.
    /// </para>
    /// <para>
    /// Never faults the session: a body that throws is swallowed by the marshalling owner, because one
    /// bad input must not end a stream someone is watching. SINGLE-CONSUMER — await each call before
    /// the next (see the class doc); the held-button state below is why order matters.
    /// </para>
    /// </summary>
    /// <param name="input">The input to replay.</param>
    /// <param name="cancellationToken">
    /// Abandons the WAIT for the UI thread. It cannot un-send an input already handed to CDP, so this
    /// bounds the caller, not the page.
    /// </param>
    public Task DispatchAsync(SessionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_disposed != 0 || _form.IsDisposed) return Task.CompletedTask;
        return RunOnUiAsync(async () =>
        {
            var core = TryGetCore();
            if (core is null) return;
            switch (input)
            {
                case SessionViewportInput viewport:
                {
                    var (w, h) = ClampViewport(viewport.Width, viewport.Height);
                    _viewportWidth = w;
                    _viewportHeight = h; // cache so pointer/wheel need no innerWidth round-trip
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                        BuildMetricsOverrideJson(w, h, viewport.DeviceScaleFactor)).ConfigureAwait(true);
                    break;
                }
                case SessionPointerInput pointer:
                {
                    if (pointer.Action == SessionPointerAction.Down) _buttonDown = true;
                    else if (pointer.Action == SessionPointerAction.Up) _buttonDown = false;
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        BuildMouseEventJson(pointer.Action, pointer.X, pointer.Y,
                            _viewportWidth, _viewportHeight, _buttonDown)).ConfigureAwait(true);
                    break;
                }
                case SessionWheelInput wheel:
                {
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        BuildWheelEventJson(wheel.X, wheel.Y, wheel.DeltaY,
                            _viewportWidth, _viewportHeight)).ConfigureAwait(true);
                    break;
                }
                case SessionTextInput text:
                {
                    await core.CallDevToolsProtocolMethodAsync("Input.insertText",
                        JsonSerializer.Serialize(new { text = text.Text })).ConfigureAwait(true);
                    break;
                }
                case SessionKeyInput key:
                {
                    // A key that ACTS rather than types: a special key (arrows / editing / nav) or a
                    // shortcut (Ctrl/Meta + key). Plain typed characters go through SessionTextInput
                    // (insertText); here we synthesize a real key event with the MODIFIER bitmask, the
                    // Windows virtual-key code and the DOM code, which CDP needs for navigation keys
                    // and shortcuts (Ctrl+A/C/V/X/Z, arrows, Home/End, Delete) to take effect at all.
                    foreach (var payload in BuildKeyEventJsons(key.Key, key.Alt, key.Ctrl, key.Meta, key.Shift))
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", payload).ConfigureAwait(true);
                    break;
                }

                default:
                    // Not assumed unreachable. `SessionInput`'s private-protected constructor makes
                    // the cases above the intended whole set, but a record's copy constructor is
                    // protected, so the seal is not airtight — and a case added here later could
                    // simply be forgotten. Either way the failure mode without this arm is an input
                    // that vanishes in silence, which on a stream someone is watching looks like the
                    // page hung. Say so instead.
                    SessionLog.Try(_options.Log, log =>
                        log.LogWarning("Co-browse input of unsupported type {InputType} was ignored",
                            input.GetType().Name));
                    break;
            }
        }, cancellationToken);
    }
    /// <summary>Stop the screencast, complete <see cref="Frames"/>, and close the hidden window.
    /// Idempotent. The frame reader is completed FIRST so the app's pump unblocks even if the
    /// message loop is already gone (the UI cleanup is then best-effort, never awaited — a dead
    /// loop must not hang dispose).</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _frames.Writer.TryComplete(); // FIRST: unblock any reader regardless of the UI loop's state
        // Then say WHY, through the shared latch — so a dispose that races a renderer crash reports
        // whichever happened first and never both (H9.3). Raised before the UI teardown below, which
        // is fire-and-forget and may never run if the message loop is already gone.
        SignalEnded(_options, _endedLatch, new SessionEnded(SessionEndReason.Disposed));
        Controller.Finish();          // (a background controller doesn't hold closes, but stay symmetric)
        RunOnUiFireAndForget(async () =>
        {
            // Detach the screencast subscription before stopping it: the handler closes over the
            // channel and the core, and leaving it attached to a receiver we still hold keeps both
            // reachable from the SDK's event plumbing after this session is gone.
            try { _frameReceiver.DevToolsProtocolEventReceived -= _onFrame; } catch { }
            try { if (TryGetCore() is { } core) await core.CallDevToolsProtocolMethodAsync("Page.stopScreencast", "{}").ConfigureAwait(true); } catch { }
            try { _form.Close(); _form.Dispose(); } catch { }
        });
        return ValueTask.CompletedTask;
    }

    private CoreWebView2? TryGetCore()
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
    private Task<T> RunOnUiAsync<T>(Func<Task<T>> body, T fallback) =>
        // The ONE marshal owner (P5.5 H4.2), in its never-faulting mode — which exists BECAUSE of
        // this contract: a per-message input dispatch must not fault the whole session. That is also
        // why the dispatcher needed an InvokeOrDefault overload rather than only faulting ones; an
        // adversarial review of the design caught that collapsing this site onto a plain InvokeAsync
        // would have silently inverted its behaviour.
        _ui.InvokeOrDefaultAsync(body, fallback);

    /// <summary>Void variant — run a UI-thread action to completion, swallowing failures (a
    /// per-message input dispatch must never fault the session). The token bounds the WAIT for the UI
    /// thread only; it cannot un-send work already handed to CDP.</summary>
    private Task RunOnUiAsync(Func<Task> body, CancellationToken cancellationToken = default) =>
        _ui.InvokeOrDefaultAsync<bool>(
            async () => { await body().ConfigureAwait(true); return true; }, false, cancellationToken);

    /// <summary>Post UI cleanup WITHOUT awaiting — dispose must never hang on a stopped message
    /// loop. The dispatcher's async Post guards the body, so no fault escapes as an async-void crash.</summary>
    private void RunOnUiFireAndForget(Func<Task> body) => _ui.Post(body);

    // ---- pure protocol builders (internal: unit-tested without a browser) ------------------

    /// <summary>
    /// Fire <see cref="StreamingSessionOptions.OnEnded"/> AT MOST ONCE, guarded.
    /// <para>
    /// The latch is shared with the instance (it is created in <c>StartAsync</c> and handed to the
    /// constructor) because the two end paths race by nature: a renderer can die while the app is
    /// disposing, and the WebView2 callback runs on a different stack from <c>DisposeAsync</c>.
    /// "Exactly once" is a contract an app will build teardown on, so it is enforced with an
    /// interlocked latch rather than by hoping the paths are mutually exclusive.
    /// </para>
    /// <para>
    /// GUARDED because an <c>Action</c> from options is APP CODE (the kit-wide rule): here it runs
    /// inside a WebView2 event handler, where an escaping exception has no caller on the stack and
    /// surfaces as the family bootstrap's crash dialog — while the session is already failing.
    /// </para>
    /// </summary>
    private static void SignalEnded(StreamingSessionOptions options, StrongBox<int> latch, SessionEnded ended)
    {
        if (options.OnEnded is not { } handler) return;
        if (Interlocked.Exchange(ref latch.Value, 1) != 0) return;
        Shenora.Core.AppCallback.Run(() => handler(ended),
            onError: ex => SessionLog.Try(options.Log, log => log.LogError(ex, "Co-browse OnEnded handler threw")));
    }

    /// <summary>
    /// The CSS viewport a screencast frame depicts, from its own <c>metadata</c>. Falls back to the
    /// session's configured viewport when the platform omits or mangles it — a frame with plausible
    /// geometry beats dropping the frame, since the fallback is what the page was told to emulate.
    /// </summary>
    internal static (int Width, int Height) ReadFrameViewport(JsonElement frameParams, int fallbackWidth, int fallbackHeight)
    {
        if (!frameParams.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
            return (fallbackWidth, fallbackHeight);

        static int Dimension(JsonElement m, string name, int fallback) =>
            m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var d) && d >= 1
                ? (int)Math.Round(d)
                : fallback;

        return (Dimension(metadata, "deviceWidth", fallbackWidth),
                Dimension(metadata, "deviceHeight", fallbackHeight));
    }

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
    internal static string BuildMouseEventJson(SessionPointerAction action, double fx, double fy, double vw, double vh, bool buttonHeld)
    {
        var (cdp, buttons) = action switch
        {
            SessionPointerAction.Down => ("mousePressed", 1),
            SessionPointerAction.Up => ("mouseReleased", 0),
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
