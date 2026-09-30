using System.Runtime.InteropServices;
using Shenora.Chromium.Host;
using Shenora.Chromium.Interop;
using Shenora.Core.Sessions;
using Shenora.Core.Shell;

namespace Shenora.Chromium;

/// <summary>
/// The Chromium shell's session browsers (D91): windowless CEF browsers, so a session renders with no window at all,
/// each profile in a request context of its own. <c>UseChromium</c> registers it as <see cref="ISessionHost"/>; the
/// shell must be started with <see cref="ChromiumHostOptions.OffscreenSessions"/> for it to make one.
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

    internal ChromiumSessionHost(CefUiDispatcher ui, string dataFolder, bool offscreen)
    {
        _ui = ui;
        _root = Path.GetFullPath(dataFolder);
        _offscreen = offscreen;
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
            if (definition.VisibleTitle is not null)
                throw new NotSupportedException(
                    "The Chromium shell's session browsers render off-screen only; a visible one comes with its session window (D91, S4).");
            var profile = Admit(definition.Options);
            // The profile opens first: a profile on disk is ready only once CEF says so, and a browser asked for before
            // then is refused (measured: create_browser_sync answered null).
            var shared = definition.Context as Context;
            var (context, ready) = shared is not null ? shared.For(profile) : Open(profile);
            return SessionCalls.WhenReady(ready, profile, definition.Options.InitTimeout, cancellationToken, _ui,
                () => shared?.IsDisposed == true
                    ? throw new ObjectDisposedException(nameof(ISessionBrowserContext), "The pool let its profile go while a browser waited for it.")
                    : Create(definition, profile, (_cef_request_context_t*)context, owned: shared is null),
                abandon: () => { if (shared is null) Release((_cef_request_context_t*)context); });
        }
        catch (Exception ex)
        {
            return Task.FromException<ISessionBrowser>(ex);
        }
    }

    /// <inheritdoc />
    public Task<ISessionWindow> OpenWindowAsync(SessionWindowDefinition definition, CancellationToken cancellationToken) =>
        Task.FromException<ISessionWindow>(new NotSupportedException(
            "The Chromium shell's interactive session window is not built yet (D91, S4). Its pool and streaming sessions run."));

    // What this host refuses, before anything opens: the profile's full path when it takes the session.
    private string Admit(SessionBrowserOptions options)
    {
        if (!_offscreen)
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
            if (owned) Release(context);
            throw new InvalidOperationException($"Chromium would not make a session browser (profile '{profile}').");
        }
        browser.Attach(made);
        // A browser of its own lets its profile go as it closes; a pool's context lets go when the pool does.
        if (owned) browser.WhenClosed(() => Release(context));
        return browser;
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
        var ready = new ProfileReady();
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

        public ProfileReady() => Struct->on_request_context_initialized = &RequestContextInitialized;

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
            From<ProfileReady>(self)._initialized.TrySetResult();
        }
    }

    /// <summary>A pool's browsers: one request context for their one profile, let go when the pool is disposed.</summary>
    private sealed class Context(ChromiumSessionHost host) : ISessionBrowserContext
    {
        private string? _profile;
        private nint _context;
        private Task _ready = Task.CompletedTask;

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
