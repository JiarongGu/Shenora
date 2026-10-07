using System.Drawing;
using System.Runtime.InteropServices;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Chromium;

/// <summary>
/// The Chromium shell's session browsers (D91), each profile in a request context of its own: windowless CEF browsers
/// for the pool and the stream, which render with no window at all, and a window of its own for an interactive session.
/// <c>UseChromium</c> registers it as <see cref="ISessionHost"/>; the shell must be started with
/// <see cref="ChromiumHostOptions.OffscreenSessions"/> for it to make a windowless one.
/// <para>
/// ⚠ <b>A session's profile must be a folder directly inside the shell's data folder</b>, the only place CEF keeps a
/// profile (compose it with one segment under <see cref="ProfilesDirectory"/>), and must not be the app's own.
/// </para>
/// </summary>
public sealed unsafe class ChromiumSessionHost : ISessionHost
{
    private readonly CefUiDispatcher _ui;
    private readonly string _root;
    private readonly bool _offscreen;
    private readonly Func<IUiInteraction?> _mainWindow;
    private readonly ChromiumColorSchemes? _colorSchemes;

    // mainWindow: the main window's input, which an interactive session's window takes while it shows. colorSchemes: the
    // app's colour scheme, which each session's profile takes as it opens.
    internal ChromiumSessionHost(CefUiDispatcher ui, string dataFolder, bool offscreen, Func<IUiInteraction?>? mainWindow = null,
        ChromiumColorSchemes? colorSchemes = null)
    {
        _colorSchemes = colorSchemes;
        _ui = ui;
        _root = Path.GetFullPath(dataFolder);
        _offscreen = offscreen;
        _mainWindow = mainWindow ?? (() => null);
    }

    /// <inheritdoc />
    public IUiDispatcher Ui => _ui;

    /// <summary>
    /// Where the app's session profiles belong: the shell's data folder, since CEF opens a profile only directly inside
    /// it. Compose each with ONE segment, <c>InteractiveSession.ComposeProfileDirectory(ProfilesDirectory, $"{provider}.{account}")</c>;
    /// a nested path is refused.
    /// </summary>
    public string ProfilesDirectory => _root;

    /// <inheritdoc />
    public ISessionBrowserContext CreateContext() => new Context(this);

