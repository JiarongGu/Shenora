using System.Text.Json;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.Windows;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Sample.Desktop;

/// <summary>
/// The sample main window — since P4 a FRAMELESS <see cref="OptimizedForm"/>: the page renders
/// its own title bar and drives the window over the <c>WINDOW</c> IPC module
/// (<see cref="WindowCommandFacade"/>); drop zones overlay page elements
/// (<see cref="DropZoneManager"/>); a tray icon (launcher-style, no close-to-tray so the e2e's
/// graceful close still exits) rounds out the native surface. The IPC bridge keeps its intended
/// order — construct before init (event buffering), attach after init, before navigation.
/// </summary>
public sealed class MainForm : OptimizedForm
{
    /// <summary>One background everywhere — form, WebView2, splash, page CSS, and the DWM border.</summary>
    public static readonly Color Background = Color.FromArgb(31, 31, 31);

    private readonly WebViewHost _host;
    private readonly SplashPanel _splash;
    private readonly WebView2Control _webView;
    private readonly WebViewIpcBridge _bridge;
    private readonly DropZoneManager _dropZones;
    private readonly TrayIcon _tray;
    private readonly RenderSessionPool _renderPool;

    // The streaming session and its frame pump are the SAMPLE's state, not the kit's: one
    // at a time here purely to keep the seam test small (the kit imposes no such limit).
    private StreamingSession? _stream;
    private Task? _streamPump;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private int _tickCount;

