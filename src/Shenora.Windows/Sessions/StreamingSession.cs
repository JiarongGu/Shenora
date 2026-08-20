using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;
using Shenora.Core.Shell;

namespace Shenora.Windows;

/// <summary>
/// The CSS viewport a streamed page believes it has, applied through CDP device metrics — never a
/// physical window resize, and DPI-independent.
/// <para>
/// ⚠ <b>CLAMPED, and this is the ONE place the bounds are written down.</b> Width 320–1560, height
/// 240–1080, scale 1–2; <c>StreamingSession.ClampViewport</c> is the authority and a test pins the upper
/// edge. A viewer larger than that is FITTED to the maximum rather than refused, so a 4K client sees a
/// 1560-wide page scaled up.
/// </para>
/// </summary>
/// <param name="Width">CSS px, clamped to 320–1560.</param>
/// <param name="Height">CSS px, clamped to 240–1080.</param>
/// <param name="DeviceScaleFactor">Device pixel ratio, clamped to 1–2.</param>
public readonly record struct SessionViewport(int Width, int Height, double DeviceScaleFactor);

/// <summary>How a captured frame is encoded. The platform offers both; the kit picks neither for you.</summary>
public enum StreamingSessionFrameFormat
{
    /// <summary>Lossy, small, and what a live viewer wants. Honours quality.</summary>
    Jpeg,

    /// <summary>Lossless and much larger — for capture or a preview a user will inspect. Quality is ignored.</summary>
    Png,
}

/// <summary>
/// One captured frame: the encoded bytes, HOW they are encoded, and the CSS viewport they depict, read
/// from the frame's OWN metadata. Input coordinates are FRACTIONS of the viewport, so a click maps back
/// against these.</summary>
/// <param name="Bytes">The encoded frame.</param>
/// <param name="Format">Which encoding <paramref name="Bytes"/> is in — label your transport with it.</param>
/// <param name="Width">CSS width of the viewport this frame depicts.</param>
/// <param name="Height">CSS height of the viewport this frame depicts.</param>
public readonly record struct StreamingSessionFrame(byte[] Bytes, StreamingSessionFrameFormat Format, int Width, int Height);

/// <summary>Why a <see cref="StreamingSession"/> ended.</summary>
public enum StreamingSessionEndReason
{
    /// <summary>The app disposed the session — the ordinary path.</summary>
    Disposed,

    /// <summary>The page's RENDERER died (crash, OOM, kill). The stream cannot resume; dispose and start
    /// again.</summary>
    RendererFailed,
}

/// <summary>Why a session ended, handed to <see cref="StreamingSessionOptions.OnEnded"/>.</summary>
/// <param name="Reason">Ordinary dispose, or a renderer failure.</param>
/// <param name="Detail">Diagnostic text when the platform supplied any; null otherwise.</param>
public sealed record StreamingSessionEnded(StreamingSessionEndReason Reason, string? Detail = null);

/// <summary>Inputs for <see cref="StreamingSession"/>.</summary>
public sealed class StreamingSessionOptions
{
    /// <summary>A live UI-thread control (typically the main window) browser work marshals onto.</summary>
    public required Control Anchor { get; init; }

    /// <summary>
    /// Browser configuration — same scoping rule as <see cref="SessionBrowserOptions.ProfileDirectory"/>:
    /// one profile per (provider, sub-account), NEVER one shared jar. Set
    /// <see cref="SessionBrowserOptions.KeepAliveInBackground"/>: the page renders off-screen and must
    /// keep painting for the screencast.
    /// </summary>
    public required SessionBrowserOptions Browser { get; init; }
    /// <summary>Diagnostics. Null = silent. Browser-level events (init failure, suppressed popups, denied
    /// permissions, a dead renderer) report through <see cref="SessionBrowserOptions.Log"/> instead.</summary>
    public Microsoft.Extensions.Logging.ILogger? Log { get; init; }


    /// <summary>Consulted before every controller navigation; return false to refuse. This session both
    /// DISCLOSES the rendered page and accepts input, so a data-driven URL is SSRF-shaped.</summary>
    public Func<Uri, CancellationToken, Task<bool>>? NavigationGuard { get; init; }

    /// <summary>How frames are encoded. Default JPEG — a live viewer wants small frames.</summary>
    public StreamingSessionFrameFormat FrameFormat { get; init; } = StreamingSessionFrameFormat.Jpeg;

