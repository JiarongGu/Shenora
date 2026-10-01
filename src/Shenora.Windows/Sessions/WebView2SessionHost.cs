using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Windows;

/// <summary>
/// The WinForms shell's session browsers (D91): WebView2 controls, each over its profile's own environment, hosted
/// off-screen on forms parked out of sight, or in an interactive session's modal window. <c>UseWindows</c> registers one
/// as <see cref="ISessionHost"/> over the main window; construct one over a control of your own otherwise.
/// </summary>
public sealed class WebView2SessionHost : ISessionHost
{
    private readonly Func<Form?> _mainForm;

    /// <summary>A host whose browsers are driven on <paramref name="anchor"/>'s UI thread (typically the main window), whose form owns an
    /// interactive session's window.</summary>
    public WebView2SessionHost(Control anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Ui = new WinFormsUiDispatcher(anchor);
        _mainForm = anchor.FindForm;
    }

    /// <summary>UseWindows: the main window's dispatcher, resolved as each call needs it.</summary>
    internal WebView2SessionHost(IUiDispatcher ui, Func<Form?> mainForm)
    {
        Ui = ui;
        _mainForm = mainForm;
    }

    /// <inheritdoc />
    public IUiDispatcher Ui { get; }

    /// <inheritdoc />
    public ISessionBrowserContext CreateContext() => new SharedContext();

    /// <inheritdoc />
    public async Task<ISessionBrowser> CreateAsync(SessionBrowserDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var shared = definition.Context as SharedContext;
        Form? host = null;
        // 🔴 OWNERSHIP, not provenance: a context's shared host belongs to the CONTEXT, and creations on it INTERLEAVE (each
        // yields at its multi-second init), so tearing it down here would dispose another caller's control with it.
        var ownsHost = false;
        WebView2Control? web = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition.VisibleTitle is { } title)
            {
                // Dev/test: a visible window per browser, cascaded so several are watchable.
                var n = shared is null ? 0 : shared.Visible++;
                host = new Form
                {
                    Text = title,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(40 + n * 40, 40 + n * 30),
                    ClientSize = new Size(760, 940),
                };
                ownsHost = true;
                host.Show();
            }
            else if (shared is not null)
            {
                // Every browser of a context rides its ONE shared hidden form (they overlap off-screen, harmlessly: each
                // renders independently). The caption is externally readable (Task Manager), so it takes a MECHANISM
                // name (D22) even though the window never shows.
                host = shared.Host ??= OffscreenWindow.Create("Render sessions", definition.ViewportSize);
            }
            else
            {
                host = OffscreenWindow.Create("Session browser", definition.ViewportSize);
                ownsHost = true;
            }

            web = new WebView2Control { Dock = DockStyle.Fill };
            host.Controls.Add(web);
            await SessionBrowser.InitializeAsync(web, definition.Options,
                onProcessFailed: e => definition.OnGone?.Invoke(new SessionProcessReport(
                    e.ProcessFailedKind.ToString(), e.Reason.ToString(), e.ExitCode, Terminal: true)),
                sessionScope: definition.Scope,
                environmentCache: shared?.Environment,
                cancellationToken: cancellationToken).ConfigureAwait(true);