    public MainForm(WebViewHostOptions hostOptions, IMessageDispatcher dispatcher, IEventBus eventBus, ShenoraPaths paths)
        : base(new OptimizedFormOptions
        {
            FramelessChrome = true,
            BackColor = Background,
            DwmBorderColor = Background, // border line matches the app edge → no visible frame
            // P5.6 hybrid chrome: the window owns the three caption-button pixels and paints them.
            NativeCaptionButtons = true,
        })
    {
        Text = "Shenora Sample";
        MinimumSize = new Size(640, 420);

        _webView = new WebView2Control { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        // P5.6 hybrid chrome. The page keeps its title bar, its drag and its theme; only the
        // three-button cluster stops being page-drawn — the window cuts that rect out of whatever
        // covers it, so the OS routes real mouse input to the form, which is the whole reason Snap
        // Layouts works at all.
        //
        // The kit paints the buttons; these colours are ours (D13 — same split as TrayMenuColors).
        // Surface MUST match the page's title-bar background (#252525 in App.tsx) or the cut-out
        // shows as a visible seam beside the buttons.
        CaptionButtonColors = new CaptionButtonColors
        {
            Surface = Color.FromArgb(37, 37, 37),
            Hover = Color.FromArgb(47, 47, 47),
            Pressed = Color.FromArgb(58, 58, 58),
            Glyph = Color.FromArgb(236, 234, 242),
            CloseHover = Color.FromArgb(196, 43, 28),
            ClosePressed = Color.FromArgb(163, 36, 23),
            CloseGlyphHot = Color.White,
        };
        // Declare the cluster NOW, so the buttons are live behind the SPLASH — the window must be
        // closable while the page is still loading (or failing to). The page re-reports the real
        // rects on its ready handshake; until then these are the host's own estimate of the layout
        // it knows the page uses. Kept in sync by ReportSplashCaptionButtons on resize.
        Resize += (_, _) => ReportSplashCaptionButtons();

        _splash = new SplashPanel(new SplashPanelOptions { BackColor = Background });
        Controls.Add(_splash);
        _splash.BringToFront();

        // Native events → React: a 1 Hz tick emitted on the app's event bus; the bridge forwards
        // it to the page as a batched notification. Started on the client's ready handshake so
        // the first tick is never wasted on an unsubscribed page.
        _tickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _tickTimer.Tick += (_, _) => _ = eventBus.EmitAsync("SAMPLE", "TICK",
            new { Count = ++_tickCount, At = DateTimeOffset.Now.ToString("HH:mm:ss") });

        // Drop zones: transparent overlays synced to the page's zone elements (real OS paths).
        _dropZones = new DropZoneManager(new DropZoneManagerOptions
        {
            WebView = _webView,
            ParentForm = this,
            EventBus = eventBus,
        });

        // P5: a pool of driveable OFF-SCREEN browser sessions anchored on this form's UI thread.
        // Its own profile directory — never the main WebView2's user-data folder (two
        // environments over one folder fight for the browser lock). The guard demonstrates the
        // SSRF policy seam: session URLs are data-driven, and this demo only renders local pages.
        _renderPool = new RenderSessionPool(new RenderSessionPoolOptions
        {
            Anchor = this,
            Browser = new SessionBrowserOptions
            {
                ProfileDirectory = Path.Combine(paths.DataArea("sessions"), "render"),
                KeepAliveInBackground = true, // off-screen pages must keep their JS running
                // E1: hand the session the SAME bundle the shell serves, so a PACKAGED build can
                // render its own frontend off-screen. Without it the session browser has its own
                // environment with no serving at all, and `https://sample.local/...` came up as
                // WebView2's "can't reach this page" — see the STREAM session below for the same pair.
                VirtualHost = hostOptions.VirtualHost,
                ResourceProvider = hostOptions.ResourceProvider,
            },
            Capacity = 2,
            // Same fix as the STREAM guard below, same bug: `IsLoopback` alone refuses this app's own
            // packaged origin, so the demo was silently dev-only.
            NavigationGuard = (uri, _) =>
                Task.FromResult(uri.IsLoopback || uri.Host == "sample.local"),
        });

        // The window-facing facades need the live form, so they map HERE — late registration is
        // supported and safe while requests are in flight (the dispatcher rebuilds its pipeline under a
        // lock). No downcast: every mapping helper composes on IMessageDispatcher itself.
        //
        // This used to read `if (dispatcher is MessageDispatcher concrete) { … }` with no else, because
        // the interface exposed only dispatch/send. That silently dropped all three modules below for
        // any composition that registered a different IMessageDispatcher or wrapped it in a decorator —
        // and the symptom was the frameless title bar simply not working, with no error anywhere.
        {
            dispatcher.MapModule(new WindowCommandFacade(new WindowCommandOptions
            {
                Window = this,
                ToggleMaximize = ToggleMaximize,      // the frameless manual work-area path
                IsMaximized = () => IsAppMaximized,   // WindowState never reflects it
                // P5.6: let the OS treat the page-drawn caption buttons as real ones, so Windows 11
                // offers Snap Layouts on maximize. The page reports its rects in CSS px relative to
                // the WebView2; the facade converts and this hands them to the window.
                CoordinateSpace = _webView,
                SetCaptionButtons = SetCaptionButtons,
            }));
            dispatcher.MapModule(new DropZoneFacade(_dropZones));

            // The route-builder shape (SampleFacade shows the BaseFacade shape): lease a pooled
            // off-screen session, render the requested page, and prove its JS ran (title + HTML
            // length come from the LIVE DOM, not the response bytes) — the e2e drives this.
            dispatcher.MapModule("RENDER", routes => routes.RouteAsync("PROBE", async (request, ct) =>
            {
                var url = PayloadHelper.GetRequiredValue<string>(request.Payload, "url");
                // Bound the lease: with Capacity 2, two wedged sessions would otherwise hang every
                // later PROBE request forever with no response. A real queue wait is fine; an
                // indefinite one is a structured RENDER_BUSY.
                using var leaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                RenderSession session;
                try { session = await _renderPool.LeaseAsync(leaseTimeout.Token); }
                catch (OperationCanceledException) { throw new OperationException("RENDER_BUSY", "url", url); }
                await using (session)
                {
                    try
                    {
                        await session.NavigateAsync(url);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                    {
                        // The guard (or the http/https gate) refused the data-driven URL — cross
                        // the bridge as a structured error, not as leaked exception text.
                        throw new OperationException("RENDER_REFUSED", "url", url);
                    }
                    var html = await session.GetHtmlAsync() ?? "";
                    var titleJson = await session.ExecuteScriptAsync("document.title") ?? "\"\"";
                    return new { Length = html.Length, Title = JsonSerializer.Deserialize<string>(titleJson) };
                }
            }));

            // ── P5.5 H9.5: the SEAM TEST for StreamingSession ────────────────────────────────────
            // The kit ships an off-screen browser that streams frames and takes synthetic input. It
            // ships NO transport, NO viewer and no opinion about what that is for. So the sample
            // builds the product — here, a co-browse pane — the same way the RENDER route above
            // builds a render service over the pool.
            //
            // If any of this needed an `internal`, the seam would be wrong (D21). It does not: every
            // call below is public API. The transport being the interesting part is the point —
            // frames are BINARY and this bridge is JSON, so the app base64s them into notifications.
            // A server-backed profile would push the same bytes down a WebSocket instead, and the
            // session would not know the difference.
            dispatcher.MapModule("STREAM", routes => routes
                .RouteAsync("START", async (request, ct) =>
                {
                    var url = PayloadHelper.GetRequiredValue<string>(request.Payload, "url");
                    if (_stream is not null) throw new OperationException("STREAM_ALREADY_RUNNING");

                    var session = await StreamingSession.StartAsync(new StreamingSessionOptions
                    {
                        Anchor = this,
                        Browser = new SessionBrowserOptions
                        {
                            ProfileDirectory = Path.Combine(paths.DataArea("sessions"), "stream"),
                            KeepAliveInBackground = true, // off-screen, but it must keep painting
                            // E1, and this is the demo that FOUND it: with the navigation guard fixed
                            // the session navigated happily to the packaged app's own virtual host and
                            // then rendered WebView2's "can't reach this page", because a session
                            // browser's environment carries none of the shell's serving. Passing the
                            // shell's own pair through is the whole adopter recipe — note it is the
                            // SAME provider instance, so the session's requests hit a warm cache.
                            VirtualHost = hostOptions.VirtualHost,
                            ResourceProvider = hostOptions.ResourceProvider,
                            // A blocking policy AND the app's own bundle on one session — the
                            // combination, because it takes a DIFFERENT code path: with a filter the
                            // host intercepts every request ("*") rather than just the bundle prefix,
                            // and the filter is consulted FIRST. So this doubles as the test that a
                            // sane policy stays QUIET on the app's own origin: block cross-host
                            // subresources, which is the shape an adopter actually writes. If the
                            // order or the wide registration were wrong, the pane would go blank.
                            RequestFilter = (request, page) =>
                                page is not null
                                && !string.Equals(request.Host, page.Host, StringComparison.OrdinalIgnoreCase),
                        },
                        // Allow the app's OWN origin, whichever it is — loopback while the dev server
                        // is serving, the virtual host once packaged. `IsLoopback` alone was the bug:
                        // it silently made this demo dev-only, and `dev.mjs sample` runs PACKAGED by
                        // default, so the button answered STREAM_REFUSED with no reason anywhere.
                        // An adopter copying this guard would inherit exactly that.
                        NavigationGuard = (uri, _) =>
                            Task.FromResult(uri.IsLoopback || uri.Host == "sample.local"),
                        // The lifecycle hook the app plugs into: tell the page WHY the stream
                        // stopped, so a crash and a deliberate STOP look different in the UI.
                        // It must also CLEAR our handle — a dead renderer ends the session without
                        // anyone calling STOP, and leaving `_stream` set would make every later
                        // START answer STREAM_ALREADY_RUNNING for the rest of the process.
                        OnEnded = ended =>
                        {
                            _stream = null;
                            _ = eventBus.EmitAsync("STREAM", "ENDED",
                                new { Reason = ended.Reason.ToString(), ended.Detail });
                        },
                    });

                    try
                    {
                        await session.Controller.NavigateAsync(url);
                    }
                    catch (Exception ex)
                    {
                        // The guard refused the URL (or navigation failed) AFTER a browser was
                        // already live. Without this the session leaks: a real off-screen window and
                        // a browser process holding the profile lock, with no handle left to reach it.
                        await session.DisposeAsync();
                        // LOG THE CAUSE HOST-SIDE. This catch used to be a bare `catch` that threw
                        // STREAM_REFUSED and dropped `ex` on the floor, so the page showed a code
                        // with no reason and the host log said nothing at all — the failure was
                        // undiagnosable from either end. Raw exception text still must not cross the
                        // wire (ipc-contracts), so the detail goes here and the page gets the code.
                        Console.WriteLine($"[sample] STREAM/START failed for '{url}': {ex}");
                        throw new OperationException("STREAM_REFUSED", "url", url);
                    }

                    _stream = session;
                    // The app owns the pump. Frames out as base64 notifications; the channel
                    // completing (dispose OR renderer death) ends the loop on its own. The try/catch
                    // is not decoration: a fault in here would otherwise surface only as an
                    // UNOBSERVED task exception, long after the fact and with no route to the page.
                    _streamPump = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var frame in session.Frames.ReadAllAsync())
                                await eventBus.EmitAsync("STREAM", "FRAME", new
                                {
                                    Jpeg = Convert.ToBase64String(frame.Jpeg),
                                    frame.Width,
                                    frame.Height,
                                });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[sample] stream pump stopped: {ex}");
                        }
                    });
                    return new { Started = true };
                })
                .RouteAsync("INPUT", async (request, ct) =>
                {
                    if (_stream is null) throw new OperationException("STREAM_NOT_RUNNING");
                    // The client speaks the kit's legacy wire shape here ON PURPOSE: it exercises
                    // the documented adoption shim, which is the migration path a real consumer
                    // takes. A greenfield app would build SessionInput records directly.
                    var json = PayloadHelper.GetRequiredValue<string>(request.Payload, "input");
                    if (!SessionInput.TryParseLegacyJson(json, out var input))
                        throw new OperationException("STREAM_BAD_INPUT");
                    await _stream.DispatchAsync(input!);
                    return null;
                })
                .RouteAsync("STOP", async (_, ct) =>
                {
                    var session = _stream;
                    _stream = null;
                    if (session is not null) await session.DisposeAsync();
                    return new { Stopped = true };
                }));
        }

        // Construct the bridge BEFORE InitializeAsync — bus buffering starts here, so events
        // emitted during the (slow) WebView2 init survive to the first post-ready batch.
        _bridge = new WebViewIpcBridge(_webView, new WebViewIpcBridgeOptions
        {
            Dispatcher = dispatcher,
            EventBus = eventBus,
            Log = Console.WriteLine,
            // The other end of the MAUI sample's declaration — SAME page contract, different answer.
            // Every name below is something THIS composition actually registered a few lines up
            // (WindowCommandFacade, DropZoneFacade, SecondaryWindows, TrayIcon, the STA dialogs), which
            // is the discipline the descriptor demands: advertising one the app never mapped renders a
            // button that throws when pressed. The mobile shell answers `[filePicker]` to the same
            // handshake, and one bundle renders correctly against both.
            Shell = new ShellInfo
            {
                Name = "winforms",
                Capabilities =
                [
                    ShellCapability.WindowChrome, ShellCapability.DropZones,
                    ShellCapability.FilePicker, ShellCapability.FolderPicker, ShellCapability.SavePicker,
                    ShellCapability.SecondaryWindows, ShellCapability.Tray,
                ],
            },
            // The parameter is NAMED rather than discarded: the body below needs  for a real
            // discard, and a  lambda parameter shadows it (CS0029 on assignment).
            OnClientReady = readyRequest =>
            {
                // No _dropZones.ClearAll() here any more: the kit clears zones on DOCUMENT CHANGE,
                // which cannot race the page the way a handshake-time reset did. This is the same
                // per-page-state reset the stream teardown below still has to do by hand.
                _tickTimer.Start();

                // The page measures its own title bar and reports the real rects from here on, so
                // the host's splash-time estimate must stop competing with it on resize.
                _pageOwnsCaptionButtons = true;
                _ = RunResourceSeamProbesAsync();

                // A live stream belongs to the page that STARTED it, and the handshake means a new
                // page just loaded — so tear it down here, exactly as the overlays above are.
                //
                // Found by RUNNING the sample: the viewer's React unmount cleanup is NOT enough,
                // because effect cleanups DO NOT RUN on a full page reload (F5, a Vite HMR reload,
                // a navigation) — the page simply goes away. The session kept streaming into a
                // channel nobody read, and every later START answered STREAM_ALREADY_RUNNING for
                // the rest of the process. The host is the only side that can observe a reload, via
                // this handshake; the page can only report an in-page unmount.
                //
                // There used to be a CaptionButtonStateChanged handler here, pushing hot/pressed to
                // the page as a CAPTION_BUTTON_STATE event so its CSS could render the affordance.
                // P5.6's hybrid retired it: the window now CLIPS those pixels out of the WebView2 and
                // paints the buttons itself (see CaptionButtonClip below), so anything the page drew
                // there is invisible and the state is the window's own business. The kit keeps the
                // callback for the un-clipped mode — a form whose caption strip no web view covers.
                var orphan = _stream;
                _stream = null;
                if (orphan is not null) _ = orphan.DisposeAsync();
            },
        });

        // Launcher-style tray (no close-to-tray: closing exits — keeps the e2e's graceful close).
        _tray = new TrayIcon(new TrayIconOptions
        {
            Window = this,
            CloseToTray = false,
            MenuColors = new TrayMenuColors
            {
                Surface = Background,
                Hover = Color.FromArgb(50, 50, 50),
                Border = Color.FromArgb(60, 60, 60),
                Accent = Color.FromArgb(127, 209, 140),
                Text = Color.FromArgb(236, 237, 242),
                DisabledText = Color.FromArgb(150, 151, 168),
            },
        });

        _host = new WebViewHost(_webView, hostOptions);

        // The D45 interceptor route, registered BEFORE InitializeAsync — which is the point of the
        // interceptor existing on the host from construction: an app composes its routes where it composes
        // everything else, not from inside a webview callback.
        _interceptorRoute = InterceptorProbe.Register(_host.Interceptor,
            Path.Combine(paths.DataArea("probe"), "files"));

        Load += OnLoadAsync;
    }

    /// <summary>The probe's file route. Disposed with the form, as an app's own routes would be.</summary>
    private readonly IDisposable _interceptorRoute;

    /// <summary>
    /// Set once the page has taken over reporting its own caption-button rects. Until then the host
    /// supplies an estimate so the buttons work behind the splash — after, the host must never
    /// overwrite the page's real measurement with a guess.
    /// </summary>
    private bool _pageOwnsCaptionButtons;

    /// <summary>
    /// The pre-page caption cluster: three 2.6rem buttons in a 2rem bar at the top right, which is
    /// the layout <c>App.tsx</c> uses. Duplicated here deliberately and ONLY for the window's first
    /// moments — without it the splash covers the caption and a slow or failing frontend leaves a
    /// window the user cannot minimise or close.
    /// </summary>
    private async Task RunResourceSeamProbesAsync()
    {
        var core = _webView.CoreWebView2;
        if (core is null)
        {
            Console.WriteLine("RANGE SEAM: SKIPPED");
            Console.WriteLine("INTERCEPTOR SEAM: SKIPPED");
            return;
        }

        // Both seams the desktop serves resources through: the app-scheme one (P6.6/P7.1) and the portable
        // interceptor pipeline (D45). Sequentially, because each polls the page for its own global and
        // interleaving them would make a timeout ambiguous.
        try { Console.WriteLine(await RangeSchemeProbe.RunAsync(core).ConfigureAwait(true)); }
        catch (Exception ex) { Console.WriteLine($"RANGE SEAM: FAIL - probe threw {ex.GetType().Name}: {ex.Message}"); }

        try { Console.WriteLine(await InterceptorProbe.RunAsync(core).ConfigureAwait(true)); }
        catch (Exception ex) { Console.WriteLine($"INTERCEPTOR SEAM: FAIL - probe threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private void ReportSplashCaptionButtons()
    {
        if (_pageOwnsCaptionButtons || !IsHandleCreated || IsDisposed) return;
        // CSS px -> physical px through the control's own DPI, exactly as WindowCommandFacade does
        // for the page's report; a constant here would be wrong on any scaled display.
        var scale = DpiHelper.ScaleFromDeviceDpi(DeviceDpi);
        var w = (int)Math.Round(2.6 * 16 * scale);
        var h = (int)Math.Round(2.0 * 16 * scale);
        var right = ClientSize.Width;
        if (w <= 0 || h <= 0 || right <= 0) return;
        SetCaptionButtons(
        [
            new CaptionButtonRegion(CaptionButtonKind.Minimize, new Rectangle(right - (3 * w), 0, w, h)),
            new CaptionButtonRegion(CaptionButtonKind.Maximize, new Rectangle(right - (2 * w), 0, w, h)),
            new CaptionButtonRegion(CaptionButtonKind.Close, new Rectangle(right - w, 0, w, h)),
        ]);
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
        // Before anything slow: make the window closable while the splash is up.
        ReportSplashCaptionButtons();

        // Actionable install prompt instead of an obscure EnsureCoreWebView2Async failure.
        if (!WebViewEnvironment.IsRuntimeAvailable())
        {
            MessageBox.Show(
                "The WebView2 Runtime is not installed.\n\n" +
                "Install the Evergreen WebView2 Runtime from Microsoft and start the app again.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        try
        {
            await _host.InitializeAsync();
            // Attach after init (the core must exist), BEFORE Navigate — hooking after
            // navigation would lose the page's earliest messages.
            _bridge.Attach();
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (_splash.IsDisposed) return;
                Controls.Remove(_splash);
                _splash.Dispose();
                if (!args.IsSuccess)
                {
                    // Never leave a silent dark window with a spinning splash — say what failed
                    // (typical: Vite not running in dev, or an empty wwwroot in a fresh clone).
                    MessageBox.Show(
                        $"The frontend failed to load ({args.WebErrorStatus}).\n\n" +
                        "Dev mode needs the Vite server (dev.mjs vite); packaged mode needs the " +
                        "bundle built (npm run build in samples/Shenora.Sample.Web) before the app build.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            _host.Navigate();
        }
        catch (Exception ex)
        {
            // The init-timeout guard and navigation config errors both land here with actionable
            // messages — surface them instead of leaving a silent dark window.
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Stop the flush timer + detach before the WebView goes down (the source app's
            // transport once kept posting into a torn-down WebView for the process lifetime).
            _tickTimer.Dispose();
            _interceptorRoute.Dispose();
            _dropZones.Dispose();
            _bridge.Dispose();
            _tray.Dispose();
            _renderPool.Dispose();
            // Fire-and-forget is right on the UI teardown path: DisposeAsync completes the frame
            // channel FIRST (so the pump below unwinds) and only then posts the UI cleanup, which a
            // stopped message loop may never run — awaiting it here could hang the close.
            _ = _stream?.DisposeAsync();
            _stream = null;
            _streamPump = null;
        }
        base.Dispose(disposing);
    }
}