    /// <summary>
    /// Encoder quality (1–100). ⚠ JPEG only — PNG is lossless, and the platform ignores this for it.
    /// </summary>
    public int FrameQuality { get; init; } = 72;

    /// <summary>Max captured frame width — generous, so a client-mirrored viewport (~1200 CSS × dpr 2)
    /// is captured crisp.</summary>
    public int MaxFrameWidth { get; init; } = 2560;

    /// <summary>Max captured frame height (see <see cref="MaxFrameWidth"/>).</summary>
    public int MaxFrameHeight { get; init; } = 1800;

    /// <summary>FALLBACK viewport, used only until the client's own <c>viewport</c> input arrives —
    /// which then MIRRORS the client's display box 1:1 (see <see cref="SessionViewportInput"/>).</summary>
    public SessionViewport InitialViewport { get; init; } = new(1280, 860, 1.5);

    /// <summary>Frames buffered between the capture (UI thread) and the app's transport pump.
    /// Latest-frame-wins: the oldest is dropped, so a slow client never backs up the compositor.</summary>
    public int FrameBuffer { get; init; } = 2;

    /// <summary>
    /// Called exactly ONCE when the session ends, with why. Invoked GUARDED — a throw here cannot take
    /// down the session or the UI thread. It may run on the UI thread or on a WebView2 event callback,
    /// so keep it short and marshal anything heavy yourself.
    /// <para>
    /// 🔴 <b>DISPOSE THE SESSION FROM HERE when the reason is
    /// <see cref="StreamingSessionEndReason.RendererFailed"/>.</b> Nothing else will: a dead renderer
    /// ends the session without anyone calling stop, so the off-screen window and the browser process
    /// holding the profile lock survive for the life of the app.
    /// </para>
    /// <para>
    /// ⚠ <b>It does NOT hand you the session</b>, because it can fire before one exists — a renderer
    /// that dies during <c>StartAsync</c> raises this while the object is still being built. Keep your
    /// own handle and read it here; <c>StartAsync</c> owns the teardown for that window.
    /// </para>
    /// </summary>
    public Action<StreamingSessionEnded>? OnEnded { get; init; }
}

/// <summary>
/// An off-screen browser session that STREAMS what it renders and ACCEPTS synthetic input. The kit owns
/// the browser mechanics — off-screen window, profile isolation, the CDP screencast and its ack protocol,
/// latest-wins frame dropping, viewport mirroring through device metrics within
/// <see cref="SessionViewport"/>'s bounds (⚠ never a physical resize, which desyncs CSS layout), input
/// replay at fraction coordinates. The app owns the transport and the viewer: drive
/// <see cref="Controller"/>, pump <see cref="Frames"/> out, feed <see cref="SessionInput"/> back, and
/// dispose YOUR handle when <see cref="StreamingSessionOptions.OnEnded"/> says the session is over.
/// <para>
/// 🔴 <see cref="DispatchAsync"/> is SINGLE-CONSUMER — await each call before the next. Input is stateful
/// (a held button, the current viewport), so overlapping calls reorder a press/move/release.
/// </para>
/// </summary>
public sealed class StreamingSession : IAsyncDisposable
{
    private readonly Form _form;
    private readonly Shenora.Core.Shell.IUiDispatcher _ui;   // the one marshal owner (D19/D20)
    private readonly WebView2Control _web;
    private readonly Channel<StreamingSessionFrame> _frames;

    // The screencast subscription, ROOTED for the session's lifetime: nothing else references the
    // receiver, and a stream that stops after a GC reports NO error — the page just quietly goes still.
    private readonly CoreWebView2DevToolsProtocolEventReceiver _frameReceiver;
    private readonly EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> _onFrame;
    private int _disposed;
    // UI-thread-only state, safe under the single-consumer input contract: the emulated viewport (so
    // pointer/wheel need no round-trip) and whether the left button is held (drags emit buttons:1).
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _buttonDown;

    // The shared once-only latch, so DisposeAsync raises OnEnded through the SAME gate the
    // ProcessFailed callback uses.
    private readonly StreamingSessionOptions _options;
    private readonly StrongBox<int> _endedLatch;

