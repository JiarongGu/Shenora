using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shenora.WinForms;

/// <summary>Inputs for one <see cref="SecondaryWindows.Open"/> call.</summary>
public sealed class SecondaryWindowOptions
{
    /// <summary>
    /// Creates the window — runs ON the window's own STA thread (every control it creates gets
    /// that thread's message pump). Create it, don't show it: the pump shows it, after geometry
    /// is applied. A WebView2-hosting window initializes its host from its own <c>Load</c> with
    /// <c>UseSharedEnvironment = false</c> (the thread-affinity contract).
    /// </summary>
    public required Func<Form> CreateForm { get; init; }

    /// <summary>
    /// Geometry persistence for this named window — the same window-state stack the main window
    /// uses (logical-px store, physical restore, off-screen recovery): one store per name (e.g.
    /// a <c>JsonFileWindowStateStore("windows/{name}.json")</c>). Null = no persistence. This is
    /// the seam that replaces the source app's profile-config coupling.
    /// </summary>
    public IWindowStateStore? StateStore { get; init; }

    /// <summary>Sizing defaults/minimums for <see cref="StateStore"/>. Null = defaults.</summary>
    public WindowStateOptions? StateOptions { get; init; }
}

/// <summary>
/// Named secondary windows, each on its OWN STA thread with its own message pump — ported from
/// the primary desktop sibling's secondary-window service, decomposed to its generic core (the
/// source interleaved profile config, session wiring, and theme loading; those belong to the
/// app's <see cref="SecondaryWindowOptions.CreateForm"/> factory). One window per name;
/// <see cref="Open"/> on an existing name ACTIVATES it instead of the source's close-and-recreate
/// (its login-window sibling proved the focus-existing shape; recreate churned visibly).
///
/// Threading: everything here marshals to the window's thread with non-blocking
/// <c>BeginInvoke</c> — the source's blocking <c>Invoke</c> from the IPC thread deadlocked the
/// UI during scope switches (measured). Window threads are background: an app exit never hangs
/// on a forgotten window; dispose (or <see cref="CloseAll"/>) closes them gracefully first so
/// geometry saves run.
/// </summary>
public sealed class SecondaryWindows : IDisposable
{
    private sealed class WindowEntry
    {
        public volatile Form? Form;
        public volatile bool CloseRequested;

        // Pre-handle intent, same mechanism as CloseRequested and for the same reason (P5.5 H2): the
        // marshal is a deliberate no-op before the handle exists, so an Activate that arrives while the
        // window thread is still starting up must be carried in a flag or it is silently lost — and
        // that is precisely the documented "Open on an existing name ACTIVATES it" path, which a user
        // hits by double-clicking a launcher.
        public volatile bool ActivateRequested;
    }

    private readonly ILogger<SecondaryWindows> _logger;
    private readonly ConcurrentDictionary<string, WindowEntry> _windows = new();
    private bool _disposed;

    public SecondaryWindows(ILogger<SecondaryWindows>? logger = null)
    {
        _logger = logger ?? NullLogger<SecondaryWindows>.Instance;
    }

    /// <summary>
    /// Open the named window on its own STA thread. Returns false (and activates the existing
    /// window) when the name is already open.
    /// </summary>
    public bool Open(string name, SecondaryWindowOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);

        var entry = new WindowEntry();
        if (!_windows.TryAdd(name, entry))
        {
            Activate(name);
            return false;
        }

