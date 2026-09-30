using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Core.Events;
using Shenora.Core.Sessions;

namespace Shenora.Chromium.Host;

/// <summary>
/// A browser a session drives in the Chromium shell (D91): windowless (CEF's off-screen rendering), in its profile's
/// request context, with no window and nothing of the app's own. Its policies are the session's hooks with their safe
/// defaults, and what it does is published as <see cref="SessionEvents"/>. Everything runs on CEF's UI thread except the
/// request filter, which CEF asks on its IO thread: it marshals there and answers CEF asynchronously, so the app's hooks
/// all run on the UI thread, as they do in the WinForms shell.
/// </summary>
internal sealed unsafe class ChromiumSessionBrowser : ISessionBrowser
{
    private readonly SessionBrowserOptions _options;
    private readonly Func<string?> _scope;
    private readonly Action<SessionProcessReport>? _onGone;
    private readonly Size _viewport;
    private readonly CefUiDispatcher _ui;
    private readonly Client _client;
    private _cef_browser_t* _browser;
    private DevToolsChannel? _devTools;
    private readonly List<IDisposable> _subscriptions = [];
    private int _filterErrorReported;
    private Action? _closed;

    public ChromiumSessionBrowser(SessionBrowserOptions options, Func<string?> scope, Action<SessionProcessReport>? onGone, Size viewport,
        CefUiDispatcher ui)
    {
        _options = options;
        _scope = scope;
        _onGone = onGone;
        _viewport = viewport;
        _surface = viewport;
        _ui = ui;
        _client = new Client(this);
    }

    /// <summary>The client to create the browser with, with the reference CEF takes added.</summary>
    public _cef_client_t* ClientForCef() => _client.ForCef();

    /// <summary>Called once the browser has gone, so its context can let the profile go. UI thread.</summary>
    public void WhenClosed(Action closed) => _closed = closed;

    // The session's own browser, by CEF's id for it. A popup the page opens gets this browser's client too, so every
    // callback asks whose browser it is about: a popup's must never be taken for the session's (its close, above all,
    // would tear this one's state down). 0 until known, when only this browser exists.
    private int _browserId;

    // The popups the page opened, allowed by OnWindowRequest: off-screen as this browser is, driven by nobody, and closed
    // with the session. Each holds a reference of ours.
    private readonly Dictionary<int, nint> _popups = [];

    private bool Mine(_cef_browser_t* browser) => browser != null && (_browserId == 0 || browser->get_identifier(browser) == _browserId);