    private StreamingSession(Form form, WebView2Control web, Channel<StreamingSessionFrame> frames, SessionController controller,
        StreamingSessionOptions options, StrongBox<int> endedLatch,
        CoreWebView2DevToolsProtocolEventReceiver frameReceiver,
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> onFrame)
    {
        _form = form;
        _ui = new Shenora.Windows.WinFormsUiDispatcher(form);
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
    /// Captured frames, newest last — a bounded latest-wins buffer (drop-oldest), so a slow client never
    /// backs up the compositor; each carries the CSS viewport it depicts. ⚠ The reader COMPLETES when
    /// the session ends but does not say why: pair it with
    /// <see cref="StreamingSessionOptions.OnEnded"/> to tell a clean dispose from a dead renderer.
    /// </summary>
    public ChannelReader<StreamingSessionFrame> Frames => _frames.Reader;

    /// <summary>
    /// The driver primitives over the streamed page — navigate (guarded), script, origin-scoped cookies.
    /// A BACKGROUND controller, so its window-managing calls (Reveal/FitToBox, hold-close) are inert.
    /// </summary>
    public SessionController Controller { get; }

    /// <summary>
    /// This session's identity — the SCOPE its browser publishes every <see cref="SessionEvents"/>
    /// under. Same value as <c>Controller.Id</c>.</summary>
    public string Id => Controller.Id;

    /// <summary>Create the off-screen browser and start the screencast, on the anchor's UI thread. No
    /// navigation happens here — the caller's driver navigates via <see cref="Controller"/>.</summary>
    public static async Task<StreamingSession> StartAsync(StreamingSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.FrameQuality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(options), "FrameQuality must be 1-100.");
        if (options.MaxFrameWidth < 1 || options.MaxFrameHeight < 1) throw new ArgumentOutOfRangeException(nameof(options), "Max frame size must be positive.");
        if (options.FrameBuffer < 1) throw new ArgumentOutOfRangeException(nameof(options), "FrameBuffer must be at least 1.");

        var frames = Channel.CreateBounded<StreamingSessionFrame>(new BoundedChannelOptions(options.FrameBuffer)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

        // Shared by both end paths (renderer death and dispose) so OnEnded fires exactly once.
        var ended = new StrongBox<int>(0);
        // The label a frame gets when its own metadata is absent or unusable.
        var (fallbackWidth, fallbackHeight) = ClampViewport(options.InitialViewport.Width, options.InitialViewport.Height);

        var tcs = new TaskCompletionSource<StreamingSession>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 🔴 THE TOKEN HAS TO REACH THE RETURNED TASK, not only the posted body — see the same
        // registration in RenderSessionPool.CreateInstanceAsync. `BeginInvoke` succeeds whenever the
        // handle exists, including after `Application.Run` has returned, so every check inside the body
        // is unreachable if nothing pumps it and `StartAsync` never returns even with `ct` ALREADY
        // cancelled.
        using var cancelled = cancellationToken.Register(() =>
        {
            frames.Writer.TryComplete();
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            options.Anchor.BeginInvoke(new Action(async () =>
            {
                Form? form = null;
                try
                {
                    if (cancellationToken.IsCancellationRequested) { frames.Writer.TryComplete(); tcs.TrySetCanceled(cancellationToken); return; }
                    // A generous FIXED physical surface: the real CSS viewport comes purely from
                    // Emulation.setDeviceMetricsOverride (DPI-independent), so this must NOT track the
                    // box. The caption is externally readable (Task Manager), so it takes a MECHANISM
                    // name (D22) even though the window never shows.
                    form = OffscreenWindow.Create("Streaming session", new Size(1600, 1100));
                    var web = new WebView2Control { Dock = DockStyle.Fill };
                    form.Controls.Add(web);
                    // A dead renderer must COMPLETE the frame channel, or the app's `await foreach` over
                    // Frames waits forever for a stream that can never resume.
                    var sessionId = SessionBrowser.NewSessionId();
                    await SessionBrowser.InitializeAsync(web, options.Browser,
                        onProcessFailed: e =>
                        {
                            frames.Writer.TryComplete();
                            SignalEnded(options, ended, new StreamingSessionEnded(StreamingSessionEndReason.RendererFailed,
                                $"{e.ProcessFailedKind}"));
                        },
                        // One browser, one session — but still SCOPED, because the app's bus is shared and
                        // an unscoped emit is a broadcast to every other session's subscribers.
                        sessionScope: () => sessionId,
                        // Gates the await, so a cancelled start escapes here instead of waiting out the
                        // whole InitTimeout to reach the re-check below.
                        cancellationToken: cancellationToken).ConfigureAwait(true);

                    // Re-check AFTER the multi-second init: a start cancelled during it would otherwise
                    // publish nothing while leaving a live off-screen window and a held profile lock
                    // behind, with no owner left to dispose either.
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
                                // The frame's OWN metadata, not the session's current viewport: a resize
                                // in flight would label this frame with a viewport it does not depict.
                                var (w, h) = ReadFrameViewport(doc.RootElement, fallbackWidth, fallbackHeight);
                                frames.Writer.TryWrite(new StreamingSessionFrame(Convert.FromBase64String(data), options.FrameFormat, w, h));
                            }
                            // Observed, not bare-discarded: the catch below covers only the SYNCHRONOUS
                            // half, so a CDP call faulting mid-teardown would surface as an
                            // UnobservedTaskException.
                            core.CallDevToolsProtocolMethodAsync("Page.screencastFrameAck", $"{{\"sessionId\":{sid}}}")
                                .ContinueWith(static t => { var observed = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        }
                        catch { /* one bad frame shouldn't sink the stream */ }
                    }
                    receiver.DevToolsProtocolEventReceived += OnFrame;
                    // A BACKGROUND controller — no hold-close, no reveal — so the off-screen host can
                    // never veto app shutdown.
                    var controller = new SessionController(form, web, options.NavigationGuard, onLoading: null,
                        foreground: false, id: sessionId);
                    await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}").ConfigureAwait(true);
                    var vp = options.InitialViewport;
                    await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                        BuildMetricsOverrideJson(vp.Width, vp.Height, vp.DeviceScaleFactor)).ConfigureAwait(true);
                    // CDP screencast emits only when the page VISUALLY CHANGES, so idle bandwidth is
                    // ~0; everyNthFrame:1 streams every changed frame rather than halving the rate.
                    await core.CallDevToolsProtocolMethodAsync("Page.startScreencast",
                        string.Create(CultureInfo.InvariantCulture,
                            $"{{\"format\":\"{(options.FrameFormat == StreamingSessionFrameFormat.Png ? "png" : "jpeg")}\",\"quality\":{options.FrameQuality},\"maxWidth\":{options.MaxFrameWidth},\"maxHeight\":{options.MaxFrameHeight},\"everyNthFrame\":1}}")).ConfigureAwait(true);

                    // Past this line the caller owns teardown, so anything cancelled up to here must be
                    // torn down here rather than left running.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { receiver.DevToolsProtocolEventReceived -= OnFrame; } catch { }
                        try { form.Dispose(); } catch { }
                        frames.Writer.TryComplete();
                        tcs.TrySetCanceled(cancellationToken);
                        return;
                    }

                    var session = new StreamingSession(form, web, frames, controller, options, ended,
                        receiver, OnFrame);
                    // ⚠ A false return means the registration above already cancelled the task, so the
                    // caller is gone and NOBODY OWNS this session — tear it down rather than leak an
                    // off-screen form and a live browser process.
                    if (!tcs.TrySetResult(session))
                    {
                        try { receiver.DevToolsProtocolEventReceived -= OnFrame; } catch { }
                        try { form.Dispose(); } catch { }
                        frames.Writer.TryComplete();
                    }
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
            // The anchor's handle isn't created / is gone — surface it through the task, not
            // synchronously out of a Task-returning API, and don't leave the reader hanging.
            frames.Writer.TryComplete();
            tcs.TrySetException(ex);
        }

        // ⚠ AWAITED, not returned. `using var` on a non-async method disposes at the `return`, which is
        // immediately — the registration above would be gone before the token could ever fire, and the
        // fix would be inert while looking correct.
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Replay ONE client input into the page. Coordinates arrive as FRACTIONS (0..1) of the viewport and
    /// map to CSS px via the emulated viewport the session itself set, so there is no round-trip. Never
    /// faults the session: a body that throws is swallowed by the marshalling owner, because one bad
    /// input must not end a stream someone is watching. ⚠ SINGLE-CONSUMER — await each call before the
    /// next; the held-button state is why order matters.
    /// </summary>
    /// <param name="input">The input to replay.</param>
    /// <param name="cancellationToken">Abandons the WAIT for the UI thread. It cannot un-send an input
    /// already handed to CDP, so this bounds the caller, not the page.</param>
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
                    // A key that ACTS rather than types; plain characters go through SessionTextInput.
                    // CDP needs the modifier bitmask, the virtual-key code and the DOM code for those
                    // to take effect at all.
                    foreach (var payload in BuildKeyEventJsons(key.Key, key.Alt, key.Ctrl, key.Meta, key.Shift))
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", payload).ConfigureAwait(true);
                    break;
                }

                default:
                    // Not assumed unreachable: `SessionInput`'s seal is not airtight (a record's copy
                    // constructor is protected) and a case added later could be missed. Without this
                    // arm the input vanishes in silence, which on a stream someone is watching looks
                    // like the page hung.
                    SessionLog.Try(_options.Log, log =>
                        log.LogWarning("Streaming session: input of unsupported type {InputType} was ignored",
                            input.GetType().Name));
                    break;
            }
        }, cancellationToken);
    }
    /// <summary>Stop the screencast, complete <see cref="Frames"/>, and close the hidden window.
    /// Idempotent. The frame reader is completed FIRST so the app's pump unblocks even if the message
    /// loop is already gone; the UI cleanup is best-effort and never awaited.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _frames.Writer.TryComplete(); // FIRST: unblock any reader regardless of the UI loop's state
        // Then say WHY, through the shared latch — a dispose racing a renderer crash reports whichever
        // happened first, never both. Before the UI teardown below, which may never run.
        SignalEnded(_options, _endedLatch, new StreamingSessionEnded(StreamingSessionEndReason.Disposed));
        Controller.Finish();          // (a background controller doesn't hold closes, but stay symmetric)
        RunOnUiFireAndForget(async () =>
        {
            // Detach before stopping: the handler closes over the channel and the core, so leaving it
            // attached keeps both reachable from the SDK's event plumbing after this session is gone.
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

    /// <summary>Marshal an async body onto the WinForms UI thread (WebView2 must be touched there) and
    /// await it, through the ONE marshal owner in its never-faulting mode. A throwing body, or a control
    /// whose handle is gone, yields the fallback — one input dispatch must not fault the session.</summary>
    private Task<T> RunOnUiAsync<T>(Func<Task<T>> body, T fallback) =>
        _ui.InvokeOrDefaultAsync(body, fallback);

    /// <summary>Void variant — run a UI-thread action to completion, swallowing failures. The token
    /// bounds the WAIT for the UI thread only; it cannot un-send work already handed to CDP.</summary>
    private Task RunOnUiAsync(Func<Task> body, CancellationToken cancellationToken = default) =>
        _ui.InvokeOrDefaultAsync<bool>(
            async () => { await body().ConfigureAwait(true); return true; }, false, cancellationToken);

    /// <summary>Post UI cleanup WITHOUT awaiting — dispose must never hang on a stopped message loop.
    /// The dispatcher's async Post guards the body, so no fault escapes as an async-void crash.</summary>
    private void RunOnUiFireAndForget(Func<Task> body) => _ui.Post(body);

    // ---- pure protocol builders (internal: unit-tested without a browser) ------------------

    /// <summary>
    /// Fire <see cref="StreamingSessionOptions.OnEnded"/> AT MOST ONCE, guarded. The latch is shared with
    /// the instance because the two end paths race by nature: a renderer can die while the app disposes,
    /// on a different stack from <c>DisposeAsync</c>. Guarded because an escaping exception in a WebView2
    /// event handler has no caller on the stack.
    /// </summary>
    private static void SignalEnded(StreamingSessionOptions options, StrongBox<int> latch, StreamingSessionEnded ended)
    {
        if (options.OnEnded is not { } handler) return;
        if (Interlocked.Exchange(ref latch.Value, 1) != 0) return;
        Shenora.AppCallback.Run(() => handler(ended),
            onError: ex => SessionLog.Try(options.Log, log => log.LogError(ex, "Streaming session: OnEnded handler threw")));
    }

    /// <summary>The CSS viewport a screencast frame depicts, from its own <c>metadata</c>. Falls back to
    /// the session's configured viewport — what the page was told to emulate — when the platform omits
    /// or mangles it.</summary>
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

    /// <summary>Viewport clamps (width 320–1560, height 240–1080), shared by the metrics JSON and the
    /// cached mapping viewport so they never disagree.</summary>
    internal static (int Width, int Height) ClampViewport(double width, double height) =>
        ((int)Math.Clamp(width, 320, 1560), (int)Math.Clamp(height, 240, 1080));

    /// <summary>Device-metrics JSON (dpr clamped 1–2, default 1.5). Invariant-culture dpr — "1,50" on
    /// a comma-decimal locale is broken JSON.</summary>
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

    // A DOM key name → its Windows virtual-key code + DOM `code`. 0 = no VK (CDP infers from `key`).
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