            // Re-check AFTER the multi-second init: a browser made for a caller that has gone would be a live process
            // holding the profile lock with no owner.
            cancellationToken.ThrowIfCancellationRequested();
            return new WebView2SessionBrowser(web, host, ownsHost);
        }
        catch
        {
            // Undo everything this call realized, and NOTHING another one did: an abandoned control can still finish
            // attaching a live browser process that holds the very lock the init timeout is diagnosing.
            try
            {
                if (web is not null) { host?.Controls.Remove(web); web.Dispose(); }
                if (ownsHost) host?.Dispose();
            }
            catch (Exception cleanup)
            {
                SessionLog.Try(definition.Options.Log, l => l.LogWarning(cleanup, "Tearing down a session browser that failed to start failed."));
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var opened = new TaskCompletionSource<ISessionWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var main = _mainForm();

        // A silent window shows without taking activation (QuietForm): parked off-screen at opacity 0, it must not hold
        // the keyboard of a user typing into the app.
        var form = definition.Revealed ? new Form() : new QuietForm();
        form.Text = definition.Title;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ShowInTaskbar = true;
        // Device-independent pixels, as on every shell: scaled by the display the window opens on.
        form.ClientSize = form.LogicalToDeviceUnits(definition.ClientSize);
        form.MinimumSize = form.LogicalToDeviceUnits(definition.MinimumSize);
        if (definition.BackColor is { } backColor) form.BackColor = backColor;
        if (main?.Icon is { } icon)
        {
            try { form.Icon = icon; } catch { /* cosmetic */ }
        }
        if (!definition.Revealed)
        {
            // Silent-refresh shape: realized (WebView2 needs a real handle) but parked off-screen; Reveal() brings it on
            // screen only when interaction is needed.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OffscreenWindow.ParkedCoordinate, OffscreenWindow.ParkedCoordinate);
            form.ShowInTaskbar = false;
        }

        var web = new WebView2Control { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        var window = new WebView2SessionWindow(form, web, definition.Revealed && !OffscreenWindow.IsParked(form), _mainForm);
        Exception? failure = null;

        form.Shown += async (_, _) =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) { form.Close(); return; }
                await SessionBrowser.InitializeAsync(web, definition.Options, onProcessFailed: null, sessionScope: definition.Scope,
                    environmentCache: null).ConfigureAwait(true);
                window.Browser = new WebView2SessionBrowser(web, form, ownsHost: false);
                opened.TrySetResult(window);
            }
            catch (Exception ex)
            {
                failure = ex;
                form.Close();
            }
        };

        // Once the window is gone: a window that never got to the caller lets the call answer then, so a cancelled or
        // failed open never returns while its window still holds the profile.
        void Finished()
        {
            window.MarkClosed();
            try { form.Dispose(); } catch { }
            if (failure is not null) opened.TrySetException(failure);
            else opened.TrySetCanceled(cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true));
        }

        // QUEUED, never inline: called on the UI thread, an inline ShowDialog would run its whole nested loop inside this
        // call, which could then not return the task the caller awaits until the window was gone.
        var posted = Ui.Queue(() =>
        {
            if (!definition.Revealed)
            {
                // 🔴 A silent window is MODELESS: ShowDialog disables every window of the thread whatever its owner, and
                // activates the dialog, so a refresh parked out of sight disabled the main window and took the keyboard for
                // its whole run. It becomes modal to the main window only if it is revealed (WebView2SessionWindow.Reveal).
                // Inline when the queue refuses (the app shutting down), or the window's Closed never completes.
                form.FormClosed += (_, _) =>
                {
                    if (!Ui.Queue(() => { Finished(); return Task.CompletedTask; })) Finished();
                };
                try { form.Show(); }
                catch (Exception ex) { failure ??= ex; Finished(); }
                return Task.CompletedTask;
            }

            // ShowDialog runs its own nested loop until the window closes: the caller gets the window as it shows, and
            // drives it from inside that loop, modal, with the app's main window disabled behind it.
            try
            {
                form.ShowDialog(main is { Visible: true, IsDisposed: false } ? main : null);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
            finally
            {
                Finished();
            }
            return Task.CompletedTask;
        });
        if (!posted)
        {
            form.Dispose();
            opened.TrySetException(new ObjectDisposedException(nameof(WebView2SessionHost), "The UI thread is gone."));
        }
        return opened.Task;
    }

    // A pool's browsers: one environment for the profile, one hidden form for all of them.
    private sealed class SharedContext : ISessionBrowserContext
    {
        public readonly SessionEnvironmentCache Environment = new();
        public Form? Host;
        public int Visible;

        // On the UI thread (the pool is disposed there): the shared host goes, and the environment is let go so the
        // profile's browser process can exit and its folder be cleared.
        public void Dispose()
        {
            try { Host?.Dispose(); } catch { }
            Host = null;
            Environment.Clear();
        }
    }
}

/// <summary>A WebView2 control a session drives, and the form hosting it.</summary>
internal sealed class WebView2SessionBrowser : ISessionBrowser
{
    private readonly WebView2Control _web;
    private readonly Form _host;
    private readonly bool _ownsHost;
    private readonly NavigationChain _navigation = new();