    /// <summary>The browser exists (from <c>create_browser_sync</c>): keep it and open its DevTools channel. UI thread.</summary>
    public void Attach(_cef_browser_t* browser)
    {
        _browser = browser;   // the reference create_browser_sync returned, kept until the browser closes
        _browserId = browser->get_identifier(browser);
        using var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser));
        _devTools = new DevToolsChannel(host.Ptr);
        if (_options.MuteAudio) host.Ptr->set_audio_muted(host.Ptr, 1);
        host.Ptr->was_hidden(host.Ptr, 0);   // painting and timers run, as for a page on screen
        AnswerCredentialsThroughDevTools();
        ObserveThroughDevTools();
    }

    // ── ISessionBrowser ────────────────────────────────────────────────────────────────────────────────

    public string Source
    {
        get
        {
            if (_browser == null) return string.Empty;
            using var frame = new CefRef<_cef_frame_t>(_browser->get_main_frame(_browser));
            return frame.IsNull ? string.Empty : CefStrings.TakeUserFree(frame.Ptr->get_url(frame.Ptr));
        }
    }

    public event Action<SessionNavigationResult>? NavigationCompleted;

    public Func<string, bool>? CancelNavigation { get; set; }

    public bool CancelDownloads { get; set; }

    public void Navigate(string url)
    {
        if (_browser == null) return;
        using var frame = new CefRef<_cef_frame_t>(_browser->get_main_frame(_browser));
        if (frame.IsNull) return;
        fixed (char* u = url)
        {
            var target = CefStrings.View(u, url.Length);
            frame.Ptr->load_url(frame.Ptr, &target);
        }
    }

    /// <summary>The value JSON-encoded, as WebView2's <c>ExecuteScriptAsync</c> answers: <c>null</c> for a value that
    /// is undefined or cannot be encoded, and for a script that threw.</summary>
    public Task<string?> ExecuteScriptAsync(string javaScript) =>
        // A promise is not awaited, as WebView2 does not: it answers as the object it is.
        SessionCalls.Map<string?>(CallDevToolsAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = javaScript, returnByValue = true })), ScriptResult);

    /// <summary>What <c>Runtime.evaluate</c>'s answer means as a script's JSON-encoded value. Internal: tested.</summary>
    internal static string ScriptResult(string evaluateAnswer)
    {
        using var doc = JsonDocument.Parse(evaluateAnswer);
        var root = doc.RootElement;
        if (root.TryGetProperty("exceptionDetails", out _)) return "null";
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value)) return "null";
        return value.GetRawText();
    }

    public Task<string> CallDevToolsAsync(string method, string parametersJson)
    {
        if (_devTools is not { } devTools) return Task.FromException<string>(new ObjectDisposedException(nameof(ChromiumSessionBrowser)));
        FollowEmulatedDevice(method, parametersJson);
        return devTools.CallAsync(method, parametersJson);
    }

    // The off-screen surface, in device-independent pixels, and its scale: the page's window, since it has no other.
    private Size _surface;
    private float _scale = 1;

    /// <summary>
    /// A windowless browser has no window to emulate a device inside: the page lays out at an emulated size, but the
    /// surface (and so every screencast frame) stayed the browser's own, with the page in its corner at 1× (measured:
    /// an 800×600 emulation streamed as 1600×1100 frames). So the surface takes the emulated device's size and scale,
    /// as a window capture would: a session streaming frames maps its viewer's input against exactly what they show.
    /// </summary>
    private void FollowEmulatedDevice(string method, string parametersJson)
    {
        Size surface;
        float scale;
        if (method == "Emulation.clearDeviceMetricsOverride")
        {
            (surface, scale) = (_viewport, 1f);
        }
        else if (method == "Emulation.setDeviceMetricsOverride" && EmulatedDevice(parametersJson) is { } device)
        {
            (surface, scale) = device;
        }
        else return;
        if (surface == _surface && scale == _scale) return;
        (_surface, _scale) = (surface, scale);
        if (_browser == null) return;
        using var host = new CefRef<_cef_browser_host_t>(_browser->get_host(_browser));
        if (host.IsNull) return;
        host.Ptr->notify_screen_info_changed(host.Ptr);
        host.Ptr->was_resized(host.Ptr);
    }

    /// <summary>The size and scale an <c>Emulation.setDeviceMetricsOverride</c> asks for, or null when it names none.
    /// Internal: tested.</summary>
    internal static (Size Surface, float Scale)? EmulatedDevice(string parametersJson)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("width", out var w) || !root.TryGetProperty("height", out var h)) return null;
        var width = w.GetInt32();
        var height = h.GetInt32();
        if (width <= 0 || height <= 0) return null;   // 0 is the protocol's "leave the window's"
        var scale = root.TryGetProperty("deviceScaleFactor", out var s) && s.GetDouble() > 0 ? (float)s.GetDouble() : 1f;
        return (new Size(width, height), scale);
    }

    public IDisposable OnDevToolsEvent(string eventName, Action<string> onEvent) =>
        _devTools?.Subscribe(eventName, onEvent) ?? throw new ObjectDisposedException(nameof(ChromiumSessionBrowser));

    /// <summary>The cookies a request to <paramref name="origin"/> carries, from the protocol's own jar reader.</summary>
    public Task<IReadOnlyList<SessionCookie>> GetCookiesAsync(string origin) =>
        SessionCalls.Map(CallDevToolsAsync("Network.getCookies", JsonSerializer.Serialize(new { urls = new[] { origin } })), Cookies);

    internal static IReadOnlyList<SessionCookie> Cookies(string getCookiesAnswer)
    {
        using var doc = JsonDocument.Parse(getCookiesAnswer);
        var list = new List<SessionCookie>();
        if (!doc.RootElement.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in cookies.EnumerateArray())
            list.Add(new SessionCookie(Text(c, "name"), Text(c, "value"), Text(c, "domain"), Text(c, "path")));
        return list;

        static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    }

    public void Focus()
    {
        if (_browser == null) return;
        using var host = new CefRef<_cef_browser_host_t>(_browser->get_host(_browser));
        if (!host.IsNull) host.Ptr->set_focus(host.Ptr, 1);
    }

    public void Close()
    {
        if (_browser == null || _closing) return;
        _closing = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _devTools?.Dispose();
        _devTools = null;
        foreach (var popup in _popups.Values.ToArray()) ForceClose((_cef_browser_t*)popup);
        ForceClose(_browser);
    }

    // Forced: a session's page has no one to ask.
    private static void ForceClose(_cef_browser_t* browser)
    {
        using var host = new CefRef<_cef_browser_host_t>(browser->get_host(browser));
        if (!host.IsNull) host.Ptr->close_browser(host.Ptr, 1);
    }

    // A browser made from this one's client: the page's popup, which the hook allowed. UI thread.
    private void PopupCreated(_cef_browser_t* browser)
    {
        var id = browser->get_identifier(browser);
        ((_cef_base_ref_counted_t*)browser)->add_ref((_cef_base_ref_counted_t*)browser);
        _popups[id] = (nint)browser;
        if (_closing || _browser == null) ForceClose(browser);   // the session went while it was being made
    }

    private void PopupClosed(_cef_browser_t* browser)
    {
        if (_popups.Remove(browser->get_identifier(browser), out var kept))
            ((_cef_base_ref_counted_t*)kept)->release((_cef_base_ref_counted_t*)kept);
    }

    // ── what the browser reports ───────────────────────────────────────────────────────────────────────

    private void Publish(string type, Func<object?> payload, bool coalesce = false)
    {
        if (_options.Events is not { } bus) return;
        try
        {
            bus.Emit(new EventMessage
            {
                Module = SessionEvents.Module,
                Type = type,
                Scope = _scope(),
                Payload = payload(),
                // Only for the two whose payload is a full SNAPSHOT of "where am I"; coalescing the others would lose
                // events.
                CoalesceKey = coalesce ? type : null,
            });
        }
        catch (Exception ex)
        {
            SessionLog(l => l.LogError(ex, "Publishing session event {Type} failed.", type));
        }
    }

    // The title the display handler last reported: the one events carry.
    private string _title = string.Empty;

    // Set when the main frame's navigation failed, so its load-end (which CEF raises after a committed failure too) does
    // not report it again as a success. Reset as each main-frame navigation begins.
    private bool _navigationFailed;

    // Set as the session closes the browser, so that close is not reported as the page's own window.close().
    private bool _closing;

    private void SessionLog(Action<ILogger> write)
    {
        if (_options.Log is { } log) AppCallback.Run(() => write(log));
    }

    /// <summary>
    /// What CEF has no callback for, the protocol reports: the DOM being ready, a page's posted messages (through the
    /// same <c>chrome.webview.postMessage</c> a page written for WebView2 calls), and, when the app asked, responses.
    /// </summary>
    private void ObserveThroughDevTools()
    {
        if (_devTools is not { } devTools || _options.Events is null) return;
        _subscriptions.Add(devTools.Subscribe("Page.domContentEventFired", _ =>
            Publish(SessionEvents.DomContentLoaded, () => new SessionSource(Source, _title))));
        _subscriptions.Add(devTools.Subscribe("Runtime.bindingCalled", json =>
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("name").GetString() != WebMessageBinding) return;
            // Only the top document's posts are the page's, as WebView2 raises WebMessageReceived for it alone: a frame's
            // (an ad's, say) must not speak for the page.
            if (!_contextFrames.TryGetValue(root.GetProperty("executionContextId").GetInt32(), out var frame) || frame != _mainFrameId) return;
            var message = root.GetProperty("payload").GetString() ?? string.Empty;
            Publish(SessionEvents.WebMessage, () => new SessionWebMessage(message));
        }));
        _subscriptions.Add(devTools.Subscribe("Page.frameNavigated", json =>
        {
            using var doc = JsonDocument.Parse(json);
            var frame = doc.RootElement.GetProperty("frame");
            if (!frame.TryGetProperty("parentId", out _)) _mainFrameId = frame.GetProperty("id").GetString();
        }));
        _subscriptions.Add(devTools.Subscribe("Runtime.executionContextCreated", json =>
        {
            if (DefaultContext(json) is { } context) _contextFrames[context.Id] = context.FrameId;
        }));
        _subscriptions.Add(devTools.Subscribe("Runtime.executionContextDestroyed", json =>
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("executionContextId", out var id)) _contextFrames.Remove(id.GetInt32());
        }));
        _subscriptions.Add(devTools.Subscribe("Runtime.executionContextsCleared", _ => _contextFrames.Clear()));
        Setup(devTools, "Page.enable", "{}");
        Setup(devTools, "Runtime.enable", "{}");
        Setup(devTools, "Runtime.addBinding", JsonSerializer.Serialize(new { name = WebMessageBinding }));
        Setup(devTools, "Page.addScriptToEvaluateOnNewDocument", JsonSerializer.Serialize(new { source = WebMessageShim }));

        if (_options.ObserveResponse is null) return;
        _subscriptions.Add(devTools.Subscribe("Network.responseReceived", ResponseReceived));
        if (_options.ResponseBodySample > 0)
            _subscriptions.Add(devTools.Subscribe("Network.loadingFinished", LoadingFinished));
        Setup(devTools, "Network.enable", "{}");
    }

    /// <summary>
    /// HTTP credentials, answered through the protocol's Fetch domain: a windowless browser fails a challenge on its own
    /// (<c>ERR_INVALID_AUTH_CREDENTIALS</c>) without asking the request handler's <c>get_auth_credentials</c> (measured).
    /// Only a session that answers challenges pays for it, since Fetch pauses each of its requests once.
    /// </summary>
    private void AnswerCredentialsThroughDevTools()
    {
        if (_devTools is not { } devTools || _options.OnAuthRequest is null) return;
        _subscriptions.Add(devTools.Subscribe("Fetch.requestPaused", json =>
            _ = Observe(devTools.CallAsync("Fetch.continueRequest", JsonSerializer.Serialize(new { requestId = RequestId(json) })))));
        _subscriptions.Add(devTools.Subscribe("Fetch.authRequired", json =>
        {
            var (requestId, challenge) = AuthChallenge(json);
            var decided = SessionPolicy.Decide(_options.OnAuthRequest, challenge,
                ex => SessionLog(l => l.LogError(ex, "OnAuthRequest threw; cancelling.")));
            _ = Observe(devTools.CallAsync("Fetch.continueWithAuth", AuthAnswer(requestId, decided)));
        }));
        Setup(devTools, "Fetch.enable", """{"handleAuthRequests":true,"patterns":[{"urlPattern":"*"}]}""");
    }

    private static string RequestId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
    }

    /// <summary>A <c>Fetch.authRequired</c> event as the challenge the hook is asked. Internal: tested.</summary>
    internal static (string RequestId, SessionAuthRequest Challenge) AuthChallenge(string authRequiredJson)
    {
        using var doc = JsonDocument.Parse(authRequiredJson);
        var root = doc.RootElement;
        var uri = root.TryGetProperty("request", out var request) && request.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "";
        var challenge = root.GetProperty("authChallenge");
        var scheme = challenge.TryGetProperty("scheme", out var s) ? s.GetString() ?? "" : "";
        var realm = challenge.TryGetProperty("realm", out var r) ? r.GetString() ?? "" : "";
        // The header's shape, as WebView2 reports it; the protocol reports the scheme in lower case.
        if (scheme.Length > 0) scheme = char.ToUpperInvariant(scheme[0]) + scheme[1..];
        return (root.GetProperty("requestId").GetString() ?? "", new SessionAuthRequest(uri, $"{scheme} realm=\"{realm}\""));
    }

    /// <summary>The hook's answer as <c>Fetch.continueWithAuth</c>'s parameters: both halves set provide them, anything
    /// else cancels. Internal: tested.</summary>
    internal static string AuthAnswer(string requestId, SessionAuthRequest decided) =>
        decided.UserName is { } user && decided.Password is { } password
            ? JsonSerializer.Serialize(new { requestId, authChallengeResponse = new { response = "ProvideCredentials", username = user, password } })
            : JsonSerializer.Serialize(new { requestId, authChallengeResponse = new { response = "CancelAuth" } });

    // Each document's main-world context, by id, and the frame it belongs to; and the top frame's id.
    private readonly Dictionary<int, string> _contextFrames = [];
    private string? _mainFrameId;

    /// <summary>A <c>Runtime.executionContextCreated</c> event's context when it is a document's own (its main world),
    /// with the frame it belongs to; null for any other. Internal: tested.</summary>
    internal static (int Id, string FrameId)? DefaultContext(string executionContextCreatedJson)
    {
        using var doc = JsonDocument.Parse(executionContextCreatedJson);
        var context = doc.RootElement.GetProperty("context");
        if (!context.TryGetProperty("auxData", out var aux)
            || !aux.TryGetProperty("isDefault", out var isDefault) || isDefault.ValueKind != JsonValueKind.True
            || !aux.TryGetProperty("frameId", out var frame) || frame.GetString() is not { } frameId) return null;
        return (context.GetProperty("id").GetInt32(), frameId);
    }

    private const string WebMessageBinding = "__shenoraSessionPost";

    // The page's side of SessionEvents.WebMessage: WebView2's postMessage, forwarded to the binding as a string. The
    // binding is looked up as the page posts: a new document runs this before Chromium has put the binding there
    // (measured: read at document start it was absent, and no page could post).
    private const string WebMessageShim =
        "(() => { globalThis.chrome = globalThis.chrome || {}; if (globalThis.chrome.webview) return;"
        + " globalThis.chrome.webview = { postMessage: m => globalThis." + WebMessageBinding
        + "(typeof m === 'string' ? m : JSON.stringify(m)) }; })();";

    private readonly Dictionary<string, SessionResponse> _awaitingBodies = new(StringComparer.Ordinal);

    private void ResponseReceived(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var response = root.GetProperty("response");
        var url = response.GetProperty("url").GetString() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        // App code on the per-response path: a throw means "do not report" rather than "report everything".
        try { if (!_options.ObserveResponse!(uri)) return; }
        catch (Exception ex)
        {
            SessionLog(l => l.LogError(ex, "ObserveResponse threw; not reporting {Uri}.", uri));
            return;
        }
        var headers = new List<KeyValuePair<string, string>>();
        if (response.TryGetProperty("headers", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var header in map.EnumerateObject())
                // The protocol folds a repeated header into one value, lines apart: split them back, so Set-Cookie
                // arrives as the several headers it was.
                foreach (var value in (header.Value.GetString() ?? string.Empty).Split('\n'))
                    headers.Add(new(header.Name, value));
        var described = new SessionResponse(url, response.GetProperty("status").GetInt32(),
            response.TryGetProperty("statusText", out var text) ? text.GetString() ?? string.Empty : string.Empty, headers, string.Empty);
        if (_options.ResponseBodySample <= 0)
        {
            Publish(SessionEvents.ResponseReceived, () => described);
            return;
        }
        _awaitingBodies[root.GetProperty("requestId").GetString() ?? string.Empty] = described;
    }

    private void LoadingFinished(string json)
    {
        string requestId;
        using (var doc = JsonDocument.Parse(json)) requestId = doc.RootElement.GetProperty("requestId").GetString() ?? string.Empty;
        if (!_awaitingBodies.Remove(requestId, out var described)) return;
        var limit = Math.Min(_options.ResponseBodySample, SessionPolicy.MaxBodySample);
        SessionCalls.Then(CallDevToolsAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId })),
            answer => Publish(SessionEvents.ResponseReceived, () => described with { BodySample = BodySample(answer, limit) }),
            // A body already gone, or one the protocol will not give: the response is reported anyway, with no sample.
            ex =>
            {
                SessionLog(l => l.LogDebug(ex, "No body sample for {Uri}.", described.Uri));
                Publish(SessionEvents.ResponseReceived, () => described);
            });
    }

    /// <summary>The first <paramref name="limit"/> characters of a <c>Network.getResponseBody</c> answer's body.</summary>
    internal static string BodySample(string getResponseBodyAnswer, int limit)
    {
        using var doc = JsonDocument.Parse(getResponseBodyAnswer);
        var raw = doc.RootElement.GetProperty("body").GetString() ?? string.Empty;
        if (doc.RootElement.TryGetProperty("base64Encoded", out var encoded) && encoded.GetBoolean())
            raw = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
        return raw.Length <= limit ? raw : raw[..limit];
    }

    private static Task Observe(Task<string> call) => call.ContinueWith(static t => { var observed = t.Exception; },
        TaskContinuationOptions.OnlyOnFaulted);

    private readonly List<Task> _setup = [];

    /// <summary>
    /// Once the DevTools setup has been answered: what it installs (the page's <c>postMessage</c>, the Fetch domain)
    /// reaches only documents made after that, and the first navigation otherwise races it (measured: the shim missing
    /// from the first page and present on the next). Completes whether or not each call succeeded; a refusal is logged.
    /// </summary>
    public Task Ready => Task.WhenAll(_setup);

    private void Setup(DevToolsChannel devTools, string method, string parametersJson) =>
        _setup.Add(devTools.CallAsync(method, parametersJson).ContinueWith(t =>
        {
            if (t.Exception is { } ex) SessionLog(l => l.LogWarning(ex.GetBaseException(), "Session browser setup {Method} was refused.", method));
        }, TaskScheduler.Default));

    // ── from CEF's callbacks ───────────────────────────────────────────────────────────────────────────

    private void BeforeClose()
    {
        if (_browser == null) return;
        foreach (var popup in _popups.Values.ToArray()) ForceClose((_cef_browser_t*)popup);   // gone with the page that opened them
        using (new CefRef<_cef_browser_t>(_browser)) _browser = null;
        _devTools?.Dispose();
        _devTools = null;
        _client.Release();
        var closed = _closed;
        _closed = null;
        closed?.Invoke();
    }

    private void RendererGone(cef_termination_status_t status, int errorCode)
    {
        var report = new SessionProcessReport("RenderProcessExited", status.ToString(), errorCode, Terminal: true);
        SessionLog(l => l.LogWarning("Session browser renderer terminated: {Status}, code {Code}.", status, errorCode));
        Publish(SessionEvents.ProcessFailed, () => report);
        if (_onGone is { } gone) AppCallback.Run(() => gone(report));
    }

    // The request filter (RequestFilter), asked on the UI thread as in the WinForms shell. FAILS OPEN, reported once.
    private bool Blocks(string requestUrl)
    {
        if (_options.RequestFilter is not { } filter) return false;
        return SessionPolicy.ShouldBlockRequest(requestUrl, Source, filter, ex =>
        {
            if (Interlocked.Exchange(ref _filterErrorReported, 1) != 0) return;
            SessionLog(l => l.LogError(ex, "The session's RequestFilter threw and the request was ALLOWED (fail-open). Every later "
                + "throw from it is also allowed and will not be logged again: if this filter is your blocking policy, it is not blocking."));
        });
    }

    // ── the structs CEF sees ──────────────────────────────────────────────────────────────────────────

    private sealed class Client : CefObject<_cef_client_t>
    {
        private readonly LifeSpan _lifeSpan;
        private readonly Load _load;
        private readonly Display _display;
        private readonly Requests _requests;
        private readonly Dialogs _dialogs;
        private readonly Permissions _permissions;
        private readonly Downloads _downloads;
        private readonly Render _render;

        public Client(ChromiumSessionBrowser owner)
        {
            _lifeSpan = new LifeSpan(owner);
            _load = new Load(owner);
            _display = new Display(owner);
            _requests = new Requests(owner);
            _dialogs = new Dialogs(owner);
            _permissions = new Permissions(owner);
            _downloads = new Downloads(owner);
            _render = new Render(owner);
            Struct->get_life_span_handler = &GetLifeSpan;
            Struct->get_load_handler = &GetLoad;
            Struct->get_display_handler = &GetDisplay;
            Struct->get_request_handler = &GetRequests;
            Struct->get_jsdialog_handler = &GetDialogs;
            Struct->get_permission_handler = &GetPermissions;
            Struct->get_download_handler = &GetDownloads;
            Struct->get_render_handler = &GetRender;
        }

        private protected override void OnFreed()
        {
            _lifeSpan.Release();
            _load.Release();
            _display.Release();
            _requests.Release();
            _dialogs.Release();
            _permissions.Release();
            _downloads.Release();
            _render.Release();
        }

        [UnmanagedCallersOnly] private static _cef_life_span_handler_t* GetLifeSpan(_cef_client_t* self) => From<Client>(self)._lifeSpan.ForCef();
        [UnmanagedCallersOnly] private static _cef_load_handler_t* GetLoad(_cef_client_t* self) => From<Client>(self)._load.ForCef();
        [UnmanagedCallersOnly] private static _cef_display_handler_t* GetDisplay(_cef_client_t* self) => From<Client>(self)._display.ForCef();
        [UnmanagedCallersOnly] private static _cef_request_handler_t* GetRequests(_cef_client_t* self) => From<Client>(self)._requests.ForCef();
        [UnmanagedCallersOnly] private static _cef_jsdialog_handler_t* GetDialogs(_cef_client_t* self) => From<Client>(self)._dialogs.ForCef();
        [UnmanagedCallersOnly] private static _cef_permission_handler_t* GetPermissions(_cef_client_t* self) => From<Client>(self)._permissions.ForCef();
        [UnmanagedCallersOnly] private static _cef_download_handler_t* GetDownloads(_cef_client_t* self) => From<Client>(self)._downloads.ForCef();
        [UnmanagedCallersOnly] private static _cef_render_handler_t* GetRender(_cef_client_t* self) => From<Client>(self)._render.ForCef();
    }

    private sealed class LifeSpan : CefObject<_cef_life_span_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public LifeSpan(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->on_before_popup = &BeforePopup;
            Struct->on_after_created = &AfterCreated;
            Struct->do_close = &DoClose;
            Struct->on_before_close = &BeforeClose;
        }

        [UnmanagedCallersOnly]
        private static void AfterCreated(_cef_life_span_handler_t* self, _cef_browser_t* browser)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<LifeSpan>(self)._owner;
            if (owner._browserId == 0) owner._browserId = browser->get_identifier(browser);   // the session's own, first
            else if (!owner.Mine(browser))
            {
                var popup = (nint)browser;
                AppCallback.Run(() => owner.PopupCreated((_cef_browser_t*)popup));
            }
        }

        // window.open and target=_blank. Null OnWindowRequest SUPPRESSES; a hook may allow it, and CEF then opens it
        // windowless too, as this browser is. Returning 1 cancels.
        [UnmanagedCallersOnly]
        private static int BeforePopup(_cef_life_span_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, int popupId,
            _cef_string_utf16_t* targetUrl, _cef_string_utf16_t* targetFrameName, cef_window_open_disposition_t disposition, int userGesture,
            _cef_popup_features_t* features, _cef_window_info_t* windowInfo, _cef_client_t** client, _cef_browser_settings_t* settings,
            _cef_dictionary_value_t** extraInfo, int* noJavascriptAccess)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var owner = From<LifeSpan>(self)._owner;
            var request = SessionPolicy.Decide(owner._options.OnWindowRequest, new SessionWindowRequest(CefStrings.Read(targetUrl), userGesture == 1),
                ex => owner.SessionLog(l => l.LogError(ex, "OnWindowRequest threw; suppressing.")));
            if (request.Allow) return 0;
            owner.SessionLog(l => l.LogDebug("Session browser suppressed a new-window request for {Uri}.", request.Uri));
            return 1;
        }

        // CEF is about to close the browser: the session closing it, or the page's own window.close() (which Chromium
        // allows only a window script opened). Only the page's is reported. 0 lets the close go ahead, as a windowless
        // browser's must.
        [UnmanagedCallersOnly]
        private static int DoClose(_cef_life_span_handler_t* self, _cef_browser_t* browser)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<LifeSpan>(self)._owner;
            if (!owner._closing && owner.Mine(browser)) owner.Publish(SessionEvents.WindowCloseRequested, () => null);
            return 0;
        }

        [UnmanagedCallersOnly]
        private static void BeforeClose(_cef_life_span_handler_t* self, _cef_browser_t* browser)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<LifeSpan>(self)._owner;
            if (owner.Mine(browser)) AppCallback.Run(owner.BeforeClose);
            else
            {
                var popup = (nint)browser;
                AppCallback.Run(() => owner.PopupClosed((_cef_browser_t*)popup));
            }
        }
    }

    private sealed class Load : CefObject<_cef_load_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Load(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->on_load_end = &LoadEnd;
            Struct->on_load_error = &LoadError;
        }

        [UnmanagedCallersOnly]
        private static void LoadEnd(_cef_load_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, int httpStatusCode)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var owner = From<Load>(self)._owner;
            if (frame == null || frame->is_main(frame) != 1 || !owner.Mine(browser)) return;
            if (owner._navigationFailed) return;   // already reported, as the failure it was
            AppCallback.Run(() => owner.Completed(new SessionNavigationResult(owner.Source, true, "Unknown")));
        }

        [UnmanagedCallersOnly]
        private static void LoadError(_cef_load_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, cef_errorcode_t errorCode,
            _cef_string_utf16_t* errorText, _cef_string_utf16_t* failedUrl)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var owner = From<Load>(self)._owner;
            if (frame == null || frame->is_main(frame) != 1 || !owner.Mine(browser)) return;
            owner._navigationFailed = true;
            var url = CefStrings.Read(failedUrl);
            AppCallback.Run(() => owner.Completed(new SessionNavigationResult(url, false, errorCode.ToString())));
        }
    }

    private void Completed(SessionNavigationResult result)
    {
        Publish(SessionEvents.NavigationCompleted, () => result);
        NavigationCompleted?.Invoke(result);
    }

    private sealed class Display : CefObject<_cef_display_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Display(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->on_address_change = &AddressChanged;
            Struct->on_title_change = &TitleChanged;
        }

        // The SPA signal: history.pushState changes the address with no navigation at all.
        [UnmanagedCallersOnly]
        private static void AddressChanged(_cef_display_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, _cef_string_utf16_t* url)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            var owner = From<Display>(self)._owner;
            if (frame == null || frame->is_main(frame) != 1 || !owner.Mine(browser)) return;
            var address = CefStrings.Read(url);
            owner.Publish(SessionEvents.SourceChanged, () => new SessionSource(address, owner._title), coalesce: true);
        }

        [UnmanagedCallersOnly]
        private static void TitleChanged(_cef_display_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* title)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<Display>(self)._owner;
            if (!owner.Mine(browser)) return;
            owner._title = CefStrings.Read(title);
            owner.Publish(SessionEvents.TitleChanged, () => new SessionSource(owner.Source, owner._title), coalesce: true);
        }
    }

    private sealed class Requests : CefObject<_cef_request_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;
        private readonly Filter _filter;

        public Requests(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            _filter = new Filter(owner);
            Struct->on_before_browse = &BeforeBrowse;
            Struct->get_resource_request_handler = &GetResourceRequestHandler;
            Struct->on_select_client_certificate = &SelectCertificate;
            Struct->on_render_process_terminated = &RendererTerminated;
        }

        private protected override void OnFreed() => _filter.Release();

        // Every main-frame navigation, redirects included: reported, then asked of the session's policy (the pool's
        // cross-authority rule). The policy is guarded, and a throw CANCELS. Returning 1 cancels.
        [UnmanagedCallersOnly]
        private static int BeforeBrowse(_cef_request_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, _cef_request_t* request,
            int userGesture, int isRedirect)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var r = new CefRef<_cef_request_t>(request);
            var owner = From<Requests>(self)._owner;
            // A popup's navigations are its own, as a new window's are in WebView2: neither reported nor guarded here.
            if (frame == null || frame->is_main(frame) != 1 || !owner.Mine(browser)) return 0;
            var url = CefStrings.TakeUserFree(request->get_url(request));
            if (isRedirect == 0) owner._navigationFailed = false;   // a new navigation, which has not failed yet
            owner.Publish(SessionEvents.NavigationStarting, () => new SessionSource(url, owner._title));
            if (owner.CancelNavigation is not { } cancel) return 0;
            try { return cancel(url) ? 1 : 0; }
            catch { return 1; }
        }

        // CEF's IO thread. Only a session with a request filter looks at its requests at all.
        [UnmanagedCallersOnly]
        private static _cef_resource_request_handler_t* GetResourceRequestHandler(_cef_request_handler_t* self, _cef_browser_t* browser,
            _cef_frame_t* frame, _cef_request_t* request, int isNavigation, int isDownload, _cef_string_utf16_t* initiator, int* disableDefault)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var r = new CefRef<_cef_request_t>(request);
            var me = From<Requests>(self);
            return me._owner._options.RequestFilter is null ? null : me._filter.ForCef();
        }

        // UI thread. Null OnCertificateRequest presents NONE, which fails the handshake rather than hanging it.
        [UnmanagedCallersOnly]
        private static int SelectCertificate(_cef_request_handler_t* self, _cef_browser_t* browser, int isProxy, _cef_string_utf16_t* host, int port,
            nuint certificatesCount, _cef_x509_certificate_t** certificates, _cef_select_client_certificate_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_select_client_certificate_callback_t>(callback);
            var owner = From<Requests>(self)._owner;
            var subjects = new List<string>((int)certificatesCount);
            for (nuint i = 0; i < certificatesCount; i++)
            {
                var certificate = certificates[i];
                using var subject = new CefRef<_cef_x509_cert_principal_t>(certificate->get_subject(certificate));
                subjects.Add(subject.IsNull ? string.Empty : CefStrings.TakeUserFree(subject.Ptr->get_display_name(subject.Ptr)));
            }
            var request = SessionPolicy.Decide(owner._options.OnCertificateRequest, new SessionCertificateRequest(CefStrings.Read(host), port, subjects),
                ex => owner.SessionLog(l => l.LogError(ex, "OnCertificateRequest threw; cancelling.")));
            if (request.SelectedIndex is { } index && index >= 0 && index < (int)certificatesCount)
            {
                var chosen = certificates[index];
                ((_cef_base_ref_counted_t*)chosen)->add_ref((_cef_base_ref_counted_t*)chosen);   // select consumes one
                callback->select(callback, chosen);
            }
            else callback->select(callback, null);
            return 1;
        }

        [UnmanagedCallersOnly]
        private static void RendererTerminated(_cef_request_handler_t* self, _cef_browser_t* browser, cef_termination_status_t status, int errorCode,
            _cef_string_utf16_t* errorText)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<Requests>(self)._owner;
            if (!owner.Mine(browser)) return;   // a popup's renderer: the session's page lives on
            AppCallback.Run(() => owner.RendererGone(status, errorCode));
        }
    }

    /// <summary>The request filter, one handler for every request of a session that has one. CEF's IO thread.</summary>
    private sealed class Filter : CefObject<_cef_resource_request_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Filter(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->on_before_resource_load = &BeforeResourceLoad;
        }

        // Asked on the UI thread, where the filter runs in both shells, and answered from there. A blocked request is
        // cancelled: it fails as blocked, where WebView2 answers an empty 403.
        [UnmanagedCallersOnly]
        private static cef_return_value_t BeforeResourceLoad(_cef_resource_request_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame,
            _cef_request_t* request, _cef_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var r = new CefRef<_cef_request_t>(request);
            using var c = new CefRef<_cef_callback_t>(callback);
            var owner = From<Filter>(self)._owner;
            var url = CefStrings.TakeUserFree(request->get_url(request));
            ((_cef_base_ref_counted_t*)callback)->add_ref((_cef_base_ref_counted_t*)callback);   // kept for the answer
            var answer = (nint)callback;
            var posted = owner._ui.Queue(() =>
            {
                var blocked = AppCallback.RunOrDefault(() => owner.Blocks(url), fallback: false);
                using var kept = new CefRef<_cef_callback_t>((_cef_callback_t*)answer);
                if (blocked) kept.Ptr->cancel(kept.Ptr);
                else kept.Ptr->cont(kept.Ptr);
                return Task.CompletedTask;
            });
            if (posted) return cef_return_value_t.RV_CONTINUE_ASYNC;
            // The UI thread is gone: let it through, as the filter's own failure does, and drop the answer kept for it.
            using (new CefRef<_cef_callback_t>(callback)) { }
            return cef_return_value_t.RV_CONTINUE;
        }
    }

    /// <summary>The three dialogs a page can raise that wedge an unseen browser for good if nothing answers them.</summary>
    private sealed class Dialogs : CefObject<_cef_jsdialog_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Dialogs(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->on_jsdialog = &JsDialog;
            Struct->on_before_unload_dialog = &BeforeUnload;
        }

        // Null OnScriptDialog DISMISSES. Returning 1 with the callback run answers the dialog here and now.
        [UnmanagedCallersOnly]
        private static int JsDialog(_cef_jsdialog_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* originUrl, cef_jsdialog_type_t type,
            _cef_string_utf16_t* messageText, _cef_string_utf16_t* defaultPromptText, _cef_jsdialog_callback_t* callback, int* suppressMessage)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_jsdialog_callback_t>(callback);
            var owner = From<Dialogs>(self)._owner;
            var kind = type switch
            {
                cef_jsdialog_type_t.JSDIALOGTYPE_ALERT => "Alert",
                cef_jsdialog_type_t.JSDIALOGTYPE_CONFIRM => "Confirm",
                _ => "Prompt",
            };
            var dialog = SessionPolicy.Decide(owner._options.OnScriptDialog,
                new SessionScriptDialog(kind, CefStrings.Read(originUrl), CefStrings.Read(messageText), CefStrings.Read(defaultPromptText)),
                ex => owner.SessionLog(l => l.LogError(ex, "OnScriptDialog threw; dismissing.")));
            Answer(callback, dialog.Accept, kind == "Prompt" ? dialog.ResultText : string.Empty);
            return 1;
        }

        [UnmanagedCallersOnly]
        private static int BeforeUnload(_cef_jsdialog_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* messageText, int isReload,
            _cef_jsdialog_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_jsdialog_callback_t>(callback);
            var owner = From<Dialogs>(self)._owner;
            var dialog = SessionPolicy.Decide(owner._options.OnScriptDialog,
                new SessionScriptDialog("BeforeUnload", owner.Source, CefStrings.Read(messageText), string.Empty),
                ex => owner.SessionLog(l => l.LogError(ex, "OnScriptDialog threw; dismissing.")));
            Answer(callback, dialog.Accept, string.Empty);
            return 1;
        }

        private static void Answer(_cef_jsdialog_callback_t* callback, bool accept, string text)
        {
            fixed (char* t = text)
            {
                var input = CefStrings.View(t, text.Length);
                callback->cont(callback, accept ? 1 : 0, &input);
            }
        }
    }

    /// <summary>Null OnPermissionRequest DENIES: an unseen page cannot meaningfully ask, and an unanswered request
    /// stalls whatever asked.</summary>
    private sealed class Permissions : CefObject<_cef_permission_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Permissions(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->on_show_permission_prompt = &ShowPrompt;
            Struct->on_request_media_access_permission = &CaptureAccess;
        }

        // One prompt can carry several kinds, and is answered as a whole: accepted only when every kind is granted.
        [UnmanagedCallersOnly]
        private static int ShowPrompt(_cef_permission_handler_t* self, _cef_browser_t* browser, ulong promptId, _cef_string_utf16_t* origin,
            uint requested, _cef_permission_prompt_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var c = new CefRef<_cef_permission_prompt_callback_t>(callback);
            var owner = From<Permissions>(self)._owner;
            var page = CefStrings.Read(origin);
            var allow = true;
            foreach (var (_, kind) in PermissionKinds(requested)) allow &= owner.Allows(kind, page);
            callback->cont(callback, allow ? cef_permission_request_result_t.CEF_PERMISSION_RESULT_ACCEPT : cef_permission_request_result_t.CEF_PERMISSION_RESULT_DENY);
            return 1;
        }

        // Camera and microphone are granted apart, as WebView2 asks for them apart.
        [UnmanagedCallersOnly]
        private static int CaptureAccess(_cef_permission_handler_t* self, _cef_browser_t* browser, _cef_frame_t* frame, _cef_string_utf16_t* origin,
            uint requested, _cef_media_access_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var f = new CefRef<_cef_frame_t>(frame);
            using var c = new CefRef<_cef_media_access_callback_t>(callback);
            var owner = From<Permissions>(self)._owner;
            var page = CefStrings.Read(origin);
            uint granted = 0;
            foreach (var (bit, kind) in MediaKinds(requested))
                if (owner.Allows(kind, page)) granted |= bit;
            callback->cont(callback, granted);
            return 1;
        }
    }

    /// <summary>
    /// The kinds a permission prompt carries, each by the name WebView2 gives it (<c>CoreWebView2PermissionKind</c>),
    /// so a hook written for one shell reads the same in the other. A kind WebView2 has no name for keeps CEF's, as
    /// PascalCase, and a bit CEF has none for either is <c>UnknownPermission</c>. Internal: tested.
    /// </summary>
    internal static IEnumerable<(uint Bit, string Kind)> PermissionKinds(uint requested)
    {
        for (var bit = 1u; bit != 0 && bit <= requested; bit <<= 1)
        {
            if ((requested & bit) == 0) continue;
            var type = (cef_permission_request_types_t)bit;
            yield return (bit, type switch
            {
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION => "Geolocation",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM => "Camera",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM => "Microphone",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS => "Notifications",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_SENSORS => "OtherSensors",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD => "ClipboardRead",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_MULTIPLE_DOWNLOADS => "MultipleAutomaticDownloads",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_FILE_SYSTEM_ACCESS => "FileReadWrite",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_FONTS => "LocalFonts",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX => "MidiSystemExclusiveMessages",
                cef_permission_request_types_t.CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT => "WindowManagement",
                _ => Pascal(type.ToString(), "CEF_PERMISSION_TYPE_"),
            });
        }
    }

    /// <summary>The kinds a media request carries, named as <see cref="PermissionKinds"/> names them. Internal: tested.</summary>
    internal static IEnumerable<(uint Bit, string Kind)> MediaKinds(uint requested)
    {
        for (var bit = 1u; bit != 0 && bit <= requested; bit <<= 1)
        {
            if ((requested & bit) == 0) continue;
            var type = (cef_media_access_permission_types_t)bit;
            yield return (bit, type switch
            {
                cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE => "Microphone",
                cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE => "Camera",
                _ => Pascal(type.ToString(), "CEF_MEDIA_PERMISSION_"),
            });
        }
    }

    // CEF_PERMISSION_TYPE_CAMERA_PAN_TILT_ZOOM → CameraPanTiltZoom; a bit CEF has no name for is WebView2's
    // UnknownPermission.
    private static string Pascal(string name, string prefix) =>
        !name.StartsWith(prefix, StringComparison.Ordinal) ? "UnknownPermission"
        : string.Concat(name[prefix.Length..].Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word[..1] + word[1..].ToLowerInvariant()));

    private bool Allows(string kind, string origin)
    {
        // CEF raises no user-gesture flag with a prompt; false is the honest answer.
        var request = SessionPolicy.Decide(_options.OnPermissionRequest, new SessionPermissionRequest(kind, origin, UserInitiated: false),
            ex => SessionLog(l => l.LogError(ex, "OnPermissionRequest threw; denying.")));
        SessionLog(l => l.LogDebug("Session browser {Decision} permission {Kind}.", request.Allow ? "granted" : "denied", kind));
        return request.Allow;
    }

    /// <summary>A page-started download: always reported, and cancelled when the session's controller hands downloads to
    /// the app (<see cref="CancelDownloads"/>).</summary>
    private sealed class Downloads : CefObject<_cef_download_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Downloads(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->can_download = &CanDownload;
            Struct->on_before_download = &BeforeDownload;
        }

        // CEF's point to cancel one. A download the session hands to the app is reported here, with no file name yet;
        // one it lets run is reported as it starts, with the name. Returning 0 cancels.
        [UnmanagedCallersOnly]
        private static int CanDownload(_cef_download_handler_t* self, _cef_browser_t* browser, _cef_string_utf16_t* url, _cef_string_utf16_t* method)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<Downloads>(self)._owner;
            if (!owner.CancelDownloads) return 1;
            if (!owner.Mine(browser)) return 0;   // a popup's: cancelled with the session's, and not the session's to report
            var address = CefStrings.Read(url);
            owner.Publish(SessionEvents.DownloadStarting, () => new DownloadHit(address, null));
            return 0;
        }

        // 0: CEF's default path.
        [UnmanagedCallersOnly]
        private static int BeforeDownload(_cef_download_handler_t* self, _cef_browser_t* browser, _cef_download_item_t* item,
            _cef_string_utf16_t* suggestedName, _cef_before_download_callback_t* callback)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            using var i = new CefRef<_cef_download_item_t>(item);
            using var c = new CefRef<_cef_before_download_callback_t>(callback);
            var owner = From<Downloads>(self)._owner;
            if (!owner.Mine(browser)) return 0;
            var url = CefStrings.TakeUserFree(item->get_url(item));
            var name = CefStrings.Read(suggestedName);
            owner.Publish(SessionEvents.DownloadStarting, () => new DownloadHit(url, string.IsNullOrEmpty(name) ? null : name));
            return 0;
        }
    }

    /// <summary>The off-screen surface: its size and scale, and nothing done with its pixels, which the DevTools
    /// screencast reads when a session streams.</summary>
    private sealed class Render : CefObject<_cef_render_handler_t>
    {
        private readonly ChromiumSessionBrowser _owner;

        public Render(ChromiumSessionBrowser owner)
        {
            _owner = owner;
            Struct->get_view_rect = &ViewRect;
            Struct->get_screen_info = &ScreenInfo;
        }

        [UnmanagedCallersOnly]
        private static void ViewRect(_cef_render_handler_t* self, _cef_browser_t* browser, _cef_rect_t* rect)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            *rect = Surface(From<Render>(self)._owner);
        }

        // The surface is its own screen, at the scale the session asked for. Returning 1 says it was filled in.
        [UnmanagedCallersOnly]
        private static int ScreenInfo(_cef_render_handler_t* self, _cef_browser_t* browser, _cef_screen_info_t* info)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var owner = From<Render>(self)._owner;
            info->device_scale_factor = owner._scale;
            info->rect = Surface(owner);
            info->available_rect = info->rect;
            return 1;
        }

        private static _cef_rect_t Surface(ChromiumSessionBrowser owner) =>
            new() { x = 0, y = 0, width = Math.Max(1, owner._surface.Width), height = Math.Max(1, owner._surface.Height) };
    }
}

