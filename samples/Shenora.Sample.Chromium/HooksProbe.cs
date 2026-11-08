using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Core.Shell;
using Shenora.Core.WebView;

namespace Shenora.Sample.Chromium;

/// <summary>
/// The app's hooks on a page's downloads, permissions and renderer failure, run for real rather than unit-tested:
/// <c>SHENORA_SAMPLE_HOOKS_PROBE=1</c> or <c>2</c> makes the main window show a page of the probe's own, which downloads three
/// files (a click posted to the window starts each), asks for notifications and for the position, and is then left without its renderer (killed from outside, since
/// the page cannot crash itself). <c>HOOKS:</c> lines go to <c>SHENORA_SAMPLE_HOOKS_LOG</c> (default: beside the app), and the
/// app closes. In <c>1</c> the renderer hook answers "handled", so the page is NOT reloaded; in <c>2</c> it only observes, so
/// the shell's own reload runs and the page loads a second time.
/// <list type="bullet">
/// <item><c>yes.txt</c> — the download hook names a path: the file must be there afterwards, with the page's content, and
/// <c>PROGRESS</c> lines must end in <c>Complete</c> (a cancelled download ends in <c>Canceled</c>). With
/// <c>SHENORA_SAMPLE_HOOKS_DOWNLOAD</c> set to <c>pause</c> or <c>cancel</c> it arrives slowly (about 5 s, no announced size)
/// and the progress hook acts on it at its first bytes: <c>pause</c> pauses it and resumes it 2.5 s later, so the lines must
/// show <c>paused</c> and then <c>Complete</c> with the whole file; <c>cancel</c> cancels it, so they must end in
/// <c>Canceled</c>. <c>SHENORA_SAMPLE_HOOKS_DOWNLOAD=auto</c> or <c>autodeny</c> instead has the page start two downloads
/// (<c>auto1.txt</c>, <c>auto2.txt</c>) with no click, past the last click's user activation: the page's first automatic download
/// raises <c>MultipleDownloads</c> at the permission hook, which allows it in <c>auto</c> (both files saved) and declines it in
/// <c>autodeny</c> (neither). ⚠ Delete the app's <c>data</c> folder between runs: an allow is kept in the profile, and the prompt
/// is then never raised.</item>
/// <item><c>SHENORA_SAMPLE_HOOKS_CERT=allow</c> or <c>deny</c> starts a loopback HTTPS server with a certificate of its own, which the
/// page fetches (a sub-resource: it must fail, with no <c>CERTIFICATE</c> line) and then navigates itself to once it is done: the
/// certificate hook is asked, and in <c>allow</c> it pins the server's SHA-256 (<c>pinned True</c>) and the server must report a request
/// served, while in <c>deny</c> it must report none. ⚠ The page is then the server's: in this mode read the <c>RENDERER</c> and
/// <c>RELOAD</c> lines as the reload of that page, not the probe's.</item>
/// <item><c>SHENORA_SAMPLE_HOOKS_FILE=answer</c> or <c>cancel</c>: once the clicks are done the page opens a file dialog
/// (<c>input.click()</c> within the last click's user activation). The hook is asked (<c>FILEDIALOG</c>) and in <c>answer</c> names
/// <c>picked.txt</c>, which the page must then read (<c>PAGE file: picked.txt …</c>, with the file's content); in <c>cancel</c> it
/// names none and the page must see a <c>cancel</c> event, with no dialog window of Chromium's on screen either way.</item>
/// <item><c>SHENORA_SAMPLE_HOOKS_AUTH=answer</c>, <c>wrong</c>, <c>cancel</c> or <c>decline</c> starts a loopback HTTP server that
/// answers <c>401</c> (<c>Basic realm="probe"</c>) until a request carries <c>probe:secret</c>, and the page navigates itself to it once it
/// is done. The auth hook is asked (<c>AUTH</c>): <c>answer</c> gives <c>probe:secret</c>, so the server must report a request
/// <c>authorized</c>; <c>wrong</c> gives a wrong password for its first three asks and
/// then cancels, which shows how often the page is asked again; <c>cancel</c> answers with none, and <c>decline</c> returns false: the server
/// must see one request, with no credentials. ⚠ The page is then the server's: read the
/// <c>RENDERER</c> and <c>RELOAD</c> lines as the reload of that page. <c>SHENORA_SAMPLE_HOOKS_AUTHAT=frame</c> has the page
/// put the server in an iframe instead of navigating itself (the <c>authframe loaded</c> line, and the page's own address stays), and
/// <c>first</c> makes the server's address the window's own first navigation: there is no probe page then, only the <c>AUTH</c> lines, and
/// <c>sub</c> has the server serve its page unchallenged and challenge a <c>/sub</c> that the page fetches and loads as an image (and that the
/// probe's own page fetches across origins first; the <c>authsub</c> line says whether that fetch settled). <c>SHENORA_SAMPLE_HOOKS_AUTHSCHEME=digest</c>
/// makes the challenge Digest, which the server checks the response hash of. <c>SHENORA_SAMPLE_HOOKS_AUTH=off</c> sets no hook: the control.</item>
/// <item><c>SHENORA_SAMPLE_HOOKS_DIALOG=answer</c> or <c>refuse</c>: once the page is done it raises <c>alert</c>, <c>confirm</c> and
/// <c>prompt</c> and then reloads itself with a <c>beforeunload</c> handler set. The hook is asked for each (<c>SCRIPTDIALOG</c>):
/// <c>answer</c> accepts all and types <c>from the hook</c>, so the page must read <c>true</c> and that text and then reload (a second
/// load); <c>refuse</c> answers <c>confirm</c> false, no prompt text and no leaving, so the page must read <c>false</c> and <c>null</c>
/// and then report that it <c>stayed</c> (no second load). No dialog window of Chromium's may be on screen either way.</item>
/// <item><c>SHENORA_SAMPLE_HOOKS_CLIENTCERT=select</c> or <c>none</c> runs the certificate probe's server (<c>CERT</c>, whose own certificate
/// the hook allows) with a client certificate REQUIRED, and puts a certificate of the probe's own into the CURRENT USER's personal store for
/// the run (it is removed, with its key, as the run ends and before the next one starts; <c>CLIENT certificate … removed</c> is the line to
/// look for, and a run killed before it leaves the certificate, which the next run removes). The page navigates itself to the server, and the
/// hook is asked (<c>CLIENTCERT</c>, with every certificate of the store Chromium offered): <c>select</c> picks the probe's by its SHA-256,
/// so the server must report that certificate; <c>none</c> picks none, so it must report none; <c>decline</c> returns false, which leaves
/// CEF's default (read the server's line and the windows on screen for what it did), and <c>throw</c> fails the hook, so it must report none.</item>
/// <item><c>SHENORA_SAMPLE_HOOKS_POPUP=take</c>, <c>decline</c> or <c>browser</c> starts the loopback HTTP server of the auth probe (<c>POPUP</c>, no
/// challenge) and has the page, inside the last click's user activation, call <c>window.open</c> and then click a
/// <c>target="_blank"</c> link, both to an address of the server in <c>take</c> and to an <c>ftp:</c> one in <c>decline</c>. The hook is
/// asked for each (<c>POPUP hook</c>): <c>take</c> answers true, so the server must see no request (the shell opened nothing, where its
/// own policy would hand the address to the user's browser), and <c>decline</c> answers false, so the shell's policy drops the
/// non-web address. <c>browser</c> is the control for <c>take</c>: the hook declines the same server addresses, so the shell hands them to
/// the user's browser (⚠ which opens on this machine) and the server must see requests. No window of Chromium's may be on screen either way, and <c>window.open</c> must return null.
/// <c>SHENORA_SAMPLE_HOOKS_POPUPAT=nogesture</c> raises them after the activation has lapsed (<c>popup active=false</c>), <c>frame</c> from an
/// iframe of the page (<c>popup frame active=…</c>), and <c>self</c> navigates the page itself with <c>window.open(url, '_self')</c> (the hook
/// must not be asked), <c>redirect</c> opens the server's <c>/redirect</c> (the hook is asked with that address, before any hop),
/// <c>crossframe</c> has an iframe of another origin (the server's <c>/frame</c>) take the last click itself and open the popup, and <c>beforeunload</c>
/// opens one from the page's <c>beforeunload</c> handler as it reloads, <c>features</c> opens two with window features (<c>popup,width=420,height=310,left=50,top=60</c>,
/// and <c>popup</c> alone: the <c>POPUP hook</c> line gives the size and place the request carried, <c>-</c> for one not given), and <c>ctrl</c> and <c>shift</c>
/// click a <c>target="_blank"</c> link with that key held, which reaches the hook through the request handler and not the popup one (the page must
/// still finish, and the server see nothing).</item>
/// <item><c>no.txt</c> — handled with no path: cancelled. <c>decline.txt</c> — not handled: the default policy cancels it.
/// Neither file may exist.</item>
/// <item>notifications — the permission hook allows them, so the page's promise reads <c>granted</c>; the position — the hook
/// declines, so the default denies it (code 1).</item>
/// </list>
/// <c>Shenora.Sample.ChromiumView</c> links this file and runs the same probe on a WinForms <c>ChromiumView</c> page
/// (<see cref="Start"/>). Windows only, since the renderer is killed through PowerShell. Nothing outside the app is touched but its own renderer
/// processes, found by their command line.
/// </summary>
internal static class HooksProbe
{
    private static readonly bool On = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_PROBE") is "1" or "2";
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "hooks-probe-downloads");
    private static readonly string? LogPath = On
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_LOG") ?? Path.Combine(AppContext.BaseDirectory, "hooks-probe.log")
        : null;
    private static readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _loads, _pageClicks;

    /// <summary>The probe is running.</summary>
    public static bool Active => On;

    /// <summary>The page the main window shows: the probe's own while it runs, else the one given.</summary>
    public static string Page(string normal) => !On ? normal : AuthAt == "first" && Login.Value is not null ? $"http://{AuthHost}/page" : "hooks.html";

    /// <summary>The app's provider, answering the probe's page and its reports first while it runs.</summary>
    public static IWebViewResourceProvider? Provider(IWebViewResourceProvider? app) => On ? new ProbeProvider(app) : app;

    public static Func<ChromiumDownloadRequest, bool>? OnDownloadStarting => !On ? null : download =>
    {
        Log($"DOWNLOAD hook: {download.SuggestedName} ({download.MimeType}) from {download.Url}");
        switch (download.SuggestedName)
        {
            case "yes.txt": download.ResultFilePath = Path.Combine(Folder, "yes.txt"); return true;
            case "no.txt": return true;
            case "auto1.txt" or "auto2.txt": download.ResultFilePath = Path.Combine(Folder, download.SuggestedName); return true;
            default: download.ResultFilePath = Path.Combine(Folder, download.SuggestedName); return false;   // declined: a path set is ignored
        }
    };

    private static readonly string? SlowMode = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_DOWNLOAD") switch { "pause" => "pause", "cancel" => "cancel", _ => null };
    private static int _acted;

    // "auto" and "autodeny": after the three clicks the page starts two downloads with no user gesture, the second of which should
    // raise the MultipleDownloads permission; the hook allows it in "auto" and declines it in "autodeny", where the default denies.
    private static readonly string? Auto = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_DOWNLOAD") is "auto" or "autodeny"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_DOWNLOAD") : null;

    public static Action<ChromiumDownloadProgress>? OnDownloadProgress => !On ? null : progress =>
    {
        Log($"PROGRESS {Path.GetFileName(progress.FilePath)}: {progress.State}{(progress.IsPaused ? " paused" : "")} {progress.ReceivedBytes}/{progress.TotalBytes} (id {progress.Id})");
        if (SlowMode is null || !progress.FilePath.EndsWith("yes.txt") || progress.ReceivedBytes == 0 || Interlocked.Exchange(ref _acted, 1) == 1) return;
        Log($"ACT {SlowMode} on id {progress.Id}");
        if (SlowMode == "cancel") { progress.Cancel(); return; }
        progress.Pause();
        _ = Task.Delay(2500).ContinueWith(_ => { Log($"ACT resume on id {progress.Id}"); progress.Resume(); });
    };

    public static Func<ChromiumPermissionRequest, bool>? OnPermissionRequested => !On ? null : request =>
    {
        Log($"PERMISSION hook: {request.Kinds} from {request.Origin}");
        if (request.Kinds is ChromiumPermissionKind.MultipleDownloads && Auto == "autodeny") return false;
        if (request.Kinds is not (ChromiumPermissionKind.Notifications or ChromiumPermissionKind.MultipleDownloads)) return false;
        request.Allow = true;
        return true;
    };

    // "allow" and "deny": the page also fetches a local HTTPS server whose certificate no authority signed, which the certificate hook
    // allows when the certificate is the server's own (pinned by its SHA-256) and declines in "deny".
    // A mutual-TLS run needs the same server, whose own certificate the certificate hook then allows.
    private static readonly string? Cert = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_CERT") is "allow" or "deny"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_CERT") : Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_CLIENTCERT") is "select" or "none" or "decline" or "throw" ? "allow" : null;
    private static readonly Lazy<CertServer?> Server = new(() => On && Cert is not null ? new CertServer(Log, requireClientCertificate: ClientCert is not null) : null);

    // "select" and "none": the server wants a client certificate, which the hook answers with the probe's own, put in the current user's
    // store for the run and taken out after, or with none.
    private static readonly string? ClientCert = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_CLIENTCERT") is "select" or "none" or "decline" or "throw"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_CLIENTCERT") : null;
    private static readonly Lazy<ProbeClientCertificate?> Mine = new(() => On && ClientCert is not null ? new ProbeClientCertificate(Log) : null);
    private static int _askedForCertificate;

    public static Func<ChromiumClientCertificateRequest, bool>? OnClientCertificateRequest => !On || ClientCert is null ? null : request =>
    {
        Interlocked.Increment(ref _askedForCertificate);
        Log($"CLIENTCERT hook: {request.Host}:{request.Port}, proxy {request.IsProxy}, {request.Certificates.Count} on offer: "
            + string.Join("; ", request.Certificates.Select(c => $"'{c.Subject}' by '{c.Issuer}' {c.ValidFrom:u}..{c.ValidUntil:u} sha256 {c.Sha256}")));
        if (ClientCert == "throw") throw new InvalidOperationException("the probe's hook failed");
        if (ClientCert == "decline") return false;
        if (ClientCert == "select")
            request.SelectedIndex = request.Certificates.Select((c, i) => (c, i)).Where(p => p.c.Sha256 == Mine.Value?.Sha256).Select(p => (int?)p.i).FirstOrDefault();
        return true;
    };

    public static Func<ChromiumCertificateError, bool>? OnCertificateError => !On || Cert is null ? null : error =>
    {
        var pinned = error.Sha256 == Server.Value?.Sha256;
        Log($"CERTIFICATE hook: {error.ErrorName} ({error.ErrorCode}) for {error.Url}, subject '{error.Subject}', issuer '{error.Issuer}', sha256 {error.Sha256}, pinned {pinned}");
        if (Cert == "deny" || !pinned) return false;
        error.Allow = true;
        return true;
    };

    // "answer" and "cancel": the page opens a file dialog, which the hook answers with a file of the probe's or with none.
    private static readonly string? Pick = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_FILE") is "answer" or "cancel"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_FILE") : null;

    public static Func<ChromiumFileDialogRequest, bool>? OnFileDialog => !On || Pick is null ? null : request =>
    {
        Log($"FILEDIALOG hook: {request.Mode}, title '{request.Title}', default '{request.DefaultPath}', accept [{string.Join(", ", request.Accept)}]");
        if (Pick == "cancel") return true;
        Directory.CreateDirectory(Folder);
        var picked = Path.Combine(Folder, "picked.txt");
        File.WriteAllText(picked, "picked by the hook");
        request.Paths = [picked];
        return true;
    };

    // "answer", "wrong", "cancel" and "decline": the page navigates to a loopback server that wants Basic credentials.
    private static readonly string? Auth = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_AUTH") is "answer" or "wrong" or "cancel" or "decline" or "off"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_AUTH") : null;
    // "digest": the server's challenge is Digest (MD5, qop auth); unset, Basic.
    private static readonly string AuthScheme = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_AUTHSCHEME") switch { "digest" => "digest", _ => "basic" };

    private static readonly Lazy<AuthServer?> Login = new(() => On && Auth is not null
        ? new AuthServer(Log, scheme: AuthScheme, challengedPath: AuthAt == "sub" ? "/sub" : "/page",
            pages: AuthAt == "sub" ? new Dictionary<string, string> { ["/page"] = SubPage } : null)
        : null);

    // Where the challenge is raised: "frame" (an iframe of the probe's page), "first" (the window's own first navigation, which is the
    // server's address, so there is no probe page) or "sub" (the page the server serves unchallenged fetches and loads an image from a
    // path of it that is challenged, and the probe's own page fetches that path across origins first); unset, the page navigates itself
    // to the server once it is done.
    private static readonly string? AuthAt = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_AUTHAT") is "frame" or "first" or "sub"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_AUTHAT") : null;

    private static string AuthHost => $"127.0.0.1:{Login.Value?.Port}";

    private const string SubPage = """
        <title>sub page</title><script>
        const note = t => fetch('/note/' + encodeURIComponent(t)).catch(() => {});
        fetch('/sub').then(r => note('sub fetch status ' + r.status), e => note('sub fetch failed ' + e.message));
        const img = new Image();
        img.onload = () => note('sub img loaded'); img.onerror = () => note('sub img failed');
        img.src = '/sub?img';
        </script>
        """;
    private static int _asked;
    private static int _popups;

    public static Func<ChromiumAuthRequest, bool>? OnAuthRequest => !On || Auth is null or "off" ? null : request =>
    {
        var count = Interlocked.Increment(ref _asked);
        Log($"AUTH hook #{count}: {request.Scheme} realm '{request.Realm}' of {request.Origin} for {request.Url}, proxy {request.IsProxy}, as text {request}");
        switch (Auth)
        {
            case "answer": request.UserName = "probe"; request.Password = "secret"; return true;
            case "wrong": if (count <= 3) { request.UserName = "probe"; request.Password = "wrong"; } return true;
            case "cancel": return true;
            default: return false;
        }
    };

    // "answer" and "refuse": the page raises alert, confirm, prompt and a beforeunload, which the hook answers.
    private static readonly string? Dialog = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_DIALOG") is "answer" or "refuse"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_DIALOG") : null;

    public static Func<ChromiumScriptDialogRequest, bool>? OnScriptDialog => !On || Dialog is null ? null : request =>
    {
        Log($"SCRIPTDIALOG hook: {request.Kind} from '{request.Origin}', message '{request.Message}', default '{request.DefaultText}', reload {request.IsReload}");
        if (Dialog == "refuse") return true;
        request.Accept = true;
        request.ResultText = "from the hook";
        return true;
    };

    // "take" and "decline": the page opens windows, which the hook takes or leaves to the shell's policy.
    private static readonly string? Popup = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_POPUP") is "take" or "decline" or "browser"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_POPUP") : null;
    private static readonly Lazy<AuthServer?> Opened = new(() => On && Popup is not null
        ? new AuthServer(Log, "POPUP", challenge: false, pages: new Dictionary<string, string> { ["/frame"] = CrossFrame })
        : null);

    // Where the popup is raised: "nogesture" (after the last click's user activation has lapsed), "frame" (by an iframe of the page, inside
    // the activation) or "self" (a `_self` navigation, which is no popup); unset, the page itself raises it inside the activation.
    private static readonly string? PopupAt = Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_POPUPAT") is "nogesture" or "frame" or "self" or "redirect" or "crossframe" or "beforeunload" or "features" or "ctrl" or "shift"
        ? Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_POPUPAT") : null;
    private static readonly TaskCompletionSource CrossReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // "crossframe": the frame is the server's, another origin than the page. A click that reaches it is its own gesture; it opens the popup itself.
    private const string CrossFrame = """
        <body style="margin:0;background:#642"><script>
        const note = t => fetch('/note/' + encodeURIComponent(t)).catch(() => {});
        addEventListener('click', e => {
          if (!e.isTrusted) return;   // the link's own click below
          note('frame click active=' + navigator.userActivation.isActive + ' trusted=' + e.isTrusted);
          const w = window.open('/page', 'probe-side');
          note('frame window.open returned ' + (w === null ? 'null' : 'a window'));
          const a = document.createElement('a'); a.href = '/page'; a.target = '_blank'; document.body.append(a); a.click();
        });
        note('frame loaded');
        </script>
        """;

    public static Func<ChromiumPopupRequest, bool>? OnPopupRequested => !On || Popup is null ? null : request =>
    {
        Interlocked.Increment(ref _popups);
        Log($"POPUP hook: '{request.Url}' frame '{request.FrameName}', user gesture {request.IsUserGesture}, window {request.IsWindow}, "
            + $"x {request.X?.ToString() ?? "-"} y {request.Y?.ToString() ?? "-"} width {request.Width?.ToString() ?? "-"} height {request.Height?.ToString() ?? "-"}, "
            + $"answering {(Popup == "take" ? "taken" : "declined")}");
        return Popup == "take";
    };

    public static Func<ChromiumProcessReport, bool>? OnProcessFailed => !On ? null : failure =>
    {
        Log($"RENDERER hook: {failure.Status} ({failure.ErrorCode}); answering {(Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_PROBE") == "1" ? "handled" : "observed")}");
        return Environment.GetEnvironmentVariable("SHENORA_SAMPLE_HOOKS_PROBE") == "1";
    };

    /// <summary>Start the probe if the environment asks for it; otherwise nothing.</summary>
    public static void StartIfAsked(ShenoraApplication app)
    {
        if (!On) return;
        var windows = app.Services.GetRequiredService<ChromiumWindows>();
        Start("Shenora Chromium Sample", pageWindow: false, () => windows.Close(ChromiumWindows.MainWindowName));
    }

    /// <summary>
    /// Start the probe on a window of another host, such as a WinForms form holding a <c>ChromiumView</c>. The clicks are
    /// posted to the page's own child window (<paramref name="pageWindow"/>), which a form's handle is not; the shell's
    /// top-level window takes them itself.
    /// </summary>
    /// <param name="windowTitle">The title of the window showing the probe's page.</param>
    /// <param name="pageWindow">Post the clicks to the page's render widget inside that window, not to the window.</param>
    /// <param name="close">Ends the app, from any thread, once the probe has reported.</param>
    public static void Start(string windowTitle, bool pageWindow, Action close)
    {
        if (!On) return;
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        Directory.CreateDirectory(Folder);
        _ = Task.Run(async () =>
        {
            try { await RunAsync(windowTitle, pageWindow); }
            catch (Exception ex) { Log($"THREW {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                try { if (Mine.IsValueCreated) Mine.Value?.Dispose(); }   // out of the user's store before the app ends, whatever happened
                catch (Exception ex) { Log($"CLIENT certificate: not removed ({ex.GetType().Name}: {ex.Message})"); }
                close();
            }
        });
    }

    private static async Task RunAsync(string windowTitle, bool pageWindow)
    {
        _ = Mine.Value;   // in the store before the page's navigation asks for it
        var hwnd = await MainWindowAsync(windowTitle);
        if (AuthAt == "first" && Login.Value is { } start)
        {
            // The window opened on the server itself: no page of the probe's to click or to report, only the challenge and its answer.
            await Task.Delay(4000);
            Log($"AUTH server saw {start.Requests} request(s) for the page, {start.Authorized} authorized; the hook was asked {Volatile.Read(ref _asked)} time(s)");
            return;
        }
        await Task.WhenAny(Ready.Task, Task.Delay(20_000));
        if (pageWindow)
        {
            // Not the form: the page's input window is a child CEF made, and a posted click means nothing to the form.
            var page = PageWindow(hwnd);
            Log(page == 0 ? "CLICK target: the page's child window was NOT found, the form itself takes the clicks" : $"CLICK target: the page's child window, class {ClassOf(page)}");
            if (page != 0) hwnd = page;
        }
        GetClientRect(hwnd, out var client);
        var center = (client.Bottom / 2 << 16) | (client.Right / 2 & 0xFFFF);
        // Until the page has counted three: a click posted while the page was still settling is lost (seen on the first of them).
        for (var i = 0; i < 8 && Volatile.Read(ref _pageClicks) < 3; i++)
        {
            PostMessageW(hwnd, WmMouseMove, 0, center);
            PostMessageW(hwnd, WmLButtonDown, MkLButton, center);
            PostMessageW(hwnd, WmLButtonUp, 0, center);
            await Task.Delay(1500);
        }
        if (PopupAt == "crossframe" && Opened.Value is { } framed && await Task.WhenAny(CrossReady.Task, Task.Delay(20_000)) == CrossReady.Task)
        {
            // The frame now takes the clicks: until it has reported one, which it opens its popup in.
            for (var i = 0; i < 4 && framed.FrameClicks == 0; i++)
            {
                PostMessageW(hwnd, WmMouseMove, 0, center);
                PostMessageW(hwnd, WmLButtonDown, MkLButton, center);
                PostMessageW(hwnd, WmLButtonUp, 0, center);
                await Task.Delay(1200);
            }
        }
        var finished = await Task.WhenAny(Done.Task, Task.Delay(30_000)) == Done.Task;
        await Task.Delay(1500);   // a download's file lands a moment after its hook answered
        Log($"PAGE finished: {finished}");
        if (Popup is not null && Opened.Value is { } served)
        {
            await Task.Delay(3000);   // a popup the shell opened would have reached the server by now
            Log($"POPUP server saw {served.Requests} request(s) for the page and {served.Redirects} for the redirect; the hook was asked {Volatile.Read(ref _popups)} time(s)");
        }
        if (Dialog is not null) await Task.Delay(5000);   // the dialogs, and a beforeunload's reload or stay, settle
        if (ClientCert is not null && Server.Value is { } mutual)
        {
            await Task.Delay(3000);   // the page's navigation, and the handshake's second round, settle
            Log($"CLIENTCERT server saw {mutual.Requests} page request(s), the last with client certificate {mutual.ClientCertificate ?? "none"}; "
                + $"the probe's is {Mine.Value?.Sha256}, so {(mutual.ClientCertificate is null ? "none came" : mutual.ClientCertificate == Mine.Value?.Sha256 ? "the probe's came" : "ANOTHER came")}; "
                + $"the hook was asked {Volatile.Read(ref _askedForCertificate)} time(s)");
        }
        if (Login.Value is { } login)
        {
            await Task.Delay(AuthAt == "sub" ? 14000 : 3000);   // the page's navigation, and a challenge's answer, settle
            Log($"AUTH server saw {login.Requests} request(s) for the page, {login.Authorized} authorized, {login.SubRequests} for the sub-resource, {login.SubAuthorized} authorized; "
                + $"the hook was asked {Volatile.Read(ref _asked)} time(s)");
        }
        foreach (var name in new[] { "yes.txt", "no.txt", "decline.txt", "auto1.txt", "auto2.txt" })
        {
            var path = Path.Combine(Folder, name);
            var text = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            Log($"FILE {name}: {(text is null ? "absent" : $"saved, holds '{text[..Math.Min(40, text.Length)]}' ({text.Length} chars)")}");
        }
        var before = _loads;
        var killed = KillRenderers();
        Log($"RENDERERS killed: {killed}");
        await Task.Delay(6000);
        Log($"RELOAD after the renderer went: page loaded {_loads - before} more time(s)");
    }

    // The shell's own renderer processes: those of this app's exe started with --type=renderer.
    private static int KillRenderers()
    {
        var exe = Path.GetFileName(Environment.ProcessPath!);
        var script = $"$n=0; Get-CimInstance Win32_Process -Filter \"Name='{exe}'\" | Where-Object {{ $_.CommandLine -match '--type=renderer' }} | "
            + "ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $n++ }; $n";
        using var process = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-Command", script])
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return int.TryParse(output, out var n) ? n : -1;
    }

    private static async Task<nint> MainWindowAsync(string title)
    {
        for (var i = 0; i < 150; i++)
        {
            var hwnd = FindWindowW(null, title);
            if (hwnd != 0 && IsWindowVisible(hwnd)) return hwnd;
            await Task.Delay(200);
        }
        throw new TimeoutException("the main window did not appear");
    }

    // The first descendant of the window that is CEF's render widget: where Chromium takes a page's mouse input.
    private static nint PageWindow(nint parent)
    {
        nint found = 0;
        EnumChildWindows(parent, (child, _) =>
        {
            if (ClassOf(child) != "Chrome_RenderWidgetHostHWND") return true;
            found = child;
            return false;
        }, 0);
        return found;
    }

    private static string ClassOf(nint hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(hwnd, name, name.Capacity) > 0 ? name.ToString() : "?";
    }

    private const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202;
    private const nint MkLButton = 0x0001;

    [StructLayout(LayoutKind.Sequential)] private struct RectStruct { public int Left, Top, Right, Bottom; }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string title);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] private static extern bool GetClientRect(nint hwnd, out RectStruct rect);
    [DllImport("user32")] private static extern bool PostMessageW(nint hwnd, uint message, nint w, nint l);
    private delegate bool EnumChildProc(nint hwnd, nint lParam);
    [DllImport("user32")] private static extern bool EnumChildWindows(nint parent, EnumChildProc callback, nint lParam);
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);

    private static void Log(string line)
    {
        if (LogPath is null) return;
        lock (Done) File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} HOOKS: {line}{Environment.NewLine}");
    }

    private static string PageHtml => PageTemplate.Replace("__AUTO__", Auto is null ? "false" : "true")
        .Replace("__CERTPORT__", (Server.Value?.Port ?? 0).ToString()).Replace("__FILE__", Pick is null ? "false" : "true")
        .Replace("__AUTHPORT__", (Login.Value?.Port ?? 0).ToString()).Replace("__AUTHFRAME__", AuthAt == "frame" ? "true" : "false").Replace("__DIALOG__", Dialog is null ? "false" : "true")
        .Replace("__POPUPAT__", PopupAt ?? "").Replace("__POPUPPORT__", (Opened.Value?.Port ?? 0).ToString()).Replace("__AUTHSUB__", AuthAt == "sub" ? "true" : "false")
        .Replace("__AUTHHOST__", Login.Value is null ? "" : AuthHost)
        .Replace("__POPUPURL__", Popup is null ? "''" : Popup is "take" or "browser" ? $"'http://127.0.0.1:{Opened.Value?.Port}/{(PopupAt == "redirect" ? "redirect" : "page")}'" : "'ftp://127.0.0.1/popup'");

    private const string PageTemplate = """
        <!doctype html><meta charset="utf-8"><title>hooks probe</title>
        <script>
        const say = (key, value) => fetch('/report/' + key + '/' + encodeURIComponent(value)).then(r => r.text()).catch(() => '');
        (async () => {
          // Only the first load runs the steps: a load after the renderer went has none to run again.
          if (await say('loaded', Date.now()) !== '1') return;
          // One link over the whole page, which the probe clicks three times (a download needs a user's click): each click
          // points it at the next file.
          const names = ['yes.txt', 'no.txt', 'decline.txt'];
          const link = document.createElement('a');
          link.style.cssText = 'position:fixed;inset:0;background:#246';
          const next = i => { link.href = '/dl/' + names[i]; link.download = names[i]; };
          next(0);
          document.body.append(link);
          let clicks = 0;
          const finished = new Promise(resolve => link.addEventListener('click', e => { say('click', clicks + 1 + ' trusted=' + e.isTrusted + ' focus=' + document.hasFocus() + ' ' + document.visibilityState); setTimeout(() => {
            if (++clicks === names.length) resolve(); else next(clicks);
          }, 100); }));
          // "popupat=frame": an iframe of the page, whose own window opens the popup. It exists BEFORE the click: a click's activation
          // reaches the frames that are there (a srcdoc frame is the page's origin), and a frame made after it starts with none.
          const popupFrame = '__POPUPAT__' !== 'frame' ? null : new Promise(resolve => {
            const frame = document.createElement('iframe');
            frame.srcdoc = '<body>probe frame';
            frame.addEventListener('load', () => resolve(frame.contentWindow));
            document.body.append(frame);
          });
          // "popupat=crossframe": an iframe of the server's, another origin; it exists before the click, lets the clicks through to the link,
          // and takes them once the link's three are done.
          const crossFrame = '__POPUPAT__' !== 'crossframe' ? null : new Promise(resolve => {
            const frame = document.createElement('iframe');
            frame.style.cssText = 'position:fixed;inset:0;width:100%;height:100%;border:0;z-index:9;pointer-events:none';
            frame.addEventListener('load', () => resolve(frame));
            frame.src = 'http://127.0.0.1:__POPUPPORT__/frame';
            document.body.append(frame);
          });
          if (crossFrame) await crossFrame;
          say('ready', '1');
          await finished;
          const popupAt = '__POPUPAT__';
          if (crossFrame) {
            (await crossFrame).style.pointerEvents = 'auto';
            say('crossready', 'active=' + navigator.userActivation.isActive);
            await new Promise(resolve => setTimeout(resolve, 6000));
          } else if (__POPUPURL__ && popupAt === 'beforeunload') {
            // A popup raised while the page is being left, inside the last click's activation: the reload below runs the handler.
            window.addEventListener('beforeunload', () => {
              const opened = window.open(__POPUPURL__, 'probe-side');
              say('popup', 'beforeunload active=' + navigator.userActivation.isActive + ' window.open returned ' + (opened === null ? 'null' : 'a window'));
            });
            await say('done', '1');
            location.reload();
            return;
          } else if (__POPUPURL__ && popupAt !== 'self') {
            // Inside the last click's user activation, which a popup needs to get past Chromium's blocker to the hook; "nogesture" waits it out.
            if (popupAt === 'nogesture') await new Promise(resolve => setTimeout(resolve, 6500));
            const host = popupFrame ? await popupFrame : window;
            say('popup', (popupAt === 'frame' ? 'frame ' : '') + 'active=' + host.navigator.userActivation.isActive);
            const opened = host.open(__POPUPURL__, 'probe-side');
            await say('popup', 'window.open returned ' + (opened === null ? 'null' : 'a window'));
            const a = host.document.createElement('a');
            a.href = __POPUPURL__; a.target = '_blank';
            host.document.body.append(a);
            a.click();
            if (popupAt === 'features') {
              // What the page says about the window: sized and placed features, and a feature list with no size.
              say('popup', 'features sized ' + (window.open(__POPUPURL__, 'probe-sized', 'popup,width=420,height=310,left=50,top=60') === null ? 'null' : 'a window'));
              say('popup', 'features only-popup ' + (window.open(__POPUPURL__, 'probe-bare', 'popup') === null ? 'null' : 'a window'));
            } else if (popupAt === 'ctrl' || popupAt === 'shift') {
              // A link clicked with a modifier: its disposition is the click's, not the page's call, and it does not reach OnBeforePopup.
              const link = document.createElement('a');
              link.href = __POPUPURL__; link.target = '_blank';
              document.body.append(link);
              link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window, ctrlKey: popupAt === 'ctrl', shiftKey: popupAt === 'shift' }));
            }
            await new Promise(resolve => setTimeout(resolve, 1000));
          }
          if (__FILE__) {
            // Inside the last click's user activation (5 s), which a page's own file dialog needs.
            const input = document.createElement('input');
            input.type = 'file'; input.accept = 'text/plain,.txt';
            const answered = new Promise(resolve => {
              input.addEventListener('change', async () => { const f = input.files[0]; say('file', f.name + ' ' + f.size + ' bytes: ' + await f.text()).then(resolve); });
              input.addEventListener('cancel', () => say('file', 'cancelled').then(resolve));
            });
            say('filedialog', 'active=' + navigator.userActivation.isActive);
            input.click();
            await Promise.race([answered, new Promise(resolve => setTimeout(resolve, 4000))]);
          }
          if (__AUTO__) {
            // Past the user activation the last click gave (5 s), so these two downloads follow no gesture.
            await new Promise(resolve => setTimeout(resolve, 6500));
            for (const name of ['auto1.txt', 'auto2.txt']) {
              const a = document.createElement('a');
              a.href = '/dl/' + name; a.download = name;
              document.body.append(a);
              say('auto', name + ' active=' + navigator.userActivation.isActive);
              a.click();
              await new Promise(resolve => setTimeout(resolve, 2500));
            }
          }
          // A fetch of the server is a sub-resource: kept for the end, as the cert step below is the main frame's navigation.
          if (__CERTPORT__) await fetch('https://127.0.0.1:__CERTPORT__/ping', { mode: 'no-cors' }).then(() => say('subresource', 'loaded'), e => say('subresource', 'failed ' + e.message));
          say('notification', await Notification.requestPermission());
          await new Promise(resolve => navigator.geolocation.getCurrentPosition(
            () => { say('geolocation', 'granted'); resolve(); }, e => { say('geolocation', 'denied ' + e.code); resolve(); }, { timeout: 5000 }));
          say('done', '1');
          if (__POPUPURL__ && popupAt === 'self') {
            // Not a popup: the page's own navigation, which the hook must not be asked about. The page is the server's afterwards.
            say('popup', 'self active=' + navigator.userActivation.isActive);
            await new Promise(resolve => setTimeout(resolve, 300));
            window.open(__POPUPURL__, '_self');
            return;
          }
          if (__DIALOG__) {
            // Each of these blocks the page until the hook answers; with no answer the probe's reports below never come.
            alert('probe alert'); await say('alert', 'returned');
            await say('confirm', String(confirm('probe confirm?')));
            await say('prompt', String(prompt('probe prompt?', 'default text')));
            // A beforeunload needs the page to have had a user's gesture, which its clicks gave.
            window.addEventListener('beforeunload', e => { e.preventDefault(); e.returnValue = ''; });
            location.reload();
            await new Promise(resolve => setTimeout(resolve, 3000));
            say('beforeunload', 'stayed');
          }
          // The page itself goes to the server: the one request Chromium asks the certificate hook about.
          if (__CERTPORT__) { await new Promise(resolve => setTimeout(resolve, 300)); location.href = 'https://127.0.0.1:__CERTPORT__/page'; }
          // The same for a server that wants credentials: the page's own navigation meets the challenge.
          if (__AUTHPORT__) {
            await new Promise(resolve => setTimeout(resolve, 300));
            // A sub-resource across origins: the server's own page makes the same-origin ones once the page is its.
            if (__AUTHSUB__) await Promise.race([fetch('http://127.0.0.1:__AUTHPORT__/sub', { mode: 'no-cors' }).then(r => say('authsub', 'cross-origin fetch settled, status ' + r.status + ' type ' + r.type), e => say('authsub', 'cross-origin fetch failed ' + e.message)),
              new Promise(resolve => setTimeout(() => say('authsub', 'cross-origin fetch still pending after 8 s').then(resolve), 8000))]);
            if (__AUTHFRAME__) {
              // A frame's own navigation meets the challenge; the page itself stays where it is.
              const frame = document.createElement('iframe');
              frame.addEventListener('load', () => say('authframe', 'loaded'));
              frame.src = 'http://__AUTHHOST__/page';
              document.body.append(frame);
            } else location.href = 'http://__AUTHHOST__/page';
          }
        })();
        </script>
        """;

    /// <summary>A loopback HTTPS server whose certificate is its own: no authority signed it, so Chromium meets a certificate error.</summary>
    private sealed class CertServer
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);

        private int _requests;
        private string? _clientCertificate;

        public CertServer(Action<string> log, bool requireClientCertificate = false)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=hooks-probe", key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // An ephemeral key cannot back a TLS server on Windows: round-trip it through a PFX.
            var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                made.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null);
            Sha256 = Convert.ToHexString(certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)).ToLowerInvariant();
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            log($"CERT server on {Port}, sha256 {Sha256}");
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var tcp = client;
                            using var tls = new System.Net.Security.SslStream(tcp.GetStream());
                            // Any client certificate is taken (the probe checks WHICH one came); none is refused when one is required.
                            await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions
                            {
                                ServerCertificate = certificate,
                                ClientCertificateRequired = requireClientCertificate,
                                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                            });
                            var buffer = new byte[4096];
                            var read = await tls.ReadAsync(buffer);
                            var offered = (tls.RemoteCertificate as System.Security.Cryptography.X509Certificates.X509Certificate2
                                ?? (tls.RemoteCertificate is null ? null : new System.Security.Cryptography.X509Certificates.X509Certificate2(tls.RemoteCertificate)))
                                ?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant();
                            if (read > 0 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("GET /page")) { Interlocked.Increment(ref _requests); _clientCertificate = offered; }
                            log($"CERT server: served a request ({read} bytes) over TLS, client certificate {(offered is null ? "none" : offered)}");
                            const string body = "<title>cert page</title><p>served over TLS";
                            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
                        }
                        catch (Exception ex) { log($"CERT server: the connection ended before a request ({ex.GetType().Name}: {ex.Message})"); }
                    });
                }
            });
        }

        public int Port { get; }

        /// <summary>The page requests served.</summary>
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>The SHA-256 of the client certificate the last page request came with; null when it came with none.</summary>
        public string? ClientCertificate => _clientCertificate;

        /// <summary>The certificate's SHA-256 as 64 lowercase hex digits: what the hook pins.</summary>
        public string Sha256 { get; }
    }

    /// <summary>
    /// A client certificate of the probe's own, in the CURRENT USER's personal store for the run: Chromium lists a client's certificates from the
    /// system's store, so there is nowhere else to put it. Its subject is the probe's (one a stale run left is removed first), and
    /// disposing removes it and its key.
    /// </summary>
    private sealed class ProbeClientCertificate : IDisposable
    {
        private const string Subject = "CN=shenora-hooks-probe-client";
        private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate;

        public ProbeClientCertificate(Action<string> log)
        {
            Remove(log);
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(Subject, key,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2")], critical: false));   // clientAuth
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            // A key that lives in the user's key store (PersistKeySet), which a store entry needs to sign with it.
            _certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
                made.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null,
                System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.PersistKeySet | System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.UserKeySet);
            Sha256 = Convert.ToHexString(_certificate.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)).ToLowerInvariant();
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            store.Add(_certificate);
            log($"CLIENT certificate '{Subject}' added to the current user's store for this run, sha256 {Sha256}");
        }

        /// <summary>The certificate's SHA-256 as 64 lowercase hex digits.</summary>
        public string Sha256 { get; }

        public void Dispose() => Remove(Log);

        // Every certificate of the probe's subject, with its key: this run's, and one a run that was killed left.
        private static void Remove(Action<string> log)
        {
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            foreach (var found in store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindBySubjectDistinguishedName, Subject, validOnly: false))
            {
                try
                {
                    if (System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(found) is System.Security.Cryptography.RSACng { Key: { } key }) key.Delete();
                }
                catch (Exception ex) { log($"CLIENT certificate: its key was not deleted ({ex.GetType().Name}: {ex.Message})"); }
                store.Remove(found);
                log($"CLIENT certificate '{Subject}' removed from the current user's store");
            }
        }
    }

    /// <summary>
    /// A loopback HTTP server that answers <c>401</c> with a challenge until a request carries <c>probe:secret</c>: Basic, or Digest (MD5, qop
    /// auth). Only <paramref name="challengedPath"/> is challenged (<c>/page</c>, or <c>/sub</c> for a sub-resource of an unchallenged page). It also
    /// serves <c>/note/…</c> (logged), <c>/redirect</c> (to <c>/page</c>) and the HTML of <paramref name="pages"/>.
    /// </summary>
    private sealed class AuthServer
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private int _requests, _authorized, _subRequests, _subAuthorized, _redirects, _frameClicks;

        public AuthServer(Action<string> log, string name = "AUTH", bool challenge = true, string scheme = "basic", string challengedPath = "/page",
            IReadOnlyDictionary<string, string>? pages = null)
        {
            _listener = new(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            log($"{name} server on {Port} ({scheme}, challenging {(challenge ? challengedPath : "nothing")})");
            var expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("probe:secret"));
            var nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var tcp = client;
                            var stream = tcp.GetStream();
                            var buffer = new byte[8192];
                            var read = await stream.ReadAsync(buffer);
                            if (read == 0) return;   // a connection Chromium opened ahead and never used
                            var head = Encoding.ASCII.GetString(buffer, 0, read);
                            var line = head.Split(' ', 3);
                            var target = line[1];
                            var path = Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : target;
                            var route = path.Split('?')[0];
                            var headers = head.Split("\r\n");
                            string? Header(string header) => headers.FirstOrDefault(l => l.StartsWith(header + ":", StringComparison.OrdinalIgnoreCase))?[(header.Length + 1)..].Trim();
                            if (route.StartsWith("/note/", StringComparison.Ordinal))
                            {
                                var note = Uri.UnescapeDataString(route["/note/".Length..]);
                                if (note.StartsWith("frame click", StringComparison.Ordinal)) Interlocked.Increment(ref _frameClicks);
                                log($"{name} server note: {note}");
                                await Reply(stream, "200 OK", "");
                                return;
                            }
                            if (route == "/redirect")
                            {
                                Interlocked.Increment(ref _redirects);
                                log($"{name} server: GET /redirect, sent on to /page");
                                await Reply(stream, "302 Found", "", $"Location: http://127.0.0.1:{Port}/page\r\n");
                                return;
                            }
                            if (route is not ("/page" or "/sub" or "/frame")) { log($"{name} server: {line[0]} {target} answered 404"); await Reply(stream, "404 Not Found", ""); return; }   // a favicon
                            var credentials = Header("Authorization");
                            var challenged = challenge && route == challengedPath;
                            var ok = !challenged || (scheme == "digest" ? DigestOk(credentials, line[0], path, nonce) : credentials == expected);
                            if (route == "/sub") { Interlocked.Increment(ref _subRequests); if (ok) Interlocked.Increment(ref _subAuthorized); }
                            else if (route == "/page") { Interlocked.Increment(ref _requests); if (ok) Interlocked.Increment(ref _authorized); }
                            log($"{name} server: {line[0]} {path} (site {Header("Sec-Fetch-Site") ?? "?"}, dest {Header("Sec-Fetch-Dest") ?? "?"}) with "
                                + $"{(credentials is null ? "no credentials" : ok ? "the right credentials (authorized)" : "wrong credentials")}"
                                + (credentials is { } sent && scheme == "digest" ? $" [{sent[..Math.Min(sent.Length, 24)]}…]" : ""));
                            var html = pages?.GetValueOrDefault(route) ?? (route == "/frame" ? "" : "<title>authed page</title><p>behind the login");
                            if (ok) await Reply(stream, "200 OK", html);
                            else await Reply(stream, "401 Unauthorized", "<title>401 body</title><p>login needed", scheme == "digest"
                                ? $"WWW-Authenticate: Digest realm=\"probe\", nonce=\"{nonce}\", qop=\"auth\", algorithm=MD5\r\n"
                                : "WWW-Authenticate: Basic realm=\"probe\"\r\n");
                        }
                        catch (Exception ex) { log($"{name} server: the connection ended ({ex.GetType().Name}: {ex.Message})"); }
                    });
                }
            });
        }

        private static async Task Reply(System.Net.Sockets.NetworkStream stream, string status, string body, string extraHeaders = "") =>
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\n{extraHeaders}Connection: close\r\n\r\n{body}"));

        // A digest answer is right when the response hash is the one the server computes from probe:secret and the nonce it issued.
        private static bool DigestOk(string? header, string method, string uri, string nonce)
        {
            if (header is null || !header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
            var fields = System.Text.RegularExpressions.Regex.Matches(header[7..], "(\\w+)=(\"([^\"]*)\"|[^,\\s]*)")
                .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value);
            static string Md5(string text) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.ASCII.GetBytes(text))).ToLowerInvariant();
            if (fields.GetValueOrDefault("username") != "probe" || fields.GetValueOrDefault("nonce") != nonce || fields.GetValueOrDefault("uri") != uri) return false;
            var ha1 = Md5("probe:probe:secret");
            var ha2 = Md5($"{method}:{uri}");
            var response = Md5($"{ha1}:{nonce}:{fields.GetValueOrDefault("nc")}:{fields.GetValueOrDefault("cnonce")}:{fields.GetValueOrDefault("qop")}:{ha2}");
            return fields.GetValueOrDefault("response") == response;
        }

        public int Port { get; }

        /// <summary>The requests for <c>/page</c>.</summary>
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>The requests for <c>/page</c> that carried the right credentials, or needed none.</summary>
        public int Authorized => Volatile.Read(ref _authorized);

        /// <summary>The requests for <c>/sub</c>.</summary>
        public int SubRequests => Volatile.Read(ref _subRequests);

        /// <summary>The requests for <c>/sub</c> that carried the right credentials, or needed none.</summary>
        public int SubAuthorized => Volatile.Read(ref _subAuthorized);

        /// <summary>The requests for <c>/redirect</c>.</summary>
        public int Redirects => Volatile.Read(ref _redirects);

        /// <summary>The clicks the cross-origin frame has reported.</summary>
        public int FrameClicks => Volatile.Read(ref _frameClicks);
    }

    /// <summary>A body that arrives in 12 chunks, 400 ms apart, with no announced length: a download long enough to be paused.</summary>
    private sealed class SlowBody : Stream
    {
        private const int Chunks = 12, ChunkSize = 16 * 1024;
        private int _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent >= Chunks) return 0;
            if (_sent > 0) await Task.Delay(400, cancellationToken);
            var count = Math.Min(buffer.Length, ChunkSize);
            buffer.Span[..count].Fill((byte)'x');
            if (_sent == 0) Encoding.UTF8.GetBytes("downloaded yes.txt\n").CopyTo(buffer.Span);
            _sent++;
            return count;
        }
    }

    private sealed class ProbeProvider(IWebViewResourceProvider? app) : IWebViewResourceProvider
    {
        public bool Exists(string virtualPath) => virtualPath is "hooks.html" || virtualPath.StartsWith("dl/", StringComparison.Ordinal)
            || virtualPath.StartsWith("report/", StringComparison.Ordinal) || app?.Exists(virtualPath) == true;

        public Stream? GetResourceStream(string virtualPath)
        {
            if (virtualPath == "hooks.html") return new MemoryStream(Encoding.UTF8.GetBytes(PageHtml));
            if (virtualPath.StartsWith("dl/", StringComparison.Ordinal))
            {
                Log($"PAGE asked for {virtualPath}");
                return SlowMode is not null && virtualPath == "dl/yes.txt" ? new SlowBody() : new MemoryStream(Encoding.UTF8.GetBytes($"downloaded {virtualPath[3..]}"));
            }
            if (!virtualPath.StartsWith("report/", StringComparison.Ordinal)) return app?.GetResourceStream(virtualPath);
            var report = virtualPath["report/".Length..].Split('/');
            var count = report[0] == "loaded" ? Interlocked.Increment(ref _loads) : 0;
            Log($"PAGE {report[0]}: {Uri.UnescapeDataString(report.ElementAtOrDefault(1) ?? "")}");
            if (report[0] == "click") Interlocked.Increment(ref _pageClicks);
            if (report[0] == "ready") Ready.TrySetResult();
            if (report[0] == "crossready") CrossReady.TrySetResult();
            if (report[0] == "done") Done.TrySetResult();
            return new MemoryStream(Encoding.UTF8.GetBytes(count.ToString()));
        }
    }
}