    public WebView2SessionBrowser(WebView2Control web, Form host, bool ownsHost)
    {
        _web = web;
        _host = host;
        _ownsHost = ownsHost;
        var core = web.CoreWebView2;
        // A success reads where the page is from the core: NavigationCompleted's args carry no Uri at all, and after a
        // redirect chain the address the navigation started for is not where the page ended up. A FAILURE reports its
        // own navigation's address, by id: the core still shows the page before it.
        var started = new Dictionary<ulong, string>();
        core.NavigationCompleted += (_, e) =>
        {
            var own = started.Remove(e.NavigationId, out var address) ? address : null;
            var uri = (e.IsSuccess ? core.Source : own ?? core.Source) ?? string.Empty;
            // The replaced navigation's abort is ConnectionAborted (measured: its server had not answered) or
            // OperationCanceled.
            var aborted = e.WebErrorStatus is CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.OperationCanceled;
            if (_navigation.Replaced(uri, aborted)) return;
            NavigationCompleted?.Invoke(new SessionNavigationResult(uri, e.IsSuccess, e.WebErrorStatus.ToString()));
        };
        // NavigationStarting is the main frame's alone (iframes raise FrameNavigationStarting). It has NO deferral in
        // the SDK, which is why the policy is a synchronous predicate. Guarded: an escape here is an unhandled UI-thread
        // crash, so a throw CANCELS.
        core.NavigationStarting += (_, e) =>
        {
            var address = e.Uri ?? string.Empty;
            started[e.NavigationId] = address;   // a redirect keeps the id and moves the address
            _navigation.Started(address);
            if (CancelNavigation is not { } cancel) return;
            try { if (cancel(address)) e.Cancel = true; }
            catch { e.Cancel = true; }
        };
        // After SessionBrowser's own publisher, so DOWNLOAD_STARTING is still reported for a download cancelled here.
        core.DownloadStarting += (_, e) =>
        {
            if (!CancelDownloads) return;
            try { e.Cancel = true; }
            catch { /* the operation may already be gone */ }
        };
    }

    private CoreWebView2 Core => _web.CoreWebView2;

    public string Source => _web.IsDisposed ? string.Empty : Core?.Source ?? string.Empty;

    public event Action<SessionNavigationResult>? NavigationCompleted;

    public Func<string, bool>? CancelNavigation { get; set; }

    public bool CancelDownloads { get; set; }

    public void Navigate(string url)
    {
        _navigation.Requested(url);
        Core.Navigate(url);
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript) =>
        await _web.ExecuteScriptAsync(javaScript).ConfigureAwait(true);

    public Task<string> CallDevToolsAsync(string method, string parametersJson) =>
        Core.CallDevToolsProtocolMethodAsync(method, parametersJson);

    public IDisposable OnDevToolsEvent(string eventName, Action<string> onEvent)
    {
        var receiver = Core.GetDevToolsProtocolEventReceiver(eventName);
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler = (_, e) => onEvent(e.ParameterObjectAsJson);
        receiver.DevToolsProtocolEventReceived += handler;
        // The returned handle ROOTS the receiver: nothing else references it, and a subscription that stops after a GC
        // reports NO error; the page just quietly goes still.
        return new Subscription(receiver, handler);
    }

    public async Task<IReadOnlyList<SessionCookie>> GetCookiesAsync(string origin)
    {
        var list = new List<SessionCookie>();
        foreach (var cookie in await Core.CookieManager.GetCookiesAsync(origin).ConfigureAwait(true))
            list.Add(new SessionCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path));
        return list;
    }

    public void Focus() => _web.Focus();

    public void Close()
    {
        // A browser's own form goes with it; on a shared host only the control goes, and the host stays for the others.
        if (_ownsHost)
        {
            _host.Close();
            _host.Dispose();
        }
        else
        {
            _host.Controls.Remove(_web);
            _web.Dispose();
        }
    }

    private sealed class Subscription(CoreWebView2DevToolsProtocolEventReceiver receiver,
        EventHandler<CoreWebView2DevToolsProtocolEventReceivedEventArgs> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { receiver.DevToolsProtocolEventReceived -= handler; } catch { }
        }
    }
}