/// <summary>The session browser's awaits, which an <c>unsafe</c> type cannot hold. Continuations come back to CEF's UI
/// thread, as the caller's did.</summary>
internal static class SessionCalls
{
    public static async Task<TOut> Map<TOut>(Task<string> call, Func<string, TOut> map) => map(await call.ConfigureAwait(true));

    /// <summary>
    /// Make the browser on the UI thread once its profile is <paramref name="ready"/>, and hand it over once its own setup
    /// is, all within <paramref name="budget"/>; <paramref name="abandon"/> when the profile faults, or the wait is
    /// cancelled or runs out first.
    /// </summary>
    public static async Task<ISessionBrowser> WhenReady(Task ready, string profile, TimeSpan budget, CancellationToken cancellationToken,
        CefUiDispatcher ui, Func<ChromiumSessionBrowser> make, Action abandon)
    {
        var deadline = DateTime.UtcNow + budget;
        try { await ready.WaitAsync(budget, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            AppCallback.Run(abandon);
            throw new TimeoutException($"The session profile '{profile}' did not open within {budget}.");
        }
        catch
        {
            AppCallback.Run(abandon);
            throw;
        }
        var browser = await ui.InvokeAsync(() => Task.FromResult(make()), CancellationToken.None).ConfigureAwait(false);
        var left = deadline - DateTime.UtcNow;
        Exception? failed = null;
        try { await browser.Ready.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { failed = new TimeoutException($"The session browser over '{profile}' was not ready within {budget}."); }
        catch (OperationCanceledException ex) { failed = ex; }
        if (failed is null) return browser;
        // Made, but the caller gave up on it or it ran out of time: torn down, never handed to nobody.
        await ui.InvokeAsync(browser.Close, CancellationToken.None).ConfigureAwait(false);
        throw failed;
    }

    /// <summary>Run <paramref name="then"/> with the answer, or <paramref name="failed"/> with what refused it; a throw
    /// from either is swallowed, as nothing awaits this.</summary>
    public static async void Then(Task<string> call, Action<string> then, Action<Exception> failed)
    {
        string answer;
        try { answer = await call.ConfigureAwait(true); }
        catch (Exception ex) { AppCallback.Run(() => failed(ex)); return; }
        AppCallback.Run(() => then(answer));
    }
}
