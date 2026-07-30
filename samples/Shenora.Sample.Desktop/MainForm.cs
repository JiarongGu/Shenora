using System.Text.Json;
using Shenora.Core;
using Shenora.Ipc;
using Shenora.WebView2;
using Shenora.WebView2.Sessions;
using Shenora.WinForms;
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
        })
    {
        Text = "Shenora Sample";
        MinimumSize = new Size(640, 420);

        _webView = new WebView2Control { Dock = DockStyle.Fill };
        Controls.Add(_webView);

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
            },
            Capacity = 2,
            NavigationGuard = (uri, _) => Task.FromResult(uri.IsLoopback),
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
            }));
            dispatcher.MapModule(new DropZoneFacade(_dropZones));

            // The route-builder shape (SampleFacade shows the BaseFacade shape): lease a pooled
            // off-screen session, render the requested page, and prove its JS ran (title + HTML
            // length come from the LIVE DOM, not the response bytes) — the e2e drives this.
            dispatcher.MapModule("RENDER", routes => routes.RouteAsync("PROBE", async request =>
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
                .RouteAsync("START", async request =>
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
                        },
                        NavigationGuard = (uri, _) => Task.FromResult(uri.IsLoopback),
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
                    catch
                    {
                        // The guard refused the URL (or navigation failed) AFTER a browser was
                        // already live. Without this the session leaks: a real off-screen window and
                        // a browser process holding the profile lock, with no handle left to reach it.
                        await session.DisposeAsync();
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
                .RouteAsync("INPUT", async request =>
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
                .RouteAsync("STOP", async _ =>
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
            OnClientReady = _ =>
            {
                // Every (re)load: stale overlays belong to the previous page — clear before the
                // new page re-registers its own. Then start the tick source.
                _dropZones.ClearAll();
                _tickTimer.Start();
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
        Load += OnLoadAsync;
    }

    private async void OnLoadAsync(object? sender, EventArgs e)
    {
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