        var thread = new Thread(() => RunWindow(name, entry, options))
        {
            Name = $"SecondaryWindow:{name}",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            // A failed Start (thread exhaustion) used to leave the entry behind FOREVER, and nothing
            // else can remove it: RunWindow — the only other place that cleans up — never ran. The name
            // was then permanently "already open", so every later Open answered false and tried to
            // activate a window that does not exist (P5.5 H2).
            _windows.TryRemove(name, out _);
            _logger.LogError(ex, "Secondary window '{Name}' could not start its thread", name);
            throw;
        }
        return true;
    }

    private void RunWindow(string name, WindowEntry entry, SecondaryWindowOptions options)
    {
        Form form;
        try
        {
            form = options.CreateForm();

            if (options.StateStore is { } store)
            {
                // AttachTo owns the apply-before-show / save-on-closed ordering (P5.5 H4.5).
                new WindowStateManager(store, options.StateOptions).AttachTo(form);
            }

            // NO FormClosed removal here (removed in P5.5 H2). It used to drop the entry the instant
            // FormClosed fired — but Application.Run has NOT returned yet at that point: the form is
            // still tearing itself and its child controls down. Dispose() waits on _windows becoming
            // empty, so it saw "empty" mid-teardown, returned, and let the process exit while a
            // WebView2 child was still shutting down — which leaves its user-data folder LOCKED and
            // makes the next launch's init hang. The `finally` after Application.Run is the only
            // correct removal point, and it already covers every path.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Secondary window '{Name}' failed to create", name);
            _windows.TryRemove(name, out _);
            return;
        }

        entry.Form = form;
        // A Close(name) that raced window creation lands here instead of being lost…
        if (entry.CloseRequested)
        {
            form.Dispose();
            _windows.TryRemove(name, out _);
            return;
        }
        // …and one that lands between this check and the pump creating the handle lands here
        // (Post is a deliberate no-op pre-handle — see below).
        form.HandleCreated += (_, _) =>
        {
            if (entry.CloseRequested) form.BeginInvoke(form.Close);
            // An Activate that arrived before the handle existed was dropped by the marshal; replay it
            // now (P5.5 H2). Close wins if both are pending — there is no point focusing a window that
            // is on its way out.
            else if (entry.ActivateRequested)
            {
                entry.ActivateRequested = false;
                form.BeginInvoke(() => WindowActivation.BringToFront(form));
            }
        };

        _logger.LogDebug("Secondary window '{Name}' opened on thread {Thread}", name, Environment.CurrentManagedThreadId);
        try
        {
            Application.Run(form); // this thread's own pump — returns when the form closes
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Secondary window '{Name}' pump faulted", name);
        }
        finally
        {
            _windows.TryRemove(name, out _);
            _logger.LogDebug("Secondary window '{Name}' closed", name);
        }
    }

    /// <summary>True while the named window is open (or opening).</summary>
    public bool HasWindow(string name) => _windows.ContainsKey(name);

    /// <summary>
    /// Bring the named window to the front (no-op when it isn't open). Survives being called while the
    /// window is still opening: the request is recorded and replayed once the handle exists, because
    /// the marshal cannot deliver anything before then and this IS the "<see cref="Open"/> on an
    /// existing name activates it" path.
    /// </summary>
    public void Activate(string name)
    {
        if (!_windows.TryGetValue(name, out var entry)) return;

        // Set the flag FIRST, unconditionally: the form may not exist yet (the window thread is still
        // in CreateForm), and even when it does the Post below is a no-op until the handle is created.
        // HandleCreated replays it. Cleared here on the success path so it can't fire twice.
        entry.ActivateRequested = true;
        if (entry.Form is not { } form) return;
        if (Post(form, () => WindowActivation.BringToFront(form)))   // one owner (P5.5 H4.5)
            entry.ActivateRequested = false;
    }

    /// <summary>Close the named window (non-blocking; safe from any thread).</summary>
    public void Close(string name)
    {
        if (!_windows.TryGetValue(name, out var entry)) return;
        entry.CloseRequested = true;
        if (entry.Form is { } form) Post(form, form.Close);
    }

    /// <summary>Close every window.</summary>
    public void CloseAll()
    {
        foreach (var name in _windows.Keys.ToArray()) Close(name);
    }

    /// <summary>
    /// Close every window and WAIT (bounded) for their pumps to finish — the window threads are
    /// background, so an unwaited dispose at app exit would kill them before their
    /// FormClosed-driven geometry saves ran (found in review). The wait is bounded so a wedged
    /// window can never hang shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseAll();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!_windows.IsEmpty && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
    }

    internal int WindowCount => _windows.Count;

    internal Form? TryGetForm(string name) =>
        _windows.TryGetValue(name, out var entry) ? entry.Form : null;

    // Non-blocking marshal to the window's own thread, through the ONE owner (P5.5 H4.2) — a
    // blocking Invoke from the IPC thread deadlocked the source app during scope switches.
    // Pre-handle this stays a deliberate NO-OP, which is exactly what the dispatcher's `false`
    // return means here: the caller is never the window's own thread, so running inline would
    // CREATE the handle on the wrong thread and kill the pump (found in review). Pre-handle intent
    // is carried by flags instead (CloseRequested + the HandleCreated re-check) — see Open/Close.
    private static bool Post(Form form, Action action) => new WinFormsUiDispatcher(form).Post(action);
}