    /// <inheritdoc />
    public Task<ISessionBrowser> CreateAsync(SessionBrowserDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visible = definition.VisibleTitle is not null;
            var profile = Admit(definition.Options, windowless: !visible);
            // The profile opens first: a profile on disk is ready only once CEF says so, and a browser asked for before
            // then is refused (measured: create_browser_sync answered null).
            var shared = definition.Context as Context;
            var (context, ready) = shared is not null ? shared.For(profile) : Open(profile);
            Action abandon = () => { if (shared is null) Release((_cef_request_context_t*)context); };
            void Alive()
            {
                if (shared?.IsDisposed == true)
                    throw new ObjectDisposedException(nameof(ISessionBrowserContext), "The pool let its profile go while a browser waited for it.");
            }
            if (visible)
            {
                // Development: a window per browser, to watch the pool work, cascaded so several are watchable.
                var n = shared is null ? 0 : shared.Visible++;
                return SessionCalls.Browser(SessionCalls.Made(ready, profile, definition.Options.InitTimeout, cancellationToken, _ui,
                    () =>
                    {
                        Alive();
                        return OpenWindow(definition.Options, definition.Scope, definition.OnGone, definition.VisibleTitle!,
                            new Size(760, 940), new Size(300, 340), revealed: true, modal: false, background: null, profile,
                            (_cef_request_context_t*)context, owned: shared is null, place: new Point(40 + n * 40, 40 + n * 30));
                    },
                    window => window.Opened, TearDown, abandon));
            }
            return SessionCalls.Made<ISessionBrowser>(ready, profile, definition.Options.InitTimeout, cancellationToken, _ui,
                () =>
                {
                    Alive();
                    return Create(definition, profile, (_cef_request_context_t*)context, owned: shared is null);
                },
                browser => Task.WhenAll(((ChromiumSessionBrowser)browser).Ready, ((ChromiumSessionBrowser)browser).FirstPage),
                browser => _ui.InvokeAsync(browser.Close, CancellationToken.None), abandon);
        }
        catch (Exception ex)
        {
            return Task.FromException<ISessionBrowser>(ex);
        }
    }

    /// <inheritdoc />
    public Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = Admit(definition.Options, windowless: false);
            var (context, ready) = Open(profile);
            return SessionCalls.Made<ISessionWindow>(ready, profile, definition.Options.InitTimeout, cancellationToken, _ui,
                () => OpenWindow(definition.Options, definition.Scope, onGone: null, definition.Title, definition.ClientSize, definition.MinimumSize,
                    definition.Revealed, modal: true, definition.BackColor, profile, (_cef_request_context_t*)context, owned: true, place: null),
                window => ((ChromiumSessionWindow)window).Opened, window => TearDown((ChromiumSessionWindow)window),
                () => Release((_cef_request_context_t*)context));
        }
        catch (Exception ex)
        {
            return Task.FromException<ISessionWindow>(ex);
        }
    }

    // A cancelled or failed open answers only once its window, and its hold on the profile, are gone.
    private Task TearDown(ChromiumSessionWindow window) =>
        SessionCalls.After(_ui.InvokeAsync(window.Close, CancellationToken.None), () => window.Closed);

    // What this host refuses, before anything opens: the profile's full path when it takes the session.
    private string Admit(SessionBrowserOptions options, bool windowless)
    {
        if (windowless && !_offscreen)
            throw new InvalidOperationException(
                $"A session's browser renders off-screen, which this shell was not started for: set {nameof(ChromiumHostOptions)}."
                + $"{nameof(ChromiumHostOptions.OffscreenSessions)} to true.");
        // Another engine's options carry fields this one cannot honour; refused, never silently dropped (D91).
        if (options.GetType() != typeof(SessionBrowserOptions))
            throw new NotSupportedException(
                $"The Chromium shell's sessions take {nameof(SessionBrowserOptions)}: {options.GetType().Name}'s own fields are another engine's.");
        return ProfileFor(options.ProfileDirectory);
    }

    // CEF's UI thread, once the profile is ready. An owned context is released here if no browser comes of it.
    private ChromiumSessionBrowser Create(SessionBrowserDefinition definition, string profile, _cef_request_context_t* context, bool owned)
    {
        var browser = new ChromiumSessionBrowser(definition.Options, definition.Scope, definition.OnGone, definition.ViewportSize, _ui);
        var windowInfo = new _cef_window_info_t
        {
            size = (nuint)sizeof(_cef_window_info_t),
            windowless_rendering_enabled = 1,
            runtime_style = cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY,
        };
        var settings = new _cef_browser_settings_t { size = (nuint)sizeof(_cef_browser_settings_t) };
        // On about:blank, and READY only once that page has loaded (CreateAsync awaits FirstPage): loading as the browser
        // was made, it finished after the first lease's navigation had begun, which took it for its own (measured from
        // the feed on Linux: the title read empty). Made with no page instead, a windowless browser on Windows never
        // answered its DevTools setup, and every session timed out (measured).
        _cef_browser_t* made;
        const string blank = "about:blank";
        fixed (char* u = blank)
        {
            var url = CefStrings.View(u, blank.Length);
            // The call consumes a reference to the context; ours stays.
            ((_cef_base_ref_counted_t*)context)->add_ref((_cef_base_ref_counted_t*)context);
            made = Cef.cef_browser_host_create_browser_sync(&windowInfo, browser.ClientForCef(), &url, &settings, null, context);
        }
        if (made == null)
        {
            browser.NeverMade();   // its client, which nothing would ever retire, as the window path already says
            if (owned) Release(context);
            throw new InvalidOperationException($"Chromium would not make a session browser (profile '{profile}').");
        }
        browser.Attach(made);
        // A browser of its own lets its profile go as it closes; a pool's context lets go when the pool does.
        if (owned) browser.WhenClosed(() => Release(context));
        return browser;
    }

    // CEF's UI thread, once the profile is ready: a session browser in a window of its own. An owned context is released
    // as the browser goes, or here if the window cannot be made.
    private ChromiumSessionWindow OpenWindow(SessionBrowserOptions options, Func<string?> scope, Action<SessionProcessReport>? onGone, string title,
        Size size, Size minimum, bool revealed, bool modal, Color? background, string profile, _cef_request_context_t* context, bool owned,
        Point? place)
    {
        var browser = new ChromiumSessionBrowser(options, scope, onGone, size, _ui, windowless: false);
        var window = new ChromiumSessionWindow(browser, title, size, minimum, revealed, modal ? _mainWindow() : null, place);
        try
        {
            window.Open(context, background);
        }
        catch (Exception ex)
        {
            browser.NeverMade();   // its client, which nothing will close now
            if (owned) Release(context);
            throw new InvalidOperationException($"Chromium would not open a session window (profile '{profile}').", ex);
        }
        if (owned) browser.WhenClosed(() => Release(context));
        return window;
    }

    /// <summary>
    /// The profile's full path, once it is known to be one CEF can open for a session: a folder directly inside the
    /// shell's data folder, and not the app's own profile, whose cookies a session must never share. CEF opens any other
    /// path OFF THE RECORD, with nothing kept (measured: "Cannot create profile at path" in its log, and the session ran
    /// on regardless), so a nested one is refused rather than let lose every sign-in.
    /// </summary>
    internal string ProfileFor(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var root = Path.TrimEndingDirectorySeparator(_root);
        if (!string.Equals(Path.GetDirectoryName(full), root, comparison))
            throw new ArgumentException(
                $"A session's profile must be a folder directly inside the Chromium shell's data folder ({root}): CEF opens any other "
                + $"path off the record, keeping nothing. Compose it with one segment, as ComposeProfileDirectory({nameof(ChromiumSessionHost)}."
                + $"{nameof(ProfilesDirectory)}, \"provider.account\"). It was '{full}'.", nameof(profileDirectory));
        // The app's own is "default", or Chrome's "Default" in a process whose windows are Chrome's: either spelling.
        if (string.Equals(Path.GetFileName(full), "default", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{full}' is the app's own profile, whose cookies a session must never share.", nameof(profileDirectory));
        return full;
    }

    /// <summary>The profile's request context (a reference of the caller's), and when it is ready for a browser.</summary>
    private (nint Context, Task Ready) Open(string profile)
    {
        Directory.CreateDirectory(profile);
        var ready = new ProfileReady(_colorSchemes);
        var settings = new _cef_request_context_settings_t { size = (nuint)sizeof(_cef_request_context_settings_t) };
        _cef_request_context_t* context;
        fixed (char* p = profile)
        {
            settings.cache_path = CefStrings.View(p, profile.Length);
            context = Cef.cef_request_context_create_context(&settings, ready.ForCef());
        }
        ready.Release();   // CEF holds the handler as long as it needs it
        if (context == null) throw new InvalidOperationException($"Chromium would not open the session profile '{profile}'.");
        return ((nint)context, ready.Initialized);
    }

    private static void Release(_cef_request_context_t* context) =>
        ((_cef_base_ref_counted_t*)context)->release((_cef_base_ref_counted_t*)context);

    /// <summary>A profile's request context becoming ready, which CEF reports on its UI thread.</summary>
    private sealed class ProfileReady : CefObject<_cef_request_context_handler_t>
    {
        private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ChromiumColorSchemes? _colorSchemes;

        public ProfileReady(ChromiumColorSchemes? colorSchemes)
        {
            _colorSchemes = colorSchemes;
            Struct->on_request_context_initialized = &RequestContextInitialized;
        }

        public Task Initialized => _initialized.Task;

        [UnmanagedCallersOnly]
        private static void RequestContextInitialized(_cef_request_context_handler_t* self, _cef_request_context_t* requestContext)
        {
            using var context = new CefRef<_cef_request_context_t>(requestContext);
            // Popups allowed by default in the session's own profile, so OnWindowRequest decides every one: Chromium's
            // blocker otherwise refuses a popup no gesture opened before the life-span handler is asked (measured), and an
            // unseen page has no gestures. Null URLs set the profile's default.
            context.Ptr->set_content_setting(context.Ptr, null, null,
                cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_POPUPS, cef_content_setting_values_t.CEF_CONTENT_SETTING_VALUE_ALLOW);
            // The app's colour scheme as the profile opens (a later change reaches the main window's context, not this one).
            var owner = From<ProfileReady>(self);
            if (owner._colorSchemes is { } schemes) context.Ptr->set_chrome_color_scheme(context.Ptr, schemes.Current, 0);
            owner._initialized.TrySetResult();
        }
    }

    /// <summary>A pool's browsers: one request context for their one profile, let go when the pool is disposed.</summary>
    private sealed class Context(ChromiumSessionHost host) : ISessionBrowserContext
    {
        private string? _profile;
        private nint _context;
        private Task _ready = Task.CompletedTask;

        /// <summary>How many development windows its browsers have opened, to cascade the next.</summary>
        public int Visible;

        public bool IsDisposed { get; private set; }

        // UI thread.
        public (nint Context, Task Ready) For(string profile)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_context == 0)
            {
                (_context, _ready) = host.Open(profile);
                _profile = profile;
            }
            else if (!string.Equals(_profile, profile, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"A pool's browsers share one profile ('{_profile}'); '{profile}' is another.");
            }
            return (_context, _ready);
        }

        public void Dispose()
        {
            IsDisposed = true;
            if (_context == 0) return;
            Release((_cef_request_context_t*)_context);
            _context = 0;
        }
    }
}