/// <summary>An interactive session's window: a modal form hosting the session's WebView2.</summary>
internal sealed class WebView2SessionWindow : ISessionWindow
{
    private readonly Form _form;
    private readonly WebView2Control _web;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<Form?> _mainForm;
    private bool _revealed;
    private Form? _blocked;   // the main window, whose input this window took when it was revealed

    public WebView2SessionWindow(Form form, WebView2Control web, bool revealed, Func<Form?>? mainForm = null)
    {
        _form = form;
        _web = web;
        _revealed = revealed;
        _mainForm = mainForm ?? (() => null);
        _form.FormClosed += (_, _) =>
        {
            if (_blocked is { IsDisposed: false } blocked) blocked.Enabled = true;
            _blocked = null;
        };
        _form.FormClosing += (_, e) =>
        {
            if (Closing is not { } closing) return;
            var byUser = IsByUser(e.CloseReason);
            if (!AppCallback.RunOrDefault(() => closing(byUser), fallback: true)) e.Cancel = true;
        };
    }

    /// <summary>
    /// A close a person asked for, which a session may hold once: <see cref="CloseReason.UserClosing"/>, which ALSO covers
    /// a programmatic <c>Close()</c> (<c>winforms-shell.md</c>); the host's own is let through by the controller once the
    /// flow finished. Never the app's exit, the OS's shutdown, Task Manager or an owner closing.
    /// </summary>
    internal static bool IsByUser(CloseReason reason) => reason is CloseReason.UserClosing;

    public ISessionBrowser Browser { get; internal set; } = null!;

    public Func<bool, bool>? Closing { get; set; }

    public bool IsRevealed => _revealed;

    public Task Closed => _closed.Task;

    internal void MarkClosed() => _closed.TrySetResult();

    public void Reveal()
    {
        if (_revealed || _form.IsDisposed) return;
        _revealed = true;
        // Modal to the main window from here, as a window shown revealed is: a silent one was opened modeless, so it takes
        // the main window's input now, and gives it back as it closes.
        if (!_form.Modal && _mainForm() is { IsDisposed: false, Visible: true, Enabled: true } main)
        {
            _form.Owner = main;
            main.Enabled = false;
            _blocked = main;
        }
        // NOT `_form.ShowInTaskbar = true`: that setter RECREATES the window handle, under a live WebView2, at the one
        // moment this window matters. See WindowActivation.ShowTaskbarButton.
        WindowActivation.ShowTaskbarButton(_form);
        var area = Screen.FromControl(_form).WorkingArea;
        _form.Location = new Point(area.X + (area.Width - _form.Width) / 2, area.Y + (area.Height - _form.Height) / 2);
        _form.Activate();
        _form.BringToFront();
        _web.Focus();
    }

    public void FitToContent(int cssWidth, int cssHeight)
    {
        if (_form.IsDisposed) return;
        var area = Screen.FromControl(_form).WorkingArea;
        _form.ClientSize = ComputeFitSize(cssWidth, cssHeight, _form.DeviceDpi, area.Size);
        if (_revealed)
            _form.Location = new Point(area.X + (area.Width - _form.Width) / 2, area.Y + (area.Height - _form.Height) / 2);
    }

    /// <summary>CSS px → physical px by the window's own DPI, clamped into the working area (margins leave room for the
    /// window chrome).</summary>
    internal static Size ComputeFitSize(int cssWidth, int cssHeight, int deviceDpi, Size workArea)
    {
        // DpiHelper owns the CSS-px → physical-px conversion, and guards a non-positive DPI.
        var scale = DpiHelper.ScaleFromDeviceDpi(deviceDpi);
        return new Size(
            Math.Min((int)Math.Round(cssWidth * scale), workArea.Width - 40),
            Math.Min((int)Math.Round(cssHeight * scale), workArea.Height - 60));
    }

    public void Close()
    {
        if (!_form.IsDisposed) _form.Close();
    }
}

/// <summary>A session's diagnostics, through the ONE owner of "an app logger that throws is a lost line".</summary>
internal static class SessionLog
{
    internal static void Try(ILogger? log, Action<ILogger> write)
    {
        if (log is null) return;
        AppCallback.Run(() => write(log));
    }
}
